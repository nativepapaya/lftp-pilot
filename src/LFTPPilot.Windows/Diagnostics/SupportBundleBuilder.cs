using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Windows.Diagnostics;

public sealed record SupportBundleText(string Name, string Content);

public sealed class SupportBundleBuilder
{
    internal static readonly DateTimeOffset DeterministicZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int MaximumEntryBytes = 4 * 1024 * 1024;
    private const int MaximumTotalBytes = 32 * 1024 * 1024;
    private const int MaximumEntries = 128;

    public async Task CreateAsync(
        string destination,
        IReadOnlyDictionary<string, object?> metadata,
        IEnumerable<SupportBundleText> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(entries);
        string fullPath = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("A parent directory is required.", nameof(destination));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        int total = 0;
        try
        {
            await using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                string metadataJson = SupportSanitizer.Redact(
                    JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                total = Encoding.UTF8.GetByteCount(metadataJson);
                if (total > MaximumEntryBytes) throw new InvalidDataException("Support bundle metadata exceeds its bounded size.");
                await WriteEntryAsync(archive, "metadata.json", metadataJson, cancellationToken);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.json" };
                int count = 0;
                foreach (SupportBundleText entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry is null) throw new ArgumentException("Support bundle entries cannot be null.", nameof(entries));
                    if (++count > MaximumEntries) throw new InvalidDataException("The support bundle has too many entries.");
                    ValidateName(entry.Name);
                    string normalizedName = entry.Name.Replace('\\', '/');
                    if (!names.Add(normalizedName)) throw new ArgumentException("Support bundle entry names must be unique.", nameof(entries));
                    string content = SupportSanitizer.Redact(entry.Content);
                    int size = Encoding.UTF8.GetByteCount(content);
                    if (size > MaximumEntryBytes || total + size > MaximumTotalBytes)
                        throw new InvalidDataException("The support bundle exceeds its bounded size.");
                    total += size;
                    await WriteEntryAsync(archive, normalizedName, content, cancellationToken);
                }
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            output.Close();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = DeterministicZipTimestamp;
        await using Stream stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: false);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Replace('\\', '/');
        string[] segments = normalized.Split('/');
        if (normalized.Length > 240 || Path.IsPathRooted(name) || normalized.StartsWith('/') ||
            segments.Any(static segment => segment is "" or "." or ".."))
            throw new ArgumentException("Support bundle entry names must be relative.", nameof(name));
        string extension = Path.GetExtension(name);
        if (!new[] { ".json", ".log", ".txt", ".md" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Only text diagnostics may be included in a support bundle.", nameof(name));
    }
}
