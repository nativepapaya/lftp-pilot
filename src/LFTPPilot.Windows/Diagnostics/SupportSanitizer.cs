using System.Text.RegularExpressions;

namespace LFTPPilot.Windows.Diagnostics;

public static partial class SupportSanitizer
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        string sanitized = UriCredentials().Replace(value, "${scheme}://<redacted>@");
        sanitized = SensitiveQuery().Replace(sanitized, "${key}<redacted>");
        sanitized = SensitiveAssignment().Replace(sanitized, "${key}=<redacted>");
        sanitized = AuthorizationHeader().Replace(sanitized, "${prefix}<redacted>");
        sanitized = PrivateKey().Replace(sanitized, "<redacted-private-key>");
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            sanitized = sanitized.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return sanitized;
    }

    [GeneratedRegex(@"(?<scheme>\b(?:sftp|ftps?|ftpes))://[^\s/@:]+(?::[^\s/@]*)?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriCredentials();

    [GeneratedRegex(@"(?<key>[?&](?:password|passwd|passphrase|secret|token|api[_-]?key)=)[^&#\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQuery();

    [GeneratedRegex(@"(?<key>\b(?:password|passwd|passphrase|secret|token|api[_-]?key|private[_-]?key))\s*[=:]\s*[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignment();

    [GeneratedRegex(@"(?<prefix>\bAuthorization\s*:\s*(?:Basic|Bearer)\s+)[A-Za-z0-9+/=_-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"-----BEGIN (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----.*?-----END (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PrivateKey();
}
