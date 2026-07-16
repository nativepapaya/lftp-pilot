using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class LftpRemoteEditTransportTests
{
    private static readonly DateTimeOffset BaselineTime = new(
        2026,
        7,
        15,
        12,
        34,
        0,
        TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 7, 15, 12, 34, 0)));

    [Fact]
    public async Task StrongIdentityDetectsSameSizeAndTimestampContentChange()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.Set("/note.txt", "aaaaaaaa", BaselineTime);
        var baseline = Assert.IsType<RemoteFileIdentity>(await harness.Transport.StatAsync(
            harness.SessionId,
            "/note.txt",
            TestContext.Current.CancellationToken));

        harness.Host.Set("/note.txt", "bbbbbbbb", BaselineTime);
        var changed = Assert.IsType<RemoteFileIdentity>(await harness.Transport.StatAsync(
            harness.SessionId,
            "/note.txt",
            TestContext.Current.CancellationToken));

        Assert.Equal(baseline.Size, changed.Size);
        Assert.Equal(baseline.ModifiedAt, changed.ModifiedAt);
        Assert.NotEqual(baseline.ContentSha256, changed.ContentSha256);
        Assert.All(
            harness.Host.Commands.Where(static command => command.Contains("note.txt", StringComparison.Ordinal) && command.Contains(" -ldB ", StringComparison.Ordinal)),
            static command => Assert.StartsWith("recls ", command, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanEmptyMissingListingIsTreatedAsAbsent()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.StatResultForPath = static path => path == "/missing.txt" ? new([]) : null;

        var identity = await harness.Transport.StatAsync(
            harness.SessionId,
            "/missing.txt",
            TestContext.Current.CancellationToken);

        Assert.Null(identity);
    }

    [Fact]
    public async Task ExactPathBoundFtp550MissingDiagnosticIsTreatedAsAbsent()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.StatResultForPath = static path => path == "/missing.txt"
            ? new([new("stderr", $"Access failed: 550 No such file or directory. ({path})")])
            : null;

        var identity = await harness.Transport.StatAsync(
            harness.SessionId,
            "/missing.txt",
            TestContext.Current.CancellationToken);

        Assert.Null(identity);
    }

    [Fact]
    public async Task SoleCommandPrefixedPathlessMissingDiagnosticIsTreatedAsAbsent()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.StatResultForPath = static path => path == "/missing.txt"
            ? new([new("stderr", "cls: Access failed: No such file")])
            : null;

        var identity = await harness.Transport.StatAsync(
            harness.SessionId,
            "/missing.txt",
            TestContext.Current.CancellationToken);

        Assert.Null(identity);
    }

    [Fact]
    public async Task CommandPrefixedPathBoundMissingDiagnosticRequiresTheRequestedPath()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.StatResultForPath = static path => path switch
        {
            "/missing.txt" => new([new("stderr", "recls: Access failed: 550 No such file or directory. (/missing.txt)")]),
            "/requested.txt" => new([new("stderr", "recls: Access failed: 550 No such file or directory. (/different.txt)")]),
            _ => null,
        };

        Assert.Null(await harness.Transport.StatAsync(
            harness.SessionId,
            "/missing.txt",
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.StatAsync(
            harness.SessionId,
            "/requested.txt",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ftp550MissingDiagnosticForDifferentPathFailsClosed()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.StatResultForPath = static path => path == "/requested.txt"
            ? new([new("stderr", "Access failed: 550 No such file or directory. (/different.txt)")])
            : null;

        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.StatAsync(
            harness.SessionId,
            "/requested.txt",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoisyMalformedAndFailingMissingListingsFailClosed()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.StatResultForPath = static path => path switch
        {
            "/stdout-noise.txt" => new([new("stdout", "unexpected output")]),
            "/stderr-noise.txt" => new([new("stderr", "server emitted an unclassified warning")]),
            "/blank.txt" => new([new("stdout", string.Empty)]),
            "/multiple.txt" => new([new("stdout", "first"), new("stdout", "second")]),
            "/mixed-missing.txt" => new([
                new("stderr", "cls: Access failed: No such file"),
                new("stdout", "-rw-r--r-- 1 alice staff 12 2026-07-15 12:34 mixed-missing.txt"),
            ]),
            "/truncated.txt" => new([], Truncated: true),
            "/failure.txt" => new([], Failure: "simulated transport failure"),
            _ => null,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.StatAsync(
            harness.SessionId, "/stdout-noise.txt", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.StatAsync(
            harness.SessionId, "/stderr-noise.txt", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.StatAsync(
            harness.SessionId, "/blank.txt", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.StatAsync(
            harness.SessionId, "/multiple.txt", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.StatAsync(
            harness.SessionId, "/mixed-missing.txt", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.StatAsync(
            harness.SessionId, "/truncated.txt", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => harness.Transport.StatAsync(
            harness.SessionId, "/failure.txt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TargetChangeAfterStagingIsPreservedAndNotPromoted()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.Set("/note.txt", "original", BaselineTime);
        var reviewed = await harness.StatRequiredAsync("/note.txt");
        var localPath = await harness.WriteLocalAsync("mine-now");
        var changed = false;
        harness.Host.AfterCommand = (host, command) =>
        {
            var arguments = RemoteFileHost.QuotedArguments(command);
            if (!changed && command.StartsWith("put ", StringComparison.Ordinal) && arguments[1].EndsWith(".upload", StringComparison.Ordinal))
            {
                changed = true;
                host.Set("/note.txt", "intruder", BaselineTime);
            }
        };

        var result = await harness.Transport.CommitUploadAsync(
            harness.SessionId,
            localPath,
            "/note.txt",
            reviewed,
            TestContext.Current.CancellationToken);

        Assert.False(result.Committed);
        Assert.Equal("intruder", harness.Host.Text("/note.txt"));
        Assert.DoesNotContain(harness.Host.Paths, static path => path.EndsWith(".upload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TargetThatAppearsAfterMissingReviewIsPreserved()
    {
        await using var harness = await Harness.CreateAsync();
        var localPath = await harness.WriteLocalAsync("mine-now");
        var appeared = false;
        harness.Host.AfterCommand = (host, command) =>
        {
            var arguments = RemoteFileHost.QuotedArguments(command);
            if (!appeared && command.StartsWith("put ", StringComparison.Ordinal) && arguments[1].EndsWith(".upload", StringComparison.Ordinal))
            {
                appeared = true;
                host.Set("/new.txt", "new-user", BaselineTime);
            }
        };

        var result = await harness.Transport.CommitUploadAsync(
            harness.SessionId,
            localPath,
            "/new.txt",
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Committed);
        Assert.Equal("new-user", harness.Host.Text("/new.txt"));
        Assert.DoesNotContain(harness.Host.Paths, static path => path.EndsWith(".upload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StagingUploadFailureLeavesOriginalTargetUntouched()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.Set("/note.txt", "original", BaselineTime);
        var reviewed = await harness.StatRequiredAsync("/note.txt");
        var localPath = await harness.WriteLocalAsync("mine-now");
        harness.Host.FailureForCommand = static command => command.StartsWith("put ", StringComparison.Ordinal)
            ? "simulated upload failure"
            : null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Transport.CommitUploadAsync(
            harness.SessionId,
            localPath,
            "/note.txt",
            reviewed,
            TestContext.Current.CancellationToken));

        Assert.Equal("original", harness.Host.Text("/note.txt"));
        Assert.DoesNotContain(harness.Host.Commands, static command => command.StartsWith("put -e ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackupMismatchAbortsAndRestoresConcurrentVersion()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.Set("/note.txt", "original", BaselineTime);
        var reviewed = await harness.StatRequiredAsync("/note.txt");
        var localPath = await harness.WriteLocalAsync("mine-now");
        var raced = false;
        harness.Host.BeforeCommand = (host, command) =>
        {
            var arguments = RemoteFileHost.QuotedArguments(command);
            if (!raced && command.StartsWith("mv ", StringComparison.Ordinal) &&
                arguments[0] == "/note.txt" && arguments[1].EndsWith(".backup", StringComparison.Ordinal))
            {
                raced = true;
                host.Set("/note.txt", "intruder", BaselineTime);
            }
        };

        var result = await harness.Transport.CommitUploadAsync(
            harness.SessionId,
            localPath,
            "/note.txt",
            reviewed,
            TestContext.Current.CancellationToken);

        Assert.False(result.Committed);
        Assert.Equal("intruder", harness.Host.Text("/note.txt"));
        Assert.DoesNotContain(harness.Host.Paths, static path => path.EndsWith(".backup", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Host.Paths, static path => path.EndsWith(".upload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromotionFailureRollsBackVerifiedOriginal()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.Set("/note.txt", "original", BaselineTime);
        var reviewed = await harness.StatRequiredAsync("/note.txt");
        var localPath = await harness.WriteLocalAsync("mine-now");
        harness.Host.FailureForCommand = static command =>
        {
            var arguments = RemoteFileHost.QuotedArguments(command);
            return command.StartsWith("mv ", StringComparison.Ordinal) &&
                arguments[0].EndsWith(".upload", StringComparison.Ordinal) && arguments[1] == "/note.txt"
                ? "simulated promotion failure"
                : null;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Transport.CommitUploadAsync(
            harness.SessionId,
            localPath,
            "/note.txt",
            reviewed,
            TestContext.Current.CancellationToken));

        Assert.Equal("original", harness.Host.Text("/note.txt"));
        Assert.DoesNotContain(harness.Host.Paths, static path => path.EndsWith(".backup", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Host.Paths, static path => path.EndsWith(".upload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromotionVerificationMismatchQuarantinesFailedVersionAndRestoresOriginal()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Host.Set("/note.txt", "original", BaselineTime);
        var reviewed = await harness.StatRequiredAsync("/note.txt");
        var localPath = await harness.WriteLocalAsync("mine-now");
        var corrupted = false;
        harness.Host.AfterCommand = (host, command) =>
        {
            var arguments = RemoteFileHost.QuotedArguments(command);
            if (!corrupted && command.StartsWith("mv ", StringComparison.Ordinal) &&
                arguments[0].EndsWith(".upload", StringComparison.Ordinal) && arguments[1] == "/note.txt")
            {
                corrupted = true;
                host.Set("/note.txt", "corrupt!", BaselineTime.AddMinutes(2));
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.CommitUploadAsync(
            harness.SessionId,
            localPath,
            "/note.txt",
            reviewed,
            TestContext.Current.CancellationToken));

        Assert.Equal("original", harness.Host.Text("/note.txt"));
        var failedPath = Assert.Single(harness.Host.Paths, static path => path.EndsWith(".failed", StringComparison.Ordinal));
        Assert.Equal("corrupt!", harness.Host.Text(failedPath));
        Assert.DoesNotContain(harness.Host.Paths, static path => path.EndsWith(".backup", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoteEditCommandsUseFreshListingAndNeverDeleteTargetBeforeUpload()
    {
        var stat = LftpCommandBuilder.BuildStat("/note.txt", fresh: true);
        var upload = LftpCommandBuilder.BuildRemoteEditUpload(@"C:\cache\content.txt", "/note.txt");

        Assert.StartsWith("recls -ldB ", stat, StringComparison.Ordinal);
        Assert.StartsWith("put ", upload, StringComparison.Ordinal);
        Assert.DoesNotContain("put -e ", upload, StringComparison.Ordinal);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SessionRegistry _sessions;

        private Harness(string root, RemoteFileHost host, SessionRegistry sessions, Guid sessionId, LftpRemoteEditTransport transport)
        {
            _root = root;
            Host = host;
            _sessions = sessions;
            SessionId = sessionId;
            Transport = transport;
        }

        public RemoteFileHost Host { get; }
        public Guid SessionId { get; }
        public LftpRemoteEditTransport Transport { get; }

        public static async Task<Harness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "LFTPPilot.RemoteEditTransportTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var host = new RemoteFileHost();
            var options = AgentWorkspaceOptions.CreateDefault(
                Path.Combine(root, "runtime"),
                Path.Combine(root, "cache"),
                Path.Combine(root, "temporary")) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                BrowseTimeout = TimeSpan.FromSeconds(2),
                TransferTimeout = TimeSpan.FromSeconds(2),
            };
            var sessions = new SessionRegistry(host, new FakeRuntimeProvider(), options);
            try
            {
                var profile = new ConnectionProfile(
                    Guid.NewGuid(),
                    "Remote-edit transport test",
                    ConnectionProtocol.Ftp,
                    "test.invalid",
                    21,
                    "anonymous",
                    AuthenticationKind.Anonymous);
                var session = await sessions.ConnectAsync(profile, null, TestContext.Current.CancellationToken);
                return new(root, host, sessions, session.SessionId, new(sessions, options));
            }
            catch
            {
                await sessions.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public Task<string> WriteLocalAsync(string content)
        {
            var path = Path.Combine(_root, $"local-{Guid.NewGuid():N}.txt");
            return WriteAsync(path, content);

            static async Task<string> WriteAsync(string path, string content)
            {
                await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
                return path;
            }
        }

        public async Task<RemoteFileIdentity> StatRequiredAsync(string path) =>
            await Transport.StatAsync(SessionId, path, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The test remote file was unexpectedly missing.");

        public async ValueTask DisposeAsync()
        {
            await _sessions.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeRuntimeProvider : ILftpRuntimeProvider
    {
        public Task<LftpRuntimeDescriptor> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LftpRuntimeDescriptor(
                @"C:\fake",
                @"C:\fake\bin\lftp.exe",
                @"C:\fake\bin",
                false,
                "test",
                true));
        }
    }

    private sealed class RemoteFileHost : ILftpProcessHost
    {
        private readonly Dictionary<string, RemoteState> _files = new(StringComparer.Ordinal);
        private int _nextProcessId = 1000;
        private long _writeVersion;

        public ConcurrentQueue<string> Commands { get; } = [];
        public Action<RemoteFileHost, string>? BeforeCommand { get; set; }
        public Action<RemoteFileHost, string>? AfterCommand { get; set; }
        public Func<string, string?>? FailureForCommand { get; set; }
        public Func<string, LftpCommandResult?>? StatResultForPath { get; set; }
        public IReadOnlyList<string> Paths => _files.Keys.ToArray();

        public Task<ILftpSession> StartAsync(LftpProcessStartOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ILftpSession>(new RemoteFileSession(this, Interlocked.Increment(ref _nextProcessId)));
        }

        public void Set(string path, string content, DateTimeOffset modifiedAt) =>
            _files[path] = new(System.Text.Encoding.UTF8.GetBytes(content), modifiedAt);

        public string Text(string path) => System.Text.Encoding.UTF8.GetString(_files[path].Content);

        public static string[] QuotedArguments(string command)
        {
            var arguments = new List<string>();
            for (var index = 0; index < command.Length; index++)
            {
                if (command[index] != '"') continue;
                var value = new System.Text.StringBuilder();
                for (index++; index < command.Length; index++)
                {
                    var character = command[index];
                    if (character == '"') break;
                    if (character == '\\' && index + 1 < command.Length && command[index + 1] is '\\' or '"')
                        character = command[++index];
                    value.Append(character);
                }
                arguments.Add(value.ToString());
            }
            return arguments.ToArray();
        }

        private LftpCommandResult Execute(string command)
        {
            Commands.Enqueue(command);
            BeforeCommand?.Invoke(this, command);
            if (FailureForCommand?.Invoke(command) is { } failure) return new([], Failure: failure);

            LftpCommandResult result;
            if (command.StartsWith("recls -ldB ", StringComparison.Ordinal) || command.StartsWith("cls -ldB ", StringComparison.Ordinal))
            {
                var path = QuotedArguments(command)[0];
                result = StatResultForPath?.Invoke(path) ?? (_files.TryGetValue(path, out var file)
                    ? new([new("stdout", Listing(path, file))])
                    : new([new("stderr", "cls: Access failed: No such file")]));
            }
            else if (command.StartsWith("get ", StringComparison.Ordinal))
            {
                var arguments = QuotedArguments(command);
                if (!_files.TryGetValue(arguments[0], out var file))
                    result = new([new("stderr", "get: Access failed: No such file")]);
                else
                {
                    var localPath = FromMsysPath(arguments[1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    File.WriteAllBytes(localPath, file.Content);
                    result = new([]);
                }
            }
            else if (command.StartsWith("put ", StringComparison.Ordinal))
            {
                var arguments = QuotedArguments(command);
                _files[arguments[1]] = new(
                    File.ReadAllBytes(FromMsysPath(arguments[0])),
                    BaselineTime.AddMinutes(10 + Interlocked.Increment(ref _writeVersion)));
                result = new([]);
            }
            else if (command.StartsWith("mv ", StringComparison.Ordinal))
            {
                var arguments = QuotedArguments(command);
                if (!_files.Remove(arguments[0], out var file))
                    result = new([new("stderr", "mv: Access failed: No such file")]);
                else
                {
                    _files[arguments[1]] = file;
                    result = new([]);
                }
            }
            else if (command.StartsWith("rm ", StringComparison.Ordinal))
            {
                var path = QuotedArguments(command)[0];
                result = _files.Remove(path)
                    ? new([])
                    : new([new("stderr", "rm: Access failed: No such file")]);
            }
            else
            {
                result = new([]);
            }

            AfterCommand?.Invoke(this, command);
            return result;
        }

        private static string Listing(string path, RemoteState file)
        {
            var separator = path.LastIndexOf('/');
            var name = separator >= 0 ? path[(separator + 1)..] : path;
            return $"-rw-r--r-- 1 alice staff {file.Content.LongLength} {file.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {name}";
        }

        private static string FromMsysPath(string path)
        {
            if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == '/')
                return $"{char.ToUpperInvariant(path[1])}:\\{path[3..].Replace('/', '\\')}";
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        private sealed record RemoteState(byte[] Content, DateTimeOffset ModifiedAt);

        private sealed class RemoteFileSession(RemoteFileHost host, int processId) : ILftpSession
        {
            public int ProcessId { get; } = processId;
            public bool IsRunning { get; private set; } = true;
            public event EventHandler<LftpOutputLine>? OutputReceived;
            public event EventHandler<LftpOutputLine>? UnsolicitedOutput;

            public Task<LftpCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(IsRunning ? host.Execute(command) : new LftpCommandResult([], Failure: "stopped"));
            }

            public Task StopAsync(bool force = false, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IsRunning = false;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                IsRunning = false;
                return ValueTask.CompletedTask;
            }

#pragma warning disable CS0067
            // The stateful fake is command/reply only; it never emits unsolicited output.
            private void RetainInterfaceEvents() => _ = (OutputReceived, UnsolicitedOutput);
#pragma warning restore CS0067
        }
    }
}
