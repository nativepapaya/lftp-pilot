namespace LFTPPilot.Windows.Activation;

public enum ProtocolActivationAction { OpenProfile, ShowTransfers, OpenSettings }

public sealed record ProtocolActivationRequest(ProtocolActivationAction Action, Guid? ProfileId = null);

public static class ProtocolActivationParser
{
    public const string Scheme = "lftp-pilot";

    public static bool TryParse(Uri uri, out ProtocolActivationRequest? request)
    {
        request = null;
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath.Length > 1 && uri.AbsolutePath != "/")) return false;

        Dictionary<string, string> parameters;
        try { parameters = ParseQuery(uri.Query); }
        catch (UriFormatException) { return false; }
        string action = uri.Host.Trim().ToLowerInvariant();
        switch (action)
        {
            case "open-profile" when parameters.Count == 1 &&
                parameters.TryGetValue("id", out string? id) && Guid.TryParse(id, out Guid profileId) && profileId != Guid.Empty:
                request = new(ProtocolActivationAction.OpenProfile, profileId);
                return true;
            case "transfers" when parameters.Count == 0:
                request = new(ProtocolActivationAction.ShowTransfers);
                return true;
            case "settings" when parameters.Count == 0:
                request = new(ProtocolActivationAction.OpenSettings);
                return true;
            default:
                return false;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = item.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0]);
            string value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            if (key.Length is 0 or > 32 || value.Length > 128 || !result.TryAdd(key, value))
                return new(StringComparer.OrdinalIgnoreCase) { ["__invalid"] = string.Empty };
        }
        return result;
    }
}
