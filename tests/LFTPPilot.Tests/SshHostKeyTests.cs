using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class SshKnownHostsParserTests
{
    private const string Alias = "lftp-pilot-0123456789abcdef";
    private static readonly HostKeyBinding Binding = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "sftp://example.test:22");

    [Fact]
    public void ParsesOneExactEntryAndComputesTheOpenSshSha256Fingerprint()
    {
        var (line, blob) = CreateEntry(Alias, "ssh-ed25519");

        var key = SshKnownHostsParser.Parse(line + "\n", Binding, Alias);

        Assert.Equal(Binding, key.Binding);
        Assert.Equal("ssh-ed25519", key.Algorithm);
        Assert.Equal(Convert.ToBase64String(blob), key.PublicKeyBase64);
        Assert.Equal("SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='), key.FingerprintSha256);
        Assert.Equal(line + "\n", SshKnownHostsParser.Format(Alias, key));
    }

    [Fact]
    public void IdentityCanonicalizesIpv6AndCreatesAnOpaqueStableAlias()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var profile = new ConnectionProfile(
            id, "IPv6", ConnectionProtocol.Sftp, "[2001:DB8::1]", 2222, "alice", AuthenticationKind.Password);

        var binding = SftpHostKeyIdentity.CreateBinding(profile);
        var alias = SftpHostKeyIdentity.CreateHostKeyAlias(binding);

        Assert.Equal("sftp://[2001:db8::1]:2222", binding.Endpoint);
        Assert.StartsWith($"lftp-pilot-{id:N}-", alias, StringComparison.Ordinal);
        Assert.Equal(alias, SftpHostKeyIdentity.CreateHostKeyAlias(binding));
        Assert.DoesNotContain("2001", alias, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownHostsOptionQuotesSpacesEscapesPercentTokensAndRejectsEnvironmentExpansion()
    {
        Assert.Equal(
            "UserKnownHostsFile=\"/c/LFTP Pilot/%%h/known_hosts\"",
            SftpHostKeyIdentity.CreateUserKnownHostsOption("/c/LFTP Pilot/%h/known_hosts"));
        Assert.Throws<ArgumentException>(() => SftpHostKeyIdentity.CreateUserKnownHostsOption(
            "/c/${HOME}/known_hosts"));
        Assert.Throws<ArgumentException>(() => SftpHostKeyIdentity.CreateUserKnownHostsOption(
            "/c/unsafe\\known_hosts"));
    }

    [Fact]
    public void RejectsWrongAliasMultipleEntriesMarkersCommentsAndMalformedBase64()
    {
        var (line, _) = CreateEntry(Alias, "ssh-ed25519");
        var encoded = line.Split(' ')[2];

        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse(line, Binding, "different-alias"));
        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse(line + "\n" + line, Binding, Alias));
        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse("@cert-authority " + line, Binding, Alias));
        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse(line + " comment", Binding, Alias));
        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse($"{Alias} ssh-ed25519 !!!", Binding, Alias));
        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse($"*.example.test ssh-ed25519 {encoded}", Binding, Alias));
    }

    [Fact]
    public void RejectsAlgorithmMismatchCertificatesAndFingerprintTampering()
    {
        var (_, blob) = CreateEntry(Alias, "ssh-ed25519");
        var mismatch = $"{Alias} ssh-rsa {Convert.ToBase64String(blob)}";
        var (certificate, _) = CreateEntry(Alias, "ssh-ed25519-cert-v01@openssh.com");

        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse(mismatch, Binding, Alias));
        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse(certificate, Binding, Alias));

        var valid = SshKnownHostsParser.Parse(CreateEntry(Alias, "ssh-ed25519").Line, Binding, Alias);
        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Format(
            Alias, valid with { FingerprintSha256 = "SHA256:tampered" }));
    }

    [Fact]
    public void RejectsOversizedOrNonCanonicalDocuments()
    {
        var oversized = new string('a', SshKnownHostsParser.MaximumDocumentCharacters + 1);
        var (line, _) = CreateEntry(Alias, "ssh-ed25519");

        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse(oversized, Binding, Alias));
        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse(line + "\n\n", Binding, Alias));
        Assert.Throws<InvalidDataException>(() => SshKnownHostsParser.Parse("\ufeff" + line, Binding, Alias));
    }

    private static (string Line, byte[] Blob) CreateEntry(string alias, string algorithm)
    {
        var algorithmBytes = Encoding.ASCII.GetBytes(algorithm);
        var blob = new byte[sizeof(uint) + algorithmBytes.Length + sizeof(uint) + 32];
        BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)algorithmBytes.Length);
        algorithmBytes.CopyTo(blob, sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(sizeof(uint) + algorithmBytes.Length), 32);
        for (var index = blob.Length - 32; index < blob.Length; index++) blob[index] = (byte)index;
        return ($"{alias} {algorithm} {Convert.ToBase64String(blob)}", blob);
    }
}

public sealed class OpenSshHostKeyProbeTests
{
    [Fact]
    public void ProbeArgumentsCannotPresentCredentialsOrPrivateKeys()
    {
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Site", ConnectionProtocol.Sftp, "example.test", 2222, "sensitive-user",
            AuthenticationKind.SshKey, SshKeyPath: @"C:\Keys\private-key");

        var arguments = OpenSshHostKeyProbe.BuildArguments(profile, "lftp-pilot-safe", "/c/temp/proposed_known_hosts");
        var joined = string.Join(' ', arguments);

        Assert.Contains("StrictHostKeyChecking=accept-new", arguments);
        Assert.Contains("UserKnownHostsFile=\"/c/temp/proposed_known_hosts\"", arguments);
        Assert.Contains("BatchMode=yes", arguments);
        Assert.Contains("PubkeyAuthentication=no", arguments);
        Assert.Contains("PasswordAuthentication=no", arguments);
        Assert.Contains("KbdInteractiveAuthentication=no", arguments);
        Assert.Contains("GSSAPIAuthentication=no", arguments);
        Assert.Contains("HostbasedAuthentication=no", arguments);
        Assert.Contains("IdentityAgent=none", arguments);
        Assert.Contains("lftp-pilot-host-key-probe", arguments);
        Assert.DoesNotContain("sensitive-user", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("-i", arguments);
    }

    [Fact]
    public void ProbeArgumentsRejectNonSftpProfilesAndUnsafeAliases()
    {
        var sftp = Profile(ConnectionProtocol.Sftp);
        var ftp = Profile(ConnectionProtocol.Ftp);

        Assert.Throws<ArgumentException>(() => OpenSshHostKeyProbe.BuildArguments(
            ftp, "lftp-pilot-safe", "/c/temp/proposed_known_hosts"));
        Assert.Throws<ArgumentException>(() => OpenSshHostKeyProbe.BuildArguments(
            sftp, "safe;ProxyCommand=calc", "/c/temp/proposed_known_hosts"));
        Assert.Throws<ArgumentException>(() => OpenSshHostKeyProbe.BuildArguments(
            sftp, "lftp-pilot-safe", "/c/temp/proposed\nknown_hosts"));
        Assert.ThrowsAny<ArgumentException>(() => OpenSshHostKeyProbe.BuildArguments(
            sftp with { Host = "-oProxyCommand=calc.exe" },
            "lftp-pilot-safe",
            "/c/temp/proposed_known_hosts"));
        Assert.ThrowsAny<ArgumentException>(() => SftpHostKeyIdentity.CreateBinding(
            sftp with { Host = "[example.test]" }));
    }

    [Fact]
    public async Task ProbeFailsClosedWhenAuthenticatedRuntimeHasNoSshExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lftp-pilot-host-key-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var descriptor = new LftpRuntimeDescriptor(
                directory, Path.Combine(directory, "lftp.exe"), directory, true, "test");
            var probe = new OpenSshHostKeyProbe(new FixedRuntimeProvider(descriptor), directory);

            await Assert.ThrowsAsync<FileNotFoundException>(() => probe.ProbeAsync(
                Profile(ConnectionProtocol.Sftp), "lftp-pilot-safe", TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeRejectsAnExistingTemporaryRootJunctionBeforeCreatingThroughIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "lftp-pilot-host-key-junction-test-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(directory, "runtime");
        var target = Path.Combine(directory, "target");
        var junction = Path.Combine(directory, "junction");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(Path.Combine(runtime, "ssh.exe"), [], TestContext.Current.CancellationToken);
        CreateDirectoryJunction(junction, target);
        try
        {
            var requestedRoot = Path.Combine(junction, "probe-root");
            var descriptor = new LftpRuntimeDescriptor(
                runtime, Path.Combine(runtime, "lftp.exe"), runtime, true, "test");
            var probe = new OpenSshHostKeyProbe(new FixedRuntimeProvider(descriptor), requestedRoot);

            await Assert.ThrowsAsync<InvalidDataException>(() => probe.ProbeAsync(
                Profile(ConnectionProtocol.Sftp), "lftp-pilot-safe", TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(target, "probe-root")));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ConnectionProfile Profile(ConnectionProtocol protocol) => new(
        Guid.NewGuid(), "Site", protocol, "example.test", protocol == ConnectionProtocol.Sftp ? 22 : 21,
        "alice", AuthenticationKind.Password);

    private sealed class FixedRuntimeProvider(LftpRuntimeDescriptor descriptor) : ILftpRuntimeProvider
    {
        public Task<LftpRuntimeDescriptor> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(descriptor);
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
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
        Assert.True(process.ExitCode == 0,
            $"The directory-junction test helper failed: {process.StandardError.ReadToEnd()}");
        Assert.True((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0);
    }
}
