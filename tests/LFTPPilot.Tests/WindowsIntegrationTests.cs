using System.Diagnostics;
using LFTPPilot.Engine;
using LFTPPilot.Windows.Activation;
using LFTPPilot.Windows.Diagnostics;
using LFTPPilot.Windows.Shell;

namespace LFTPPilot.Tests;

public sealed class WindowsIntegrationTests
{
    [Theory]
    [InlineData(".exe")]
    [InlineData(".cmd")]
    [InlineData(".hta")]
    [InlineData(".lnk")]
    [InlineData(".url")]
    public void TrustedEditorUsesSystemNotepadAndOneLiteralArgument(string extension)
    {
        var managedPath = Path.Combine(Path.GetTempPath(), $"managed copy & untrusted{extension}");

        var start = TrustedEditorLauncher.CreateStartInfo(managedPath);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "notepad.exe"), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(string.Empty, start.Verb);
        Assert.Equal(string.Empty, start.Arguments);
        Assert.Collection(start.ArgumentList, argument => Assert.Equal(managedPath, argument));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-file.txt")]
    public void TrustedEditorRejectsMissingOrRelativeManagedPaths(string managedPath)
    {
        Assert.ThrowsAny<ArgumentException>(() => TrustedEditorLauncher.CreateStartInfo(managedPath));
    }

    [Theory]
    [InlineData("lftp-pilot://transfers", ProtocolActivationAction.ShowTransfers)]
    [InlineData("lftp-pilot://settings", ProtocolActivationAction.OpenSettings)]
    public void ProtocolActivationAllowsOnlyKnownParameterlessActions(string value, ProtocolActivationAction expected)
    {
        Assert.True(ProtocolActivationParser.TryParse(new Uri(value), out var request));
        Assert.NotNull(request);
        Assert.Equal(expected, request.Action);
        Assert.Null(request.ProfileId);
    }

    [Fact]
    public void ProtocolActivationAllowsOnlyAProfileIdentifierForOpenProfile()
    {
        var id = Guid.NewGuid();
        Assert.True(ProtocolActivationParser.TryParse(new Uri($"lftp-pilot://open-profile?id={id:D}"), out var request));
        Assert.Equal(new ProtocolActivationRequest(ProtocolActivationAction.OpenProfile, id), request);
    }

    [Theory]
    [InlineData("https://transfers")]
    [InlineData("lftp-pilot://unknown")]
    [InlineData("lftp-pilot://settings?command=rm")]
    [InlineData("lftp-pilot://transfers#fragment")]
    [InlineData("lftp-pilot://user:secret@settings")]
    [InlineData("lftp-pilot://open-profile?id=00000000-0000-0000-0000-000000000000")]
    [InlineData("lftp-pilot://open-profile?id=not-a-guid")]
    [InlineData("lftp-pilot://open-profile?id=6f9619ff-8b86-d011-b42d-00c04fc964ff&id=6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("lftp-pilot://open-profile?id=%ZZ")]
    [InlineData("lftp-pilot://settings?%ZZ=x")]
    public void ProtocolActivationRejectsUnknownOrMalformedInput(string value)
    {
        Assert.False(ProtocolActivationParser.TryParse(new Uri(value), out var request));
        Assert.Null(request);
    }

    [Fact]
    public void SupportBundleSanitizerRedactsCredentialsTokensAndPrivateKeys()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var input = "sftp://alice:correct-horse@example.test/file " +
            "https://example.test/?token=abc123&safe=yes password=letmein " +
            "Authorization: Bearer eyJhbGciOiJub25lIn0 " +
            "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret-material\n-----END OPENSSH PRIVATE KEY----- " +
            Path.Combine(profile, "Documents", "LFTP Pilot");

        var output = SupportSanitizer.Redact(input);

        Assert.DoesNotContain("correct-horse", output, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", output, StringComparison.Ordinal);
        Assert.DoesNotContain("letmein", output, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJub25lIn0", output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-material", output, StringComparison.Ordinal);
        Assert.Contains("sftp://<redacted>@example.test/file", output, StringComparison.Ordinal);
        Assert.Contains("token=<redacted>", output, StringComparison.Ordinal);
        Assert.Contains("password=<redacted>", output, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer <redacted>", output, StringComparison.Ordinal);
        Assert.Contains("<redacted-private-key>", output, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            Assert.DoesNotContain(profile, output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ClosingJobObjectTerminatesAssignedChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" },
        }) ?? throw new InvalidOperationException("The child process did not start.");

        try
        {
            using (var job = new WindowsJobObject())
            {
                job.Assign(process);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
