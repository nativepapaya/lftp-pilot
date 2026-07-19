using LFTPPilot.App.Infrastructure;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class FileEntryViewModel(FileEntry entry, double rowHeight = 34, bool showDetailLine = false) : ObservableObject
{
    private bool _isSelected;

    public FileEntry Entry { get; } = entry;
    public double RowHeight { get; } = rowHeight;
    public bool ShowDetailLine { get; } = showDetailLine;
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public bool IsDirectory => Entry.IsDirectory;
    public string Glyph => Entry.Kind switch
    {
        EntryKind.Directory => "\uE8B7",
        EntryKind.SymbolicLink => "\uE71B",
        _ => "\uE8A5",
    };
    public string TypeDisplay => Entry.Kind switch
    {
        EntryKind.Directory => "Folder",
        EntryKind.SymbolicLink => "Link",
        _ => Path.GetExtension(Entry.Name) is { Length: > 1 } extension ? extension[1..].ToUpperInvariant() : "File",
    };
    public string SizeDisplay => Entry.Size is null ? string.Empty : FormatSize(Entry.Size.Value);
    public string ModifiedDisplay => Entry.ModifiedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
    public string DetailDisplay => string.Join("  ", new[] { Entry.Permissions, Entry.Owner }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[index]}";
    }
}
