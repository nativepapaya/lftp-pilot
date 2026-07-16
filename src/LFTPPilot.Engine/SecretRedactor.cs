using System.Text.RegularExpressions;

namespace LFTPPilot.Engine;

public sealed partial class SecretRedactor
{
    public const string Replacement = "••••••";
    private readonly string[] _forms;

    public SecretRedactor(IEnumerable<string>? secrets)
    {
        _forms = (secrets ?? [])
            .Where(static secret => !string.IsNullOrEmpty(secret))
            .SelectMany(EncodedForms)
            .Where(static secret => secret.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static secret => secret.Length)
            .ToArray();
    }

    public string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = value;
        foreach (var secret in _forms) redacted = redacted.Replace(secret, Replacement, StringComparison.Ordinal);
        return CredentialUrlRegex().Replace(redacted, $"$1{Replacement}@");
    }

    private static IEnumerable<string> EncodedForms(string secret)
    {
        var forms = new List<string> { secret };
        try { forms.Add(Uri.EscapeDataString(secret)); }
        catch (UriFormatException) { }
        try
        {
            var quoted = LftpCommandBuilder.Quote(secret);
            var shellQuoted = LftpCommandBuilder.ShellQuote(secret);
            forms.Add(quoted);
            forms.Add(quoted[1..^1]);
            forms.Add(shellQuoted);
            forms.Add(shellQuoted[1..^1]);
        }
        catch (ArgumentException) { }
        return forms;
    }

    [GeneratedRegex("([a-z][a-z0-9+.-]{0,31}://)[^/@\\s]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUrlRegex();
}
