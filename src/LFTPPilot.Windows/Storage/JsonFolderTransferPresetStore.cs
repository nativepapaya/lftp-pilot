using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using LFTPPilot.Core;

namespace LFTPPilot.Windows.Storage;

public sealed class JsonFolderTransferPresetStore : IFolderTransferPresetStore
{
    public const string FileName = "folder-transfer-presets.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly HashSet<string> PresetProperties = new(StringComparer.Ordinal)
    {
        "id",
        "name",
        "includes",
        "excludes",
        "parallelFiles",
        "downloadSegmentsPerFile",
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        IgnoreReadOnlyProperties = true,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public JsonFolderTransferPresetStore(PackageDataPaths paths)
        : this(Path.Combine(
            (paths ?? throw new ArgumentNullException(nameof(paths))).FolderTransferPresets,
            FileName))
    {
    }

    public JsonFolderTransferPresetStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        if (_path.Length > 32_767 || _path.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            _path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            _path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The folder-transfer preset store requires a bounded non-device path.", nameof(path));
        }

        _gate = Gates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<FolderTransferPreset>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        FolderTransferPreset preset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlanValidator.Validate(preset);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var presets = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (presets.Any(item => item.Id != preset.Id &&
                string.Equals(item.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("A folder-transfer preset with this name already exists.");
            }

            var index = presets.FindIndex(item => item.Id == preset.Id);
            if (index >= 0)
            {
                presets[index] = preset;
            }
            else
            {
                if (presets.Count >= FolderTransferPolicy.MaximumPresets)
                    throw new InvalidDataException("The folder-transfer preset limit has been reached.");
                presets.Add(preset);
            }

            await WriteAsync(presets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid presetId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (presetId == Guid.Empty)
            throw new ArgumentException("The folder-transfer preset identifier cannot be empty.", nameof(presetId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var presets = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (presets.RemoveAll(item => item.Id == presetId) > 0)
                await WriteAsync(presets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<FolderTransferPreset>> ReadAsync(CancellationToken cancellationToken)
    {
        ValidateSafeStorePath();
        if (!File.Exists(_path)) return [];

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > FolderTransferPolicy.MaximumSerializedStoreBytes)
                throw new InvalidDataException("The folder-transfer preset store exceeds its size limit.");

            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            ValidateDocument(document);

            var persisted = JsonSerializer.Deserialize<List<PersistedFolderTransferPreset?>>(bytes, _options)
                ?? throw new InvalidDataException("The folder-transfer preset store is empty.");
            var presets = new List<FolderTransferPreset>(persisted.Count);
            foreach (var persistedPreset in persisted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (persistedPreset is null)
                    throw new InvalidDataException("The folder-transfer preset store contains a null record.");
                var preset = persistedPreset.ToPreset();
                try
                {
                    PlanValidator.Validate(preset);
                }
                catch (ModelValidationException exception)
                {
                    throw new InvalidDataException("The folder-transfer preset store contains an invalid record.", exception);
                }
                presets.Add(preset);
            }

            ValidateCollection(presets);
            Sort(presets);
            return presets;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The folder-transfer preset store contains invalid JSON.", exception);
        }
    }

    private async Task WriteAsync(List<FolderTransferPreset> presets, CancellationToken cancellationToken)
    {
        ValidateCollection(presets);
        Sort(presets);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            presets.Select(PersistedFolderTransferPreset.FromPreset).ToArray(),
            _options);
        if (bytes.Length > FolderTransferPolicy.MaximumSerializedStoreBytes)
            throw new InvalidDataException("The folder-transfer preset store exceeds its size limit.");

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The folder-transfer preset store requires a parent directory.");
        ValidateNoReparseAncestors(directory);
        Directory.CreateDirectory(directory);
        ValidateSafeStorePath();
        await AtomicFile.WriteBytesAsync(_path, bytes, cancellationToken).ConfigureAwait(false);
        ValidateSafeStorePath(requireExistingFile: true);
    }

    private static void ValidateDocument(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The folder-transfer preset store root must be an array.");
        if (document.RootElement.GetArrayLength() > FolderTransferPolicy.MaximumPresets)
            throw new InvalidDataException("The folder-transfer preset store contains too many records.");

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Every folder-transfer preset store entry must be an object.");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                    throw new InvalidDataException("A folder-transfer preset store entry contains a duplicate property.");
                if (!PresetProperties.Contains(property.Name))
                    throw new InvalidDataException(
                        $"A folder-transfer preset store entry contains the unsupported property '{property.Name}'.");
            }
        }
    }

    private static void ValidateCollection(IReadOnlyCollection<FolderTransferPreset> presets)
    {
        if (presets.Count > FolderTransferPolicy.MaximumPresets)
            throw new InvalidDataException("The folder-transfer preset store exceeds its record limit.");
        var identifiers = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long patternCharacters = 0;
        foreach (var preset in presets)
        {
            PlanValidator.Validate(preset);
            if (!identifiers.Add(preset.Id))
                throw new InvalidDataException("The folder-transfer preset store contains a duplicate identifier.");
            if (!names.Add(preset.Name))
                throw new InvalidDataException("The folder-transfer preset store contains a duplicate name.");
            patternCharacters += preset.EffectiveIncludes.Sum(static value => (long)value.Length);
            patternCharacters += preset.EffectiveExcludes.Sum(static value => (long)value.Length);
            if (patternCharacters > FolderTransferPolicy.MaximumAggregatePatternCharacters)
                throw new InvalidDataException("The folder-transfer preset store exceeds its aggregate pattern limit.");
        }
    }

    private void ValidateSafeStorePath(bool requireExistingFile = false)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The folder-transfer preset store requires a parent directory.");
        ValidateNoReparseAncestors(directory);
        if (!File.Exists(_path))
        {
            if (Directory.Exists(_path))
                throw new InvalidDataException("The folder-transfer preset store path cannot be a directory.");
            if (requireExistingFile)
                throw new InvalidDataException("The folder-transfer preset store write did not create a regular file.");
            return;
        }

        if ((File.GetAttributes(_path) &
            (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("The folder-transfer preset store must be a regular non-reparse file.");
        }
    }

    private static void ValidateNoReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The folder-transfer preset store path cannot contain a reparse point.");
            }
        }
    }

    private static void Sort(List<FolderTransferPreset> presets)
    {
        presets.Sort(static (left, right) =>
        {
            var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (name != 0) return name;
            name = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return name != 0 ? name : left.Id.CompareTo(right.Id);
        });
    }

    private sealed record PersistedFolderTransferPreset(
        Guid Id,
        string Name,
        ImmutableArray<string> Includes = default,
        ImmutableArray<string> Excludes = default,
        int ParallelFiles = 2,
        int DownloadSegmentsPerFile = 4)
    {
        public FolderTransferPreset ToPreset() => new(
            Id,
            Name,
            Includes.IsDefault ? [] : Includes,
            Excludes.IsDefault ? [] : Excludes,
            ParallelFiles,
            DownloadSegmentsPerFile);

        public static PersistedFolderTransferPreset FromPreset(FolderTransferPreset preset) => new(
            preset.Id,
            preset.Name,
            preset.EffectiveIncludes,
            preset.EffectiveExcludes,
            preset.ParallelFiles,
            preset.DownloadSegmentsPerFile);
    }
}
