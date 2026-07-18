using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using LFTPPilot.App.Services;
using LFTPPilot.Core;
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
        var root = Path.Combine(Path.GetTempPath(), "LFTPPilot.TrustedEditorTests", Guid.NewGuid().ToString("N"));
        var ownedDirectory = Path.Combine(root, "opaque-edit");
        var managedPath = Path.Combine(ownedDirectory, $"managed copy & untrusted{extension}");
        Directory.CreateDirectory(ownedDirectory);
        File.WriteAllText(managedPath, "managed content");
        try
        {
            var start = TrustedEditorLauncher.CreateStartInfo(managedPath, root);

            Assert.Equal(Path.Combine(Environment.SystemDirectory, "notepad.exe"), start.FileName);
            Assert.False(start.UseShellExecute);
            Assert.Equal(string.Empty, start.Verb);
            Assert.Equal(string.Empty, start.Arguments);
            Assert.Collection(start.ArgumentList, argument => Assert.Equal(managedPath, argument));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-file.txt")]
    public void TrustedEditorRejectsMissingOrRelativeManagedPaths(string managedPath)
    {
        Assert.ThrowsAny<ArgumentException>(() => TrustedEditorLauncher.CreateStartInfo(managedPath, Path.GetTempPath()));
    }

    [Fact]
    public void TrustedEditorRejectsTargetsOutsideManagedRootOrMissingFromIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "LFTPPilot.TrustedEditorTests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(outside, "outside");
        try
        {
            Assert.Throws<InvalidDataException>(() => TrustedEditorLauncher.CreateStartInfo(outside, root));
            Assert.Throws<FileNotFoundException>(() => TrustedEditorLauncher.CreateStartInfo(Path.Combine(root, "missing.txt"), root));
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrustedEditorRejectsReparsePointsInsideManagedRoot()
    {
        var outer = Path.Combine(Path.GetTempPath(), "LFTPPilot.TrustedEditorTests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(outer, "managed");
        var external = Path.Combine(outer, "external");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "content.txt"), "external");
        var link = Path.Combine(root, "opaque-edit");
        try
        {
            Directory.CreateSymbolicLink(link, external);
        }
        catch (IOException) when (!Directory.Exists(link))
        {
            Directory.Delete(outer, recursive: true);
            return; // The current test token does not hold Windows symlink creation privilege.
        }
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                TrustedEditorLauncher.CreateStartInfo(Path.Combine(link, "content.txt"), root));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outer, recursive: true);
        }
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
    public void JumpListsAcceptOnlyAllowlistedProtocolActivations()
    {
        JumpListService.ValidateEntry(new("Transfers", "lftp-pilot://transfers"));
        JumpListService.ValidateEntry(new("Profile", $"lftp-pilot://open-profile?id={Guid.NewGuid():D}"));

        Assert.Throws<ArgumentException>(() => JumpListService.ValidateEntry(new("Unsafe", "cmd.exe /c whoami")));
        Assert.Throws<ArgumentException>(() => JumpListService.ValidateEntry(new("Unsafe", "lftp-pilot://settings?command=quit")));
    }

    [Fact]
    public void JumpListCommandLineFallbackRequiresOneAllowlistedActivation()
    {
        Assert.True(ProtocolActivationRouter.TryParseCommandLine(
            ["lftp-pilot://transfers"], out var request));
        Assert.Equal(ProtocolActivationAction.ShowTransfers, request?.Action);
        Assert.False(ProtocolActivationRouter.TryParseCommandLine(
            ["lftp-pilot://transfers", "--unexpected"], out request));
        Assert.Null(request);
        Assert.False(ProtocolActivationRouter.TryParseCommandLine(
            ["https://example.test"], out request));
        Assert.Null(request);
    }

    [Fact]
    public void TaskbarProgressAggregatesKnownWorkAndUsesIndeterminateForUnknownWork()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new JobSnapshot(Guid.NewGuid(), JobKind.Transfer, Guid.NewGuid(), "One", JobState.Running, now, now, Progress: 0.25);
        var second = new JobSnapshot(Guid.NewGuid(), JobKind.Mirror, Guid.NewGuid(), "Two", JobState.Running, now, now, Progress: 0.75);

        Assert.Equal(new(TaskbarProgressState.Normal, 5_000, 10_000), TaskbarProgressPolicy.Summarize([first, second]));
        Assert.Equal(TaskbarProgressState.Indeterminate, TaskbarProgressPolicy.Summarize([first with { Progress = null }]).State);
        Assert.Equal(TaskbarProgressState.Paused, TaskbarProgressPolicy.Summarize([first with { State = JobState.Paused }]).State);
        Assert.Equal(TaskbarProgressState.None, TaskbarProgressPolicy.Summarize([first with { State = JobState.Completed }]).State);
    }

    [Theory]
    [InlineData(JobState.Completed, "Transfer activity completed")]
    [InlineData(JobState.Failed, "Transfer activity failed")]
    [InlineData(JobState.Missed, "Scheduled transfer missed")]
    public void JobNotificationsAreCreatedOnlyForAttentionWorthyTerminalOutcomes(JobState state, string title)
    {
        var now = DateTimeOffset.UtcNow;
        var error = state == JobState.Failed ? new EngineError("failed", "Permission denied") : null;
        var job = new JobSnapshot(Guid.NewGuid(), JobKind.Transfer, Guid.NewGuid(), "archive.zip", state, now, now,
            Status: "Finished", Error: error);

        var notification = Assert.IsType<JobNotification>(JobNotificationPolicy.Create(job));
        Assert.Equal(title, notification.Title);
        Assert.Contains("archive.zip", notification.Message, StringComparison.Ordinal);
        Assert.Equal(job.Id.ToString("N"), notification.Tag);
        Assert.Null(JobNotificationPolicy.Create(job with { State = JobState.Cancelled, Error = null }));
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
    public async Task SupportBundleWritesOnlySanitizedBoundedTextEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lftp-pilot-support-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var bundle = Path.Combine(root, "support.zip");
        try
        {
            await new SupportBundleBuilder().CreateAsync(
                bundle,
                new Dictionary<string, object?> { ["token"] = "secret-token" },
                [new("activity/log.txt", "password=hunter2")],
                TestContext.Current.CancellationToken);

            using var archive = ZipFile.OpenRead(bundle);
            Assert.Equal(["activity/log.txt", "metadata.json"], archive.Entries.Select(static entry => entry.FullName).Order().ToArray());
            foreach (var entry in archive.Entries)
            {
                Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0), entry.LastWriteTime.DateTime);
                using var reader = new StreamReader(entry.Open());
                var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
                Assert.DoesNotContain("secret-token", content, StringComparison.Ordinal);
                Assert.DoesNotContain("hunter2", content, StringComparison.Ordinal);
            }
            using var metadataReader = new StreamReader(archive.GetEntry("metadata.json")!.Open());
            using JsonDocument metadata = JsonDocument.Parse(await metadataReader.ReadToEndAsync(TestContext.Current.CancellationToken));
            Assert.Equal("<redacted>", metadata.RootElement.GetProperty("token").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
