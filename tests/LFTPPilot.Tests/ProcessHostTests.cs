using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class ProcessHostTests
{
    private const string FakeLftpScript = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " +
        "$pendingShutdownOutput=$false; $oneShotExitCode=0; " +
        "while (($line=[Console]::In.ReadLine()) -ne $null) { " +
        "if ($line.StartsWith('echo __LFTPPILOT_SYNC__')) { [Console]::Out.WriteLine($line.Substring(5)); [Console]::Out.Flush() } " +
        "elseif ($line -eq 'exit top kill') { " +
        "if ($pendingShutdownOutput) { [Console]::Out.WriteLine('__LFTPPILOT_TEST_BOUNDARY__'); [Console]::Out.WriteLine('OUT:post-marker-buffer'); [Console]::Error.WriteLine('ERR:post-marker-buffer'); [Console]::Out.Flush(); [Console]::Error.Flush() }; " +
        "exit $oneShotExitCode } " +
        "elseif ($line -eq 'exit kill') { exit 0 } " +
        "elseif ($line -eq 'one-shot-buffered') { [Console]::Out.WriteLine('OUT:one-shot-buffered'); [Console]::Error.WriteLine('ERR:one-shot-buffered'); [Console]::Out.Flush(); [Console]::Error.Flush(); $pendingShutdownOutput=$true } " +
        "elseif ($line -eq 'one-shot-fail') { [Console]::Out.WriteLine('OUT:one-shot-fail'); [Console]::Out.Flush(); $oneShotExitCode=7 } " +
        "elseif ($line -eq 'one-shot-long-line') { [Console]::Out.WriteLine((('x' * 270000) -join '')); [Console]::Out.Flush() } " +
        "elseif ($line -eq 'hang') { Start-Sleep -Seconds 5 } " +
        "else { [Console]::Out.WriteLine('OUT:'+$line); [Console]::Error.WriteLine('ERR:'+$line); [Console]::Out.Flush(); [Console]::Error.Flush() } }";

    [Fact]
    public async Task RealRedirectedProcessAttributesBothStreamsAndHidesMarkers()
    {
        await using var session = await StartFakeAsync(TestContext.Current.CancellationToken);
        var observed = new System.Collections.Concurrent.ConcurrentBag<LftpOutputLine>();
        session.OutputReceived += (_, line) => observed.Add(line);
        var result = await session.ExecuteAsync("cls 曲", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Contains(result.Lines, line => line.Stream == "stdout" && line.Line == "OUT:cls 曲");
        Assert.Contains(result.Lines, line => line.Stream == "stderr" && line.Line == "ERR:cls 曲");
        Assert.DoesNotContain(result.Lines, line => line.Line.Contains("__LFTPPILOT_SYNC__", StringComparison.Ordinal));
        Assert.Equal(2, observed.Count);
        Assert.Contains(observed, line => line.Stream == "stdout");
        Assert.Contains(observed, line => line.Stream == "stderr");
        Assert.DoesNotContain(observed, line => line.Line.Contains("__LFTPPILOT_SYNC__", StringComparison.Ordinal));

        Assert.True(session.IsRunning);
        var next = await session.ExecuteAsync("pwd reusable", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(next.Succeeded);
        Assert.Contains(next.Lines, line => line.Stream == "stdout" && line.Line == "OUT:pwd reusable");
        Assert.True(session.IsRunning);
    }

    [Fact]
    public async Task ExecuteToExitCapturesOutputBufferedThroughProcessExit()
    {
        await using var session = await StartFakeAsync(TestContext.Current.CancellationToken);
        var unsolicited = new System.Collections.Concurrent.ConcurrentBag<LftpOutputLine>();
        session.UnsolicitedOutput += (_, line) => unsolicited.Add(line);

        var result = await session.ExecuteToExitAsync(
            "one-shot-buffered",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.False(result.Truncated);
        Assert.Null(result.Failure);
        Assert.Contains(result.Lines, line => line.Stream == "stdout" && line.Line == "OUT:one-shot-buffered");
        Assert.Contains(result.Lines, line => line.Stream == "stderr" && line.Line == "ERR:one-shot-buffered");
        Assert.Contains(result.Lines, line => line.Stream == "stdout" && line.Line == "OUT:post-marker-buffer");
        Assert.Contains(result.Lines, line => line.Stream == "stderr" && line.Line == "ERR:post-marker-buffer");
        Assert.Empty(unsolicited);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task ExecuteToExitReportsNonZeroExitAfterReturningBufferedOutput()
    {
        await using var session = await StartFakeAsync(TestContext.Current.CancellationToken);

        var result = await session.ExecuteToExitAsync(
            "one-shot-fail",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Contains("code 7", result.Failure, StringComparison.Ordinal);
        Assert.Contains(result.Lines, line => line.Stream == "stdout" && line.Line == "OUT:one-shot-fail");
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task ExecuteToExitReportsReaderLineTruncation()
    {
        await using var session = await StartFakeAsync(TestContext.Current.CancellationToken);

        var result = await session.ExecuteToExitAsync(
            "one-shot-long-line",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.Truncated);
        var line = Assert.Single(result.Lines);
        Assert.EndsWith("... [line truncated]", line.Line, StringComparison.Ordinal);
        Assert.True(line.Line.Length < 270_000);
    }

    [Fact]
    public async Task ExecuteToExitTimeoutRetiresSession()
    {
        await using var session = await StartFakeAsync(TestContext.Current.CancellationToken);

        var result = await session.ExecuteToExitAsync(
            "hang",
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Contains("timed out", result.Failure, StringComparison.Ordinal);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task ExecuteToExitCancellationRetiresSession()
    {
        await using var session = await StartFakeAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteToExitAsync(
            "hang",
            TimeSpan.FromSeconds(5),
            cancellation.Token));

        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task CommandsAreSerializedAndSecretsAreRedacted()
    {
        await using var session = await StartFakeAsync(TestContext.Current.CancellationToken, "hunter2");
        var first = session.ExecuteAsync("pwd hunter2", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = session.ExecuteAsync("cls second", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(first, second);
        Assert.Contains(SecretRedactor.Replacement, string.Join('\n', results[0].Lines.Select(static line => line.Line)), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", string.Join('\n', results[0].Lines.Select(static line => line.Line)), StringComparison.Ordinal);
        Assert.Contains(results[1].Lines, line => line.Line == "OUT:cls second");
    }

    [Fact]
    public void RedactorCoversLftpQuotedShellQuotedAndUrlEncodedSecretForms()
    {
        const string secret = "p'a\\\"ss word";
        var redactor = new SecretRedactor([secret]);
        var rendered = string.Join(' ',
            LftpCommandBuilder.Quote(secret),
            LftpCommandBuilder.ShellQuote(secret),
            Uri.EscapeDataString(secret));

        var redacted = redactor.Redact(rendered);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(LftpCommandBuilder.Quote(secret), redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(LftpCommandBuilder.ShellQuote(secret), redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(secret), redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Replacement, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutRetiresContaminatedSession()
    {
        await using var session = await StartFakeAsync(TestContext.Current.CancellationToken);
        var result = await session.ExecuteAsync("hang", TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.True(result.TimedOut);
        Assert.False(session.IsRunning);
        var next = await session.ExecuteAsync("should-not-run", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(next.Succeeded);
    }

    [Fact]
    public async Task CommandProtocolRejectsMultipleLines()
    {
        await using var session = await StartFakeAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteAsync("cls\n! calc", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    private static async Task<ILftpSession> StartFakeAsync(CancellationToken cancellationToken, params string[] secrets)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executable = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(executable), $"PowerShell was not found at {executable}");
        var options = new LftpProcessStartOptions(
            executable,
            Path.GetTempPath(),
            Arguments: ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", FakeLftpScript],
            Secrets: secrets);
        return await new LftpProcessHost().StartAsync(options, cancellationToken);
    }
}
