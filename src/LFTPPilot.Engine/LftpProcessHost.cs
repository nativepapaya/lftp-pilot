using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public sealed class LftpProcessHost : ILftpProcessHost
{
    public async Task<ILftpSession> StartAsync(LftpProcessStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.ExecutablePath)) throw new ArgumentException("The LFTP executable path must be fully qualified.", nameof(options));
        if (!File.Exists(options.ExecutablePath)) throw new FileNotFoundException("The LFTP executable was not found.", options.ExecutablePath);
        if (!Directory.Exists(options.WorkingDirectory)) throw new DirectoryNotFoundException(options.WorkingDirectory);
        var session = new RedirectedLftpSession(options);
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed partial class RedirectedLftpSession : ILftpSession
{
    private const int MaximumLineCharacters = 256 * 1024;
    private const int MaximumCaptureLines = 100_000;
    private const int MaximumCaptureCharacters = 32 * 1024 * 1024;
    private const string LineTruncationSuffix = "... [line truncated]";
    private static readonly TimeSpan StderrDrainWindow = TimeSpan.FromMilliseconds(60);
    private readonly LftpProcessStartOptions _options;
    private readonly Process _process;
    private readonly SecretRedactor _redactor;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _captureGate = new();
    private readonly string _markerPrefix;
    private WindowsJobObject? _job;
    private CommandCapture? _current;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _exitTask;
    private long _sequence;
    private bool _disposed;

    public RedirectedLftpSession(LftpProcessStartOptions options)
    {
        _options = options;
        _redactor = new(options.Secrets);
        _markerPrefix = $"__LFTPPILOT_SYNC__{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}_";
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in options.Arguments ?? []) startInfo.ArgumentList.Add(argument);
        foreach (var pair in options.Environment ?? new Dictionary<string, string?>())
        {
            if (pair.Value is null) startInfo.Environment.Remove(pair.Key);
            else startInfo.Environment[pair.Key] = pair.Value;
        }
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    public int ProcessId => _process.HasExited ? -1 : _process.Id;
    public bool IsRunning => !_disposed && _process is { HasExited: false };
    public event EventHandler<LftpOutputLine>? OutputReceived;
    public event EventHandler<LftpOutputLine>? UnsolicitedOutput;

    internal Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_process.Start()) throw new InvalidOperationException("The LFTP process did not start.");
        try
        {
            _job = new WindowsJobObject();
            _job.Assign(_process);
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        _stdoutTask = ReadLinesAsync(_process.StandardOutput, "stdout", _lifetime.Token);
        _stderrTask = ReadLinesAsync(_process.StandardError, "stderr", _lifetime.Token);
        _exitTask = ObserveExitAsync();
        return Task.CompletedTask;
    }

    public async Task<LftpCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("An LFTP command is required.", nameof(command));
        if (command.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Only one LFTP command line may be executed.", nameof(command));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning) return EmptyFailure("The LFTP session is not running.");
            var marker = _markerPrefix + Interlocked.Increment(ref _sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var capture = new CommandCapture(marker);
            lock (_captureGate) _current = capture;
            try
            {
                await _process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.WriteLineAsync($"echo {marker}".AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                var markerSeen = await capture.MarkerSeen.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (!markerSeen) return capture.ToResult(_redactor, failure: "The LFTP process exited before completing the command.");
                await Task.Delay(StderrDrainWindow, cancellationToken).ConfigureAwait(false);
                return capture.ToResult(_redactor);
            }
            catch (TimeoutException)
            {
                await RetireAsync().ConfigureAwait(false);
                return capture.ToResult(_redactor, timedOut: true, failure: "The LFTP command timed out; the session was retired to prevent late output attribution.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RetireAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                lock (_captureGate)
                {
                    if (ReferenceEquals(_current, capture)) _current = null;
                }
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<LftpCommandResult> ExecuteToExitAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("An LFTP command is required.", nameof(command));
        if (command.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Only one LFTP command line may be executed.", nameof(command));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning) return EmptyFailure("The LFTP session is not running.");
            var capture = new CommandCapture(marker: null);
            lock (_captureGate) _current = capture;
            try
            {
                Task<int>? completion = null;
                try
                {
                    await _process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await _process.StandardInput.WriteLineAsync("exit top kill".AsMemory(), cancellationToken).ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

                    completion = WaitForExitAndReadersAsync();
                    var exitCode = await completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                    var failure = exitCode == 0
                        ? null
                        : $"The LFTP process exited with code {exitCode}.";
                    return DetachCapture(capture, failure: failure);
                }
                catch (TimeoutException)
                {
                    await RetireAndObserveAsync(completion!).ConfigureAwait(false);
                    return DetachCapture(
                        capture,
                        timedOut: true,
                        failure: "The LFTP command timed out; the one-shot session was retired before returning its buffered output.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (completion is null) await RetireAsync().ConfigureAwait(false);
                    else await RetireAndObserveAsync(completion).ConfigureAwait(false);
                    _ = DetachCapture(capture);
                    throw;
                }
            }
            finally
            {
                lock (_captureGate)
                {
                    if (ReferenceEquals(_current, capture)) _current = null;
                }
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task StopAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (_disposed || _process.HasExited) return;
        if (!force)
        {
            try
            {
                await _process.StandardInput.WriteLineAsync("exit kill".AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException) { }
            catch (IOException) { }
        }
        await RetireAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync(force: true).ConfigureAwait(false);
        _disposed = true;
        _lifetime.Cancel();
        var tasks = new[] { _stdoutTask, _stderrTask, _exitTask }.Where(static task => task is not null).Cast<Task>();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (OperationCanceledException) { } catch (TimeoutException) { }
        _job?.Dispose();
        _process.Dispose();
        _commandGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ReadLinesAsync(StreamReader reader, string stream, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var discarding = false;
        var pendingCarriageReturn = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                for (var index = 0; index < read; index++)
                {
                    var current = buffer[index];
                    if (current == '\n')
                    {
                        if (!pendingCarriageReturn) EmitBufferedLine(stream, line, discarding);
                        pendingCarriageReturn = false;
                        line.Clear();
                        discarding = false;
                        continue;
                    }
                    if (current == '\r')
                    {
                        EmitBufferedLine(stream, line, discarding);
                        pendingCarriageReturn = true;
                        line.Clear();
                        discarding = false;
                        continue;
                    }
                    pendingCarriageReturn = false;
                    if (!discarding)
                    {
                        if (line.Length < MaximumLineCharacters) line.Append(current);
                        else discarding = true;
                    }
                }
            }
            if (line.Length != 0 || discarding) EmitBufferedLine(stream, line, discarding);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
    }

    private void EmitBufferedLine(string stream, StringBuilder builder, bool truncated)
    {
        var line = AnsiRegex().Replace(builder.ToString(), string.Empty);
        if (truncated) line += LineTruncationSuffix;
        OnLine(new(stream, line));
    }

    private void OnLine(LftpOutputLine line)
    {
        if (line.Line.Length == 0) return;
        var unsolicited = false;
        lock (_captureGate)
        {
            if (_current is { } capture)
            {
                if (capture.Marker is not null && line.Stream == "stdout" && string.Equals(line.Line, capture.Marker, StringComparison.Ordinal))
                {
                    capture.MarkerSeen.TrySetResult(true);
                    return;
                }
                capture.Add(line);
            }
            else unsolicited = true;
        }
        var redacted = line with { Line = _redactor.Redact(line.Line) };
        OutputReceived?.Invoke(this, redacted);
        if (unsolicited) UnsolicitedOutput?.Invoke(this, redacted);
    }

    private async Task ObserveExitAsync()
    {
        try { await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        lock (_captureGate) _current?.MarkerSeen.TrySetResult(false);
    }

    private async Task RetireAsync()
    {
        if (_process.HasExited) return;
        try { _job?.Terminate(); }
        catch (Win32Exception) { try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
    }

    private async Task<int> WaitForExitAndReadersAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        var readers = new[] { _stdoutTask, _stderrTask }
            .Where(static task => task is not null)
            .Cast<Task>();
        await Task.WhenAll(readers).ConfigureAwait(false);
        return _process.ExitCode;
    }

    private async Task RetireAndObserveAsync(Task completion)
    {
        await RetireAsync().ConfigureAwait(false);
        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or InvalidOperationException)
        {
            // The result already reports timeout or caller cancellation. This wait
            // only drains and observes the process/readers after forced retirement.
        }
    }

    private LftpCommandResult DetachCapture(
        CommandCapture capture,
        bool timedOut = false,
        string? failure = null)
    {
        lock (_captureGate)
        {
            if (ReferenceEquals(_current, capture)) _current = null;
            return capture.ToResult(_redactor, timedOut, failure);
        }
    }

    private static LftpCommandResult EmptyFailure(string failure) => new([], Failure: failure);

    [GeneratedRegex("\\x1b\\[[0-9;?]*[A-Za-z]|\\x1b\\][^\\x07]*\\x07", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiRegex();

    private sealed class CommandCapture(string? marker)
    {
        private readonly List<LftpOutputLine> _lines = [];
        private int _characters;
        private bool _truncated;

        public string? Marker { get; } = marker;
        public TaskCompletionSource<bool> MarkerSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(LftpOutputLine line)
        {
            if (line.Line.EndsWith(LineTruncationSuffix, StringComparison.Ordinal)) _truncated = true;
            if (_lines.Count >= MaximumCaptureLines || _characters + line.Line.Length > MaximumCaptureCharacters)
            {
                _truncated = true;
                return;
            }
            _lines.Add(line);
            _characters += line.Line.Length;
        }

        public LftpCommandResult ToResult(SecretRedactor redactor, bool timedOut = false, string? failure = null) =>
            new(_lines.Select(line => line with { Line = redactor.Redact(line.Line) }).ToImmutableArray(), timedOut, _truncated, failure);
    }
}
