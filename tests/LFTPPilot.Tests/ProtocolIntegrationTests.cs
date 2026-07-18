using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class ProtocolIntegrationTests
{
    private const string ConfigVariable = "LFTP_PILOT_PROTOCOL_LAB_CONFIG";
    private const string RuntimeVariable = "LFTP_PILOT_PROTOCOL_LAB_LFTP";

    [Fact]
    [Trait("Category", "ProtocolIntegration")]
    public async Task PasswordEndpointsBrowseMutateAndRoundTripUnicodeTransfers()
    {
        var configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        var executablePath = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(executablePath)) return;

        var config = JsonSerializer.Deserialize<ProtocolLabConfig>(
            await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The controlled protocol-lab configuration is empty.");
        Assert.True(Path.IsPathFullyQualified(executablePath));
        Assert.True(File.Exists(executablePath));

        var endpoints = new[]
        {
            (ConnectionProtocol.Ftp, config.Endpoints.Ftp),
            (ConnectionProtocol.FtpOpportunisticTls, config.Endpoints.FtpOpportunisticTls),
            (ConnectionProtocol.FtpsExplicit, config.Endpoints.FtpsExplicit),
            (ConnectionProtocol.FtpsImplicit, config.Endpoints.FtpsImplicit),
            (ConnectionProtocol.Sftp, config.Endpoints.Sftp),
        };
        foreach (var endpoint in endpoints)
        {
            try
            {
                await ExerciseEndpointAsync(config, executablePath, endpoint.Item1, endpoint.Item2);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"The controlled {endpoint.Item1} endpoint failed.", exception);
            }
        }
    }

    private static async Task ExerciseEndpointAsync(
        ProtocolLabConfig config,
        string executablePath,
        ConnectionProtocol protocol,
        int port)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lftp-pilot-protocol-{protocol}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new MemoryProfileStore();
            var secrets = new MemorySecretStore();
            var hostKeys = new MemoryHostKeyStore();
            var profile = new ConnectionProfile(
                Guid.NewGuid(),
                $"Loopback {protocol}",
                protocol,
                config.Host,
                port,
                config.Username,
                AuthenticationKind.Password,
                InitialRemotePath: "/",
                InitialLocalPath: root);
            if (protocol == ConnectionProtocol.Sftp)
            {
                var binding = SftpHostKeyManager.CreateBinding(profile);
                await hostKeys.SaveAsync(new(
                    binding,
                    config.SftpHostKeyAlgorithm,
                    config.SftpHostKeyBase64,
                    config.SftpHostKeyFingerprint),
                    TestContext.Current.CancellationToken);
            }

            var jobs = new JobCoordinator();
            var options = AgentWorkspaceOptions.CreateDefault(Path.Combine(root, "agent")) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(20),
                BrowseTimeout = TimeSpan.FromSeconds(20),
                TransferTimeout = TimeSpan.FromMinutes(2),
                MirrorPreviewTimeout = TimeSpan.FromSeconds(30),
                ConsoleTimeout = TimeSpan.FromSeconds(30),
            };
            await using var service = new AgentWorkspaceService(
                profiles,
                secrets,
                new SftpHostKeyManager(hostKeys, new UnexpectedHostKeyProbe()),
                new TlsCaProcessHost(new LftpProcessHost(), config.TlsCaPath),
                PackagedLftpRuntimeProvider.CreateTestOverride(executablePath),
                jobs,
                new MirrorPlanner(),
                options);

            if (protocol == ConnectionProtocol.Sftp)
                await service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
            await service.SaveProfileAsync(new(profile, config.Password), TestContext.Current.CancellationToken);
            var session = await service.ConnectAsync(
                new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
            var initial = await service.BrowseRemoteAsync(
                new(session.SessionId, "/", Fresh: true), TestContext.Current.CancellationToken);
            Assert.Contains(initial.Entries, entry => entry.Name == config.SeedName && !entry.IsDirectory);

            const string remoteDirectory = "/roundtrip-雪";
            const string uploadedPath = "/roundtrip-雪/upload-雪.bin";
            const string renamedPath = "/roundtrip-雪/renamed-雪.bin";
            await service.CreateDirectoryAsync(
                new(PaneKind.Remote, remoteDirectory, session.SessionId), TestContext.Current.CancellationToken);

            var payload = Enumerable.Range(0, 768 * 1024)
                .Select(index => (byte)((index * 31 + 17) % 251))
                .ToArray();
            var uploadSource = Path.Combine(root, "upload-雪.bin");
            await File.WriteAllBytesAsync(uploadSource, payload, TestContext.Current.CancellationToken);
            var upload = await service.EnqueueTransferAsync(new(
                session.SessionId,
                new TransferPlan(
                    Guid.NewGuid(), profile.Id, TransferDirection.Upload,
                    uploadSource, uploadedPath, TransferMode.Overwrite)),
                TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, upload.Job.Id, protocol);

            var remoteDirectoryListing = await service.BrowseRemoteAsync(
                new(session.SessionId, remoteDirectory, Fresh: true), TestContext.Current.CancellationToken);
            Assert.Contains(remoteDirectoryListing.Entries, entry => entry.Name == "upload-雪.bin");

            var downloadDirectory = Path.Combine(root, "download");
            Directory.CreateDirectory(downloadDirectory);
            var downloadTarget = Path.Combine(downloadDirectory, "download-雪.bin");
            var download = await service.EnqueueTransferAsync(new(
                session.SessionId,
                new TransferPlan(
                    Guid.NewGuid(), profile.Id, TransferDirection.Download,
                    uploadedPath, downloadTarget, TransferMode.Resume, Segments: 3)),
                TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, download.Job.Id, protocol);
            Assert.Equal(payload, await File.ReadAllBytesAsync(downloadTarget, TestContext.Current.CancellationToken));

            await service.MoveEntryAsync(
                new(PaneKind.Remote, uploadedPath, renamedPath, session.SessionId), TestContext.Current.CancellationToken);
            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [renamedPath], session.SessionId, Confirmed: true), TestContext.Current.CancellationToken);
            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [remoteDirectory], session.SessionId, Confirmed: true), TestContext.Current.CancellationToken);
            var final = await service.BrowseRemoteAsync(
                new(session.SessionId, "/", Fresh: true), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(final.Entries, entry => entry.Name == "roundtrip-雪");
            await service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForCompletedAsync(JobCoordinator jobs, Guid jobId, ConnectionProtocol protocol)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var job = jobs.GetJobs().Single(candidate => candidate.Id == jobId);
            if (job.State == JobState.Completed) return;
            if (job.State is JobState.Failed or JobState.Cancelled or JobState.Missed)
            {
                Assert.Fail($"{protocol} job {jobId} ended as {job.State}: {job.Error?.Message ?? job.Status}");
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"{protocol} job {jobId} did not complete within two minutes.");
    }

    private sealed record ProtocolLabConfig(
        string Host,
        string Username,
        string Password,
        [property: JsonPropertyName("seed_name")] string SeedName,
        [property: JsonPropertyName("tls_ca_path")] string TlsCaPath,
        [property: JsonPropertyName("sftp_host_key_algorithm")] string SftpHostKeyAlgorithm,
        [property: JsonPropertyName("sftp_host_key_base64")] string SftpHostKeyBase64,
        [property: JsonPropertyName("sftp_host_key_fingerprint")] string SftpHostKeyFingerprint,
        ProtocolLabEndpoints Endpoints);

    private sealed record ProtocolLabEndpoints(
        int Ftp,
        [property: JsonPropertyName("ftp_opportunistic_tls")] int FtpOpportunisticTls,
        [property: JsonPropertyName("ftps_explicit")] int FtpsExplicit,
        [property: JsonPropertyName("ftps_implicit")] int FtpsImplicit,
        int Sftp);

    private sealed class MemoryProfileStore : IProfileStore
    {
        private readonly ConcurrentDictionary<Guid, ConnectionProfile> _profiles = [];
        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(_profiles.Values.ToArray());
        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.TryRemove(profileId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<SecretBinding, string> _secrets = [];
        public Task SaveAsync(SecretValue secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets[secret.Binding] = secret.Value;
            return Task.CompletedTask;
        }
        public Task<string?> GetAsync(SecretBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_secrets.TryGetValue(binding, out var secret) ? secret : null);
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var binding in _secrets.Keys.Where(binding => binding.ProfileId == profileId))
                _secrets.TryRemove(binding, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryHostKeyStore : IHostKeyStore
    {
        private readonly ConcurrentDictionary<HostKeyBinding, TrustedSftpHostKey> _keys = [];
        public Task<TrustedSftpHostKey?> GetAsync(HostKeyBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_keys.TryGetValue(binding, out var key) ? key : null);
        }
        public Task SaveAsync(TrustedSftpHostKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _keys[key.Binding] = key;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var binding in _keys.Keys.Where(binding => binding.ProfileId == profileId))
                _keys.TryRemove(binding, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class UnexpectedHostKeyProbe : ISshHostKeyProbe
    {
        public Task<TrustedSftpHostKey> ProbeAsync(
            ConnectionProfile profile,
            string hostKeyAlias,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TrustedSftpHostKey>(new InvalidOperationException(
                $"The pre-trusted loopback profile {profile.Id} unexpectedly probed {hostKeyAlias}."));
    }

    private sealed class TlsCaProcessHost(ILftpProcessHost inner, string caPath) : ILftpProcessHost
    {
        private readonly string _preamble = $"set ssl:ca-file {LftpCommandBuilder.Quote(LftpCommandBuilder.ToMsysPath(caPath))}";

        public async Task<ILftpSession> StartAsync(
            LftpProcessStartOptions options,
            CancellationToken cancellationToken = default) =>
            new TlsCaSession(await inner.StartAsync(options, cancellationToken), _preamble);
    }

    private sealed class TlsCaSession : ILftpSession
    {
        private readonly ILftpSession _inner;
        private readonly string _preamble;

        public TlsCaSession(ILftpSession inner, string preamble)
        {
            _inner = inner;
            _preamble = preamble;
            _inner.OutputReceived += ForwardOutput;
            _inner.UnsolicitedOutput += ForwardUnsolicitedOutput;
        }

        public int ProcessId => _inner.ProcessId;
        public bool IsRunning => _inner.IsRunning;
        public event EventHandler<LftpOutputLine>? OutputReceived;
        public event EventHandler<LftpOutputLine>? UnsolicitedOutput;
        public async Task<LftpCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return await _inner.ExecuteAsync($"{_preamble}; {command}", timeout, cancellationToken);
        }
        public Task<LftpCommandResult> ExecuteToExitAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            _inner.ExecuteToExitAsync($"{_preamble}; {command}", timeout, cancellationToken);
        public Task StopAsync(bool force = false, CancellationToken cancellationToken = default) =>
            _inner.StopAsync(force, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            _inner.OutputReceived -= ForwardOutput;
            _inner.UnsolicitedOutput -= ForwardUnsolicitedOutput;
            await _inner.DisposeAsync();
        }
        private void ForwardOutput(object? sender, LftpOutputLine line) => OutputReceived?.Invoke(this, line);
        private void ForwardUnsolicitedOutput(object? sender, LftpOutputLine line) => UnsolicitedOutput?.Invoke(this, line);
    }
}
