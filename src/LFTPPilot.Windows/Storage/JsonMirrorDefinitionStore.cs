using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LFTPPilot.Core;

namespace LFTPPilot.Windows.Storage;

public sealed class JsonMirrorDefinitionStore : IMirrorDefinitionStore
{
    public const string FileName = "mirror-definitions.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly HashSet<string> DefinitionProperties = new(StringComparer.Ordinal)
    {
        "id",
        "profileId",
        "name",
        "direction",
        "localRoot",
        "remoteRoot",
        "includes",
        "excludes",
        "deleteExtraneous",
        "parallelFiles",
        "segmentsPerFile",
        "rateLimitBytesPerSecond",
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

    public JsonMirrorDefinitionStore(PackageDataPaths paths)
        : this(Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).MirrorDefinitions, FileName))
    {
    }

    public JsonMirrorDefinitionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        if (_path.Length > 32_767 || _path.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            _path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            _path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The mirror-definition store requires a bounded non-device path.", nameof(path));
        }

        _gate = Gates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<MirrorDefinition>> GetAllAsync(
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
        MirrorDefinition definition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlanValidator.Validate(definition);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var definitions = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = definitions.FindIndex(item => item.Id == definition.Id);
            if (index >= 0)
            {
                definitions[index] = definition;
            }
            else
            {
                if (definitions.Count >= MirrorDefinitionPolicy.MaximumDefinitions)
                    throw new InvalidDataException("The mirror-definition store has reached its record limit.");
                definitions.Add(definition);
            }

            await WriteAsync(definitions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (definitionId == Guid.Empty)
            throw new ArgumentException("The mirror identifier cannot be empty.", nameof(definitionId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var definitions = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (definitions.RemoveAll(item => item.Id == definitionId) > 0)
                await WriteAsync(definitions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<MirrorDefinition>> ReadAsync(CancellationToken cancellationToken)
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
            if (stream.Length > MirrorDefinitionPolicy.MaximumSerializedStoreBytes)
                throw new InvalidDataException("The mirror-definition store exceeds its size limit.");

            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            ValidateDocument(document);

            var persistedDefinitions = JsonSerializer.Deserialize<List<PersistedMirrorDefinition?>>(bytes, _options)
                ?? throw new InvalidDataException("The mirror-definition store is empty.");
            if (persistedDefinitions.Count > MirrorDefinitionPolicy.MaximumDefinitions)
                throw new InvalidDataException("The mirror-definition store contains too many records.");

            var validated = new List<MirrorDefinition>(persistedDefinitions.Count);
            var identifiers = new HashSet<Guid>();
            var names = new Dictionary<Guid, HashSet<string>>();
            foreach (var persistedDefinition in persistedDefinitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (persistedDefinition is null)
                    throw new InvalidDataException("The mirror-definition store contains a null record.");
                var definition = persistedDefinition.ToDefinition();
                try
                {
                    PlanValidator.Validate(definition);
                }
                catch (ModelValidationException exception)
                {
                    throw new InvalidDataException("The mirror-definition store contains an invalid record.", exception);
                }

                if (!identifiers.Add(definition.Id))
                    throw new InvalidDataException("The mirror-definition store contains a duplicate identifier.");
                if (!names.TryGetValue(definition.ProfileId, out var profileNames))
                {
                    profileNames = new(StringComparer.OrdinalIgnoreCase);
                    names.Add(definition.ProfileId, profileNames);
                }
                if (!profileNames.Add(definition.Name))
                    throw new InvalidDataException("The mirror-definition store contains a duplicate profile name.");
                validated.Add(definition);
            }

            ValidateAggregatePatternCharacters(validated);

            Sort(validated);
            return validated;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The mirror-definition store contains invalid JSON.", exception);
        }
    }

    private async Task WriteAsync(List<MirrorDefinition> definitions, CancellationToken cancellationToken)
    {
        if (definitions.Count > MirrorDefinitionPolicy.MaximumDefinitions)
            throw new InvalidDataException("The mirror-definition store contains too many records.");

        var identifiers = new HashSet<Guid>();
        var names = new Dictionary<Guid, HashSet<string>>();
        foreach (var definition in definitions)
        {
            PlanValidator.Validate(definition);
            if (!identifiers.Add(definition.Id))
                throw new InvalidDataException("The mirror-definition store contains a duplicate identifier.");
            if (!names.TryGetValue(definition.ProfileId, out var profileNames))
            {
                profileNames = new(StringComparer.OrdinalIgnoreCase);
                names.Add(definition.ProfileId, profileNames);
            }
            if (!profileNames.Add(definition.Name))
                throw new InvalidDataException("The mirror-definition store contains a duplicate profile name.");
        }
        ValidateAggregatePatternCharacters(definitions);

        Sort(definitions);
        var persistedDefinitions = definitions.Select(PersistedMirrorDefinition.FromDefinition).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(persistedDefinitions, _options);
        if (bytes.Length > MirrorDefinitionPolicy.MaximumSerializedStoreBytes)
            throw new InvalidDataException("The mirror-definition store exceeds its size limit.");

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The mirror-definition store requires a parent directory.");
        ValidateNoReparseAncestors(directory);
        Directory.CreateDirectory(directory);
        ValidateSafeStorePath();
        await AtomicFile.WriteBytesAsync(_path, bytes, cancellationToken).ConfigureAwait(false);
        ValidateSafeStorePath(requireExistingFile: true);
    }

    private static void ValidateDocument(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The mirror-definition store root must be an array.");
        if (document.RootElement.GetArrayLength() > MirrorDefinitionPolicy.MaximumDefinitions)
            throw new InvalidDataException("The mirror-definition store contains too many records.");

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Every mirror-definition store entry must be an object.");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                    throw new InvalidDataException("A mirror-definition store entry contains a duplicate property.");
                if (!DefinitionProperties.Contains(property.Name))
                    throw new InvalidDataException("A mirror-definition store entry contains an unsupported property.");
            }
        }
    }

    private void ValidateSafeStorePath(bool requireExistingFile = false)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The mirror-definition store requires a parent directory.");
        ValidateNoReparseAncestors(directory);
        if (!File.Exists(_path))
        {
            if (Directory.Exists(_path))
                throw new InvalidDataException("The mirror-definition store path cannot be a directory.");
            if (requireExistingFile)
                throw new InvalidDataException("The mirror-definition store write did not create a regular file.");
            return;
        }

        if ((File.GetAttributes(_path) &
            (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("The mirror-definition store must be a regular non-reparse file.");
        }
    }

    private static void ValidateNoReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The mirror-definition store path cannot contain a reparse point.");
            }
        }
    }

    private static void Sort(List<MirrorDefinition> definitions)
    {
        definitions.Sort(static (left, right) =>
        {
            var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (name != 0) return name;
            name = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return name != 0 ? name : left.Id.CompareTo(right.Id);
        });
    }

    private static void ValidateAggregatePatternCharacters(IEnumerable<MirrorDefinition> definitions)
    {
        long aggregateCharacters = 0;
        foreach (var definition in definitions)
        {
            aggregateCharacters += definition.EffectiveIncludes.Sum(static pattern => (long)(pattern?.Length ?? 0));
            aggregateCharacters += definition.EffectiveExcludes.Sum(static pattern => (long)(pattern?.Length ?? 0));
            if (aggregateCharacters > MirrorDefinitionPolicy.MaximumAggregatePatternCharacters)
            {
                throw new InvalidDataException(
                    $"The mirror-definition store exceeds its {MirrorDefinitionPolicy.MaximumAggregatePatternCharacters}-character pattern limit.");
            }
        }
    }

    private sealed record PersistedMirrorDefinition(
        Guid Id,
        Guid ProfileId,
        string Name,
        MirrorDirection Direction,
        string LocalRoot,
        string RemoteRoot,
        ImmutableArray<string> Includes = default,
        ImmutableArray<string> Excludes = default,
        bool DeleteExtraneous = false,
        int ParallelFiles = 2,
        int SegmentsPerFile = 1,
        long? RateLimitBytesPerSecond = null)
    {
        public MirrorDefinition ToDefinition() => new(
            Id,
            ProfileId,
            Name,
            Direction,
            LocalRoot,
            RemoteRoot,
            Includes,
            Excludes,
            DeleteExtraneous,
            ParallelFiles,
            SegmentsPerFile,
            RateLimitBytesPerSecond);

        public static PersistedMirrorDefinition FromDefinition(MirrorDefinition definition) => new(
            definition.Id,
            definition.ProfileId,
            definition.Name,
            definition.Direction,
            definition.LocalRoot,
            definition.RemoteRoot,
            definition.EffectiveIncludes,
            definition.EffectiveExcludes,
            definition.DeleteExtraneous,
            definition.ParallelFiles,
            definition.SegmentsPerFile,
            definition.RateLimitBytesPerSecond);
    }
}
