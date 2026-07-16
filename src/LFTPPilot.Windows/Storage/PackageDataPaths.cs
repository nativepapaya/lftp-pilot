using System.Runtime.InteropServices;
using System.Text;

namespace LFTPPilot.Windows.Storage;

public sealed record PackageDataPaths(string LocalState, string LocalCache, string Temporary, bool IsPackaged)
{
    public string Profiles => Path.Combine(LocalState, "profiles");
    public string Secrets => Path.Combine(LocalState, "secrets");
    public string History => Path.Combine(LocalState, "history");
    public string RuntimeHome => Path.Combine(LocalState, "lftp-home");
    public string RemoteEdits => Path.Combine(LocalCache, "remote-edits");

    public static PackageDataPaths CreateDefault()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? family = TryGetPackageFamilyName();
        string root = family is null ? Path.Combine(local, "LFTP Pilot", "Development") : Path.Combine(local, "Packages", family);
        return new(Path.Combine(root, "LocalState"), Path.Combine(root, "LocalCache"), Path.Combine(root, "TempState"), family is not null);
    }

    public void EnsureCreated()
    {
        foreach (string path in new[] { LocalState, LocalCache, Temporary, Profiles, Secrets, History, RuntimeHome, RemoteEdits })
            Directory.CreateDirectory(path);
    }

    private static string? TryGetPackageFamilyName()
    {
        uint length = 0;
        int result = GetCurrentPackageFamilyName(ref length, null);
        if (result == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
        if (result != 122 || length is 0 or > 256)
            throw new InvalidOperationException($"Unable to resolve package identity (Win32 error {result}).");
        var value = new StringBuilder((int)length);
        result = GetCurrentPackageFamilyName(ref length, value);
        if (result != 0) throw new InvalidOperationException($"Unable to resolve package family name (Win32 error {result}).");
        return value.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);
}
