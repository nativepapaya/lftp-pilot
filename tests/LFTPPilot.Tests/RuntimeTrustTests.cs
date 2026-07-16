using System.Security.Cryptography;
using System.Text.Json;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class RuntimeTrustTests
{
    [Fact]
    public async Task PackagedCandidateAuthenticatesExactInventoryAndRejectsTampering()
    {
        using var directory = new TestDirectory();
        var files = new[] { "usr/bin/lftp.exe", "usr/bin/ssh.exe", "usr/bin/sh.exe" };
        var inventory = new List<object>();
        foreach (var relative in files)
        {
            var path = Path.Combine(directory.Path, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = System.Text.Encoding.UTF8.GetBytes("trusted:" + relative);
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            inventory.Add(new { path = relative, size = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        }
        await File.WriteAllTextAsync(Path.Combine(directory.Path, ".bundle-rev"), "8\n", TestContext.Current.CancellationToken);
        var manifest = new { schema = 3, bundleRevision = "8", architecture = "x64", files = inventory };
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "bundle-manifest.json"), JsonSerializer.Serialize(manifest), TestContext.Current.CancellationToken);

        var descriptor = await PackagedLftpRuntimeProvider.CreatePackageCandidateForTests(directory.Path).ResolveAsync(TestContext.Current.CancellationToken);
        Assert.True(descriptor.IsAuthenticated);
        Assert.False(descriptor.IsTestOverride);

        await File.AppendAllTextAsync(Path.Combine(directory.Path, "usr", "bin", "lftp.exe"), "tampered", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackagedLftpRuntimeProvider.CreatePackageCandidateForTests(directory.Path).ResolveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PackagedCandidateRejectsUnexpectedFilesAndRevisionMismatch()
    {
        using var directory = new TestDirectory();
        var bin = Path.Combine(directory.Path, "usr", "bin");
        Directory.CreateDirectory(bin);
        var inventory = new List<object>();
        foreach (var name in new[] { "lftp.exe", "ssh.exe", "sh.exe" })
        {
            var bytes = new byte[] { 1, 2, 3 };
            await File.WriteAllBytesAsync(Path.Combine(bin, name), bytes, TestContext.Current.CancellationToken);
            inventory.Add(new { path = $"usr/bin/{name}", size = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        }
        await File.WriteAllTextAsync(Path.Combine(directory.Path, ".bundle-rev"), "different", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "bundle-manifest.json"),
            JsonSerializer.Serialize(new { schema = 3, bundleRevision = "8", architecture = "x64", files = inventory }), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackagedLftpRuntimeProvider.CreatePackageCandidateForTests(directory.Path).ResolveAsync(TestContext.Current.CancellationToken));

        await File.WriteAllTextAsync(Path.Combine(directory.Path, ".bundle-rev"), "8", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "unexpected.dll"), "extra", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackagedLftpRuntimeProvider.CreatePackageCandidateForTests(directory.Path).ResolveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExplicitTestOverrideIsClearlyMarkedUnauthenticated()
    {
        using var directory = new TestDirectory();
        var executable = Path.Combine(directory.Path, "fake-lftp.exe");
        await File.WriteAllTextAsync(executable, "fake", TestContext.Current.CancellationToken);
        var descriptor = await PackagedLftpRuntimeProvider.CreateTestOverride(executable).ResolveAsync(TestContext.Current.CancellationToken);
        Assert.False(descriptor.IsAuthenticated);
        Assert.True(descriptor.IsTestOverride);
        Assert.Equal(Path.GetFullPath(executable), descriptor.ExecutablePath);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LFTPPilot.RuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
