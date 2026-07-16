using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class ProcessHostTests
{
    private const string FakeLftpScript = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " +
        "while (($line=[Console]::In.ReadLine()) -ne $null) { " +
        "if ($line.StartsWith('echo __LFTPPILOT_SYNC__')) { [Console]::Out.WriteLine($line.Substring(5)); [Console]::Out.Flush() } " +
        "elseif ($line -eq 'exit kill') { exit 0 } " +
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
