using System.Diagnostics;
using System.Text;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public sealed class OpenSshHostKeyProbe : ISshHostKeyProbe
{
    private const int MaximumDiagnosticCharacters = 64 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private readonly ILftpRuntimeProvider _runtimeProvider;
    private readonly string _temporaryRoot;
    private readonly TimeSpan _timeout;

    public OpenSshHostKeyProbe(
        ILftpRuntimeProvider runtimeProvider,
        string temporaryRoot,
        TimeSpan? timeout = null)
    {
        _runtimeProvider = runtimeProvider ?? throw new ArgumentNullException(nameof(runtimeProvider));
        if (string.IsNullOrWhiteSpace(temporaryRoot) || !Path.IsPathFullyQualified(temporaryRoot))
            throw new ArgumentException("A fully qualified SSH host-key probe temporary root is required.", nameof(temporaryRoot));
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        if (_temporaryRoot.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            _temporaryRoot.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The SSH host-key probe temporary root cannot use a device namespace.", nameof(temporaryRoot));
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout), "The SSH host-key probe timeout must be between zero and two minutes.");
    }

    public async Task<TrustedSftpHostKey> ProbeAsync(
        ConnectionProfile profile,
        string hostKeyAlias,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var binding = SftpHostKeyIdentity.CreateBinding(profile);
        SftpHostKeyIdentity.ValidateAlias(hostKeyAlias, nameof(hostKeyAlias));

        var runtime = await _runtimeProvider.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!runtime.IsAuthenticated && !runtime.IsTestOverride)
            throw new InvalidDataException("The SSH host-key probe requires an authenticated runtime.");
        var sshPath = Path.GetFullPath(Path.Combine(runtime.BinaryDirectory, "ssh.exe"));
        if (!File.Exists(sshPath)) throw new FileNotFoundException("The authenticated runtime is missing ssh.exe.", sshPath);
        if ((File.GetAttributes(sshPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The authenticated SSH executable cannot be a reparse point.");

        var probeDirectory = CreateProbeDirectory();
        var knownHostsPath = Path.Combine(probeDirectory, "proposed_known_hosts");
        ProcessStartInfo startInfo;
        try
        {
            await using (var created = new FileStream(
                knownHostsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough))
            {
                created.Flush(flushToDisk: true);
            }
            startInfo = CreateStartInfo(runtime, sshPath, probeDirectory, knownHostsPath, profile, hostKeyAlias);
        }
        catch
        {
            DeleteProbeFiles(probeDirectory, knownHostsPath);
            throw;
        }
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        WindowsJobObject? job = null;
        Task? stdoutDrain = null;
        Task? stderrDrain = null;
        var stdoutState = new DiagnosticDrainState();
        var stderrState = new DiagnosticDrainState();
        var processStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("The SSH host-key probe process did not start.");
            processStarted = true;
            job = new WindowsJobObject();
            job.Assign(process);
            stdoutDrain = DrainBoundedAsync(process.StandardOutput, stdoutState, cancellationToken);
            stderrDrain = DrainBoundedAsync(process.StandardError, stderrState, cancellationToken);

            var startedAt = Stopwatch.GetTimestamp();
            InvalidDataException? lastParseError = null;
            while (Stopwatch.GetElapsedTime(startedAt) < _timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(knownHostsPath))
                {
                    try
                    {
                        var proposal = await ReadProposalAsync(knownHostsPath, cancellationToken).ConfigureAwait(false);
                        var trusted = SshKnownHostsParser.Parse(proposal, binding, hostKeyAlias);
                        if (stdoutState.Exceeded || stderrState.Exceeded)
                            throw new InvalidDataException("The SSH host-key probe produced excessive diagnostics.");
                        return trusted;
                    }
                    catch (IOException) when (!process.HasExited)
                    {
                    }
                    catch (InvalidDataException exception) when (!process.HasExited)
                    {
                        lastParseError = exception;
                    }
                }

                if (process.HasExited)
                {
                    if (lastParseError is not null)
                        throw new InvalidDataException("The SSH host-key probe produced a malformed proposal.", lastParseError);
                    throw new InvalidOperationException("The SSH host-key probe did not produce a host key.");
                }
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The SSH host-key probe timed out before receiving a host key.");
        }
        finally
        {
            if (job is not null)
            {
                try { job.Terminate(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                job.Dispose();
            }
            if (processStarted && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }

            if (processStarted)
            {
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
                catch (TimeoutException) { }
            }
            if (stdoutDrain is not null || stderrDrain is not null)
            {
                try
                {
                    await Task.WhenAll(new[] { stdoutDrain, stderrDrain }.Where(static task => task is not null).Cast<Task>())
                        .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            DeleteProbeFiles(probeDirectory, knownHostsPath);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        LftpRuntimeDescriptor runtime,
        string sshPath,
        string probeDirectory,
        string knownHostsPath,
        ConnectionProfile profile,
        string hostKeyAlias)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sshPath,
            WorkingDirectory = runtime.RuntimeRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in BuildArguments(profile, hostKeyAlias, LftpCommandBuilder.ToMsysPath(knownHostsPath)))
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["HOME"] = probeDirectory;
        startInfo.Environment["TMP"] = probeDirectory;
        startInfo.Environment["TEMP"] = probeDirectory;
        startInfo.Environment["LANG"] = "C.UTF-8";
        startInfo.Environment["LC_ALL"] = "C.UTF-8";
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["MSYS"] = "disable_pcon";
        startInfo.Environment["CYGWIN"] = "disable_pcon";
        startInfo.Environment["MSYS2_PATH_TYPE"] = "inherit";
        startInfo.Environment["CHERE_INVOKING"] = "1";
        startInfo.Environment["PATH"] = runtime.BinaryDirectory;
        startInfo.Environment.Remove("SSH_AUTH_SOCK");
        return startInfo;
    }

    internal static IReadOnlyList<string> BuildArguments(
        ConnectionProfile profile,
        string hostKeyAlias,
        string knownHostsPath)
    {
        _ = SftpHostKeyIdentity.CreateBinding(profile);
        SftpHostKeyIdentity.ValidateAlias(hostKeyAlias, nameof(hostKeyAlias));
        if (string.IsNullOrWhiteSpace(knownHostsPath) || knownHostsPath.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("A bounded known-hosts path is required.", nameof(knownHostsPath));

        var host = profile.Host.Trim();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
        return
        [
            "-F", "none", "-a", "-x", "-n", "-N",
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", SftpHostKeyIdentity.CreateUserKnownHostsOption(knownHostsPath),
            "-o", "GlobalKnownHostsFile=none",
            "-o", "HashKnownHosts=no",
            "-o", "UpdateHostKeys=no",
            "-o", "VerifyHostKeyDNS=no",
            "-o", "CheckHostIP=no",
            "-o", "PubkeyAuthentication=no",
            "-o", "PasswordAuthentication=no",
            "-o", "KbdInteractiveAuthentication=no",
            "-o", "GSSAPIAuthentication=no",
            "-o", "HostbasedAuthentication=no",
            "-o", "IdentityAgent=none",
            "-o", "IdentitiesOnly=yes",
            "-o", "NumberOfPasswordPrompts=0",
            "-o", "ConnectionAttempts=1",
            "-o", "ConnectTimeout=15",
            "-o", $"HostKeyAlias={hostKeyAlias}",
            "-p", profile.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-l", "lftp-pilot-host-key-probe",
            host,
        ];
    }

    private string CreateProbeDirectory()
    {
        ValidateNoReparseAncestors(_temporaryRoot);
        Directory.CreateDirectory(_temporaryRoot);
        ValidateNoReparseAncestors(_temporaryRoot);
        var directory = Path.Combine(_temporaryRoot, $"host-key-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        ValidateNoReparseAncestors(directory);
        return directory;
    }

    private static void ValidateNoReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The SSH host-key probe root cannot contain a reparse point.");
        }
    }

    private static async Task<string> ReadProposalAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > SshKnownHostsParser.MaximumDocumentCharacters)
            throw new InvalidDataException("The SSH host-key proposal has an invalid size.");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The SSH host-key proposal cannot be a reparse point.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        string value;
        try
        {
            value = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The SSH host-key proposal is not valid UTF-8.", exception);
        }
        if (value.Length > SshKnownHostsParser.MaximumDocumentCharacters)
            throw new InvalidDataException("The SSH host-key proposal is too large.");
        return value;
    }

    private static async Task DrainBoundedAsync(
        StreamReader reader,
        DiagnosticDrainState state,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var total = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > MaximumDiagnosticCharacters)
            {
                state.Exceeded = true;
            }
        }
    }

    private static void DeleteProbeFiles(string directory, string knownHostsPath)
    {
        try { if (File.Exists(knownHostsPath)) File.Delete(knownHostsPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            var oldPath = knownHostsPath + ".old";
            if (File.Exists(oldPath)) File.Delete(oldPath);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class DiagnosticDrainState
    {
        public volatile bool Exceeded;
    }
}
