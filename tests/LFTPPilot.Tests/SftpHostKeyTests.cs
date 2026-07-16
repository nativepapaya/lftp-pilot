using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Tests;

public sealed class SftpHostKeyTests
{
    [Fact]
    public async Task JsonStoreAtomicallyRoundTripsReplacesAndDeletesBoundKeys()
    {
        using var directory = new TestDirectory();
        var paths = new PackageDataPaths(
            Path.Combine(directory.Path, "LocalState"),
            Path.Combine(directory.Path, "LocalCache"),
            Path.Combine(directory.Path, "TempState"),
            false);
        paths.EnsureCreated();
        Assert.True(Directory.Exists(paths.HostKeys));

        var path = Path.Combine(paths.HostKeys, "trusted-sftp-host-keys.json");
        var store = new JsonHostKeyStore(path);
        var profile = Profile();
        var first = Key(profile, "ssh-ed25519", "first public key");
        var replacement = Key(profile, "ssh-ed25519", "replacement public key");

        await store.SaveAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal(first, await store.GetAsync(first.Binding, TestContext.Current.CancellationToken));
        await store.SaveAsync(replacement, TestContext.Current.CancellationToken);
        Assert.Equal(replacement, await store.GetAsync(first.Binding, TestContext.Current.CancellationToken));
        Assert.Single(JsonSerializer.Deserialize<List<TrustedSftpHostKey>>(
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Assert.Empty(Directory.GetFiles(paths.HostKeys, "*.tmp"));

        await store.DeleteAsync(profile.Id, TestContext.Current.CancellationToken);
        Assert.Null(await store.GetAsync(first.Binding, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JsonStoreRejectsInvalidKeysAndDuplicateDurableBindings()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "host-keys.json");
        var store = new JsonHostKeyStore(path);
        var key = Key(Profile(), "ssh-ed25519", "public key");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            key with { FingerprintSha256 = "SHA256:not-the-key" },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            key with { Algorithm = "ssh-ed25519 injected" },
            TestContext.Current.CancellationToken));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new[] { key, key }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(
            key.Binding,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JsonStoreRejectsAReparseAncestorBeforeWritingOutsideItsRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory();
        var target = Path.Combine(directory.Path, "outside-target");
        var junction = Path.Combine(directory.Path, "store-link");
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);
        try
        {
            var store = new JsonHostKeyStore(Path.Combine(junction, "trusted-host-keys.json"));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
                Key(Profile(), "ssh-ed25519", "public key"),
                TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task EnrollmentReviewHidesRawKeyAndMaterializesOneOpaqueKnownHostsEntry()
    {
        using var directory = new TestDirectory();
        var profile = Profile();
        var proposed = Key(profile, "ssh-ed25519", "server public key material");
        var store = new JsonHostKeyStore(Path.Combine(directory.Path, "host-keys.json"));
        var probe = new FakeProbe(proposed);
        var time = new ManualTimeProvider(new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var manager = new SftpHostKeyManager(store, probe, time);

        var inspection = await manager.InspectAsync(profile, TestContext.Current.CancellationToken);

        Assert.Equal(SftpHostKeyState.EnrollmentRequired, inspection.State);
        var review = Assert.IsType<SftpHostKeyReview>(inspection.Review);
        Assert.Equal(time.GetUtcNow().AddMinutes(5), review.ExpiresAt);
        Assert.Equal(64, review.ApprovalToken.Length);
        Assert.All(review.ApprovalToken, character => Assert.True(char.IsAsciiHexDigit(character)));
        Assert.DoesNotContain(proposed.PublicKeyBase64, JsonSerializer.Serialize(inspection), StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApproveAsync(
            profile,
            new(profile.Id, review.ReviewId, new string('0', 64)),
            replacementAllowed: false,
            TestContext.Current.CancellationToken));

        var approved = await manager.ApproveAsync(
            profile,
            new(profile.Id, review.ReviewId, review.ApprovalToken),
            replacementAllowed: false,
            TestContext.Current.CancellationToken);
        Assert.Equal(proposed.FingerprintSha256, approved.FingerprintSha256);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApproveAsync(
            profile,
            new(profile.Id, review.ReviewId, review.ApprovalToken),
            replacementAllowed: false,
            TestContext.Current.CancellationToken));

        var materialized = await manager.MaterializeAsync(
            profile,
            Path.Combine(directory.Path, "runtime", "session"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(probe.LastAlias, materialized.HostKeyAlias);
        Assert.DoesNotContain(profile.Host, materialized.HostKeyAlias, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            $"{materialized.HostKeyAlias} {proposed.Algorithm} {proposed.PublicKeyBase64}\n",
            await File.ReadAllTextAsync(materialized.KnownHostsPath, TestContext.Current.CancellationToken));
        Assert.Equal(
            (FileAttributes)0,
            File.GetAttributes(materialized.KnownHostsPath) &
            (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device));

        var trusted = await manager.InspectAsync(profile, TestContext.Current.CancellationToken);
        Assert.Equal(SftpHostKeyState.Trusted, trusted.State);
        Assert.Null(trusted.Review);
    }

    [Fact]
    public async Task ChangedKeyRequiresFreshExplicitReplacementAndExactTrustedBaseline()
    {
        using var directory = new TestDirectory();
        var profile = Profile();
        var oldKey = Key(profile, "ssh-ed25519", "old key");
        var changedKey = Key(profile, "ssh-ed25519", "changed key");
        var interveningKey = Key(profile, "ecdsa-sha2-nistp256", "intervening key");
        var store = new JsonHostKeyStore(Path.Combine(directory.Path, "host-keys.json"));
        await store.SaveAsync(oldKey, TestContext.Current.CancellationToken);
        var probe = new FakeProbe(changedKey);
        var manager = new SftpHostKeyManager(store, probe);
        var review = Assert.IsType<SftpHostKeyReview>((await manager.InspectAsync(
            profile,
            TestContext.Current.CancellationToken)).Review);

        Assert.Equal(SftpHostKeyState.Changed, review.State);
        Assert.Equal(oldKey.FingerprintSha256, review.TrustedFingerprintSha256);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApproveAsync(
            profile,
            new(profile.Id, review.ReviewId, review.ApprovalToken),
            replacementAllowed: true,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApproveAsync(
            profile,
            new(profile.Id, review.ReviewId, review.ApprovalToken, ReplaceExisting: true),
            replacementAllowed: false,
            TestContext.Current.CancellationToken));

        await store.SaveAsync(interveningKey, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApproveAsync(
            profile,
            new(profile.Id, review.ReviewId, review.ApprovalToken, ReplaceExisting: true),
            replacementAllowed: true,
            TestContext.Current.CancellationToken));
        Assert.Equal(interveningKey, await store.GetAsync(oldKey.Binding, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApproveAsync(
            profile,
            new(profile.Id, review.ReviewId, review.ApprovalToken, ReplaceExisting: true),
            replacementAllowed: true,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReviewsExpireAndAProfileEndpointChangeInvalidatesApproval()
    {
        using var directory = new TestDirectory();
        var profile = Profile();
        var time = new ManualTimeProvider(new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var store = new JsonHostKeyStore(Path.Combine(directory.Path, "host-keys.json"));
        var manager = new SftpHostKeyManager(store, new FakeProbe(Key(profile, "ssh-ed25519", "server key")), time);
        var expired = Assert.IsType<SftpHostKeyReview>((await manager.InspectAsync(
            profile,
            TestContext.Current.CancellationToken)).Review);
        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApproveAsync(
            profile,
            new(profile.Id, expired.ReviewId, expired.ApprovalToken),
            replacementAllowed: false,
            TestContext.Current.CancellationToken));

        var current = Assert.IsType<SftpHostKeyReview>((await manager.InspectAsync(
            profile,
            TestContext.Current.CancellationToken)).Review);
        var changedProfile = profile with { Host = "other.example.test" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApproveAsync(
            changedProfile,
            new(profile.Id, current.ReviewId, current.ApprovalToken),
            replacementAllowed: false,
            TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync(SftpHostKeyManager.CreateBinding(profile), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MaterializationRejectsAReparsePointInItsTargetPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory();
        var profile = Profile();
        var key = Key(profile, "ssh-ed25519", "server key");
        var store = new JsonHostKeyStore(Path.Combine(directory.Path, "host-keys.json"));
        await store.SaveAsync(key, TestContext.Current.CancellationToken);
        var target = Path.Combine(directory.Path, "real-target");
        var junction = Path.Combine(directory.Path, "linked-target");
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);
        var manager = new SftpHostKeyManager(store, new FakeProbe(key));

        try
        {
            await Assert.ThrowsAsync<IOException>(() => manager.MaterializeAsync(
                profile,
                Path.Combine(junction, "session"),
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(target, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    private static ConnectionProfile Profile() => new(
        Guid.NewGuid(),
        "SFTP test",
        ConnectionProtocol.Sftp,
        "files.example.test",
        22,
        "alice",
        AuthenticationKind.AskOnConnect);

    private static TrustedSftpHostKey Key(ConnectionProfile profile, string algorithm, string material)
    {
        var algorithmBytes = Encoding.ASCII.GetBytes(algorithm);
        var materialBytes = Encoding.UTF8.GetBytes(material);
        var bytes = new byte[sizeof(uint) + algorithmBytes.Length + materialBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)algorithmBytes.Length));
        algorithmBytes.CopyTo(bytes.AsSpan(sizeof(uint)));
        materialBytes.CopyTo(bytes.AsSpan(sizeof(uint) + algorithmBytes.Length));
        return new(
            SftpHostKeyManager.CreateBinding(profile),
            algorithm,
            Convert.ToBase64String(bytes),
            "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('='));
    }

    private sealed class FakeProbe(TrustedSftpHostKey key) : ISshHostKeyProbe
    {
        public string? LastAlias { get; private set; }

        public Task<TrustedSftpHostKey> ProbeAsync(
            ConnectionProfile profile,
            string hostKeyAlias,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAlias = hostKeyAlias;
            return Task.FromResult(key);
        }
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("The directory-junction test helper did not start.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"The directory-junction test helper failed: {process.StandardError.ReadToEnd()}");
        Assert.True((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lftp-pilot-host-key-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
