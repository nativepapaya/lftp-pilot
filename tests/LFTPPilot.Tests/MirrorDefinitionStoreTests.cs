using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using LFTPPilot.Core;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Tests;

public sealed class MirrorDefinitionStoreTests
{
    private static readonly JsonSerializerOptions DurableJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [Fact]
    public async Task PackageScopedStoreAtomicallyRoundTripsReplacesOrdersAndDeletesDefinitions()
    {
        using var directory = new TestDirectory();
        var paths = Paths(directory.Path);
        paths.EnsureCreated();
        Assert.True(Directory.Exists(paths.MirrorDefinitions));
        Assert.True(MirrorDefinitionPolicy.MaximumSerializedStoreBytes <= AgentProtocol.MaximumFrameBytes / 4);
        var path = Path.Combine(paths.MirrorDefinitions, JsonMirrorDefinitionStore.FileName);
        var store = new JsonMirrorDefinitionStore(paths);
        var zulu = Definition(directory.Path, "Zulu") with
        {
            Includes = ["*.zip"],
            Excludes = ["cache/**"],
            DeleteExtraneous = true,
            ParallelFiles = 4,
            SegmentsPerFile = 3,
            RateLimitBytesPerSecond = 12_345_678,
        };
        var alpha = Definition(directory.Path, "alpha");

        await store.SaveAsync(zulu, TestContext.Current.CancellationToken);
        await store.SaveAsync(alpha, TestContext.Current.CancellationToken);

        var firstRead = await store.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["alpha", "Zulu"], firstRead.Select(static definition => definition.Name));
        AssertDefinitionEqual(zulu, Assert.Single(firstRead, definition => definition.Id == zulu.Id));
        Assert.Empty(Directory.GetFiles(paths.MirrorDefinitions, "*.tmp"));

        var renamed = zulu with { Name = "Beta", ParallelFiles = 6 };
        await store.SaveAsync(renamed, TestContext.Current.CancellationToken);
        await store.DeleteAsync(alpha.Id, TestContext.Current.CancellationToken);

        AssertDefinitionEqual(renamed, Assert.Single(await store.GetAllAsync(TestContext.Current.CancellationToken)));
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        var persisted = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(
            [
                "deleteExtraneous", "direction", "excludes", "id", "includes", "localRoot", "name",
                "parallelFiles", "profileId", "rateLimitBytesPerSecond", "remoteRoot", "segmentsPerFile",
            ],
            persisted.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        var serialized = json.RootElement.GetRawText();
        Assert.DoesNotContain("preview", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("action", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approval", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentStoreInstancesDoNotLoseDefinitionsAndCancellationDoesNotMutateState()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "state", JsonMirrorDefinitionStore.FileName);
        var definitions = Enumerable.Range(0, 64)
            .Select(index => Definition(directory.Path, $"Mirror {index:D3}"))
            .ToArray();

        await Task.WhenAll(definitions.Select((definition, index) =>
            new JsonMirrorDefinitionStore(path).SaveAsync(definition, TestContext.Current.CancellationToken)));

        var store = new JsonMirrorDefinitionStore(path);
        var saved = await store.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(definitions.Select(static definition => definition.Id).Order(), saved.Select(static definition => definition.Id).Order());
        Assert.Equal(definitions.Select(static definition => definition.Name), saved.Select(static definition => definition.Name));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            Definition(directory.Path, "Cancelled save"),
            cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(
            definitions[0].Id,
            cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAllAsync(cancelled.Token));
        Assert.Equal(64, (await store.GetAllAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task StoreRejectsInvalidDefinitionsBeforeWriting()
    {
        using var directory = new TestDirectory();
        var store = new JsonMirrorDefinitionStore(Path.Combine(directory.Path, JsonMirrorDefinitionStore.FileName));
        var valid = Definition(directory.Path, "Valid");
        var invalid = new[]
        {
            valid with { Id = Guid.Empty },
            valid with { ProfileId = Guid.Empty },
            valid with { Name = "   " },
            valid with { LocalRoot = "relative" },
            valid with { RemoteRoot = "/remote/../escape" },
            valid with { Includes = Enumerable.Repeat("*.tmp", MirrorDefinitionPolicy.MaximumPatternsPerList + 1).ToImmutableArray() },
            valid with { Excludes = [new string('x', MirrorDefinitionPolicy.MaximumPatternLength + 1)] },
            valid with { Includes = ["bad\u001econtrol"] },
            valid with { Includes = Enumerable.Repeat(new string('x', 1024), 65).ToImmutableArray() },
            valid with { ParallelFiles = MirrorDefinitionPolicy.MaximumParallelFiles + 1 },
            valid with { SegmentsPerFile = MirrorDefinitionPolicy.MaximumSegmentsPerFile + 1 },
            valid with { RateLimitBytesPerSecond = MirrorDefinitionPolicy.MaximumRateLimitBytesPerSecond + 1 },
        };

        foreach (var definition in invalid)
        {
            await Assert.ThrowsAsync<ModelValidationException>(() => store.SaveAsync(
                definition,
                TestContext.Current.CancellationToken));
        }

        Assert.False(File.Exists(Path.Combine(directory.Path, JsonMirrorDefinitionStore.FileName)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(
            Guid.Empty,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoreFailsClosedForDuplicateMalformedOrExpandedDurableEntries()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, JsonMirrorDefinitionStore.FileName);
        var store = new JsonMirrorDefinitionStore(path);
        var definition = Definition(directory.Path, "Durable");

        await WriteAsync(path, SerializeDefinitions(definition, definition));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync(TestContext.Current.CancellationToken));

        await WriteAsync(path, SerializeDefinitions(
            definition,
            definition with { Id = Guid.NewGuid(), Name = definition.Name.ToUpperInvariant() }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync(TestContext.Current.CancellationToken));

        var validJson = SerializeDefinitions(definition);
        await WriteAsync(path, validJson.Replace(
            "\"name\":",
            "\"approvalToken\": \"must-not-persist\", \"name\":",
            StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync(TestContext.Current.CancellationToken));

        await WriteAsync(path, validJson.Replace(
            "\"name\":",
            "\"name\": \"shadow\", \"name\":",
            StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync(TestContext.Current.CancellationToken));

        await WriteAsync(path, JsonSerializer.Serialize(new[]
        {
            new
            {
                definition.Id,
                definition.ProfileId,
                definition.Name,
                definition.LocalRoot,
                definition.RemoteRoot,
            },
        }, DurableJsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync(TestContext.Current.CancellationToken));

        await WriteAsync(path, "[{ not-json }]");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync(TestContext.Current.CancellationToken));

        await File.WriteAllBytesAsync(
            path,
            new byte[MirrorDefinitionPolicy.MaximumSerializedStoreBytes + 1],
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoreRejectsDuplicateProfileNamesAndAggregatePatternOverflowWithoutLosingState()
    {
        using var directory = new TestDirectory();
        var store = new JsonMirrorDefinitionStore(Path.Combine(directory.Path, JsonMirrorDefinitionStore.FileName));
        var first = Definition(directory.Path, "Shared name");
        await store.SaveAsync(first, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            first with { Id = Guid.NewGuid(), Name = "SHARED NAME" },
            TestContext.Current.CancellationToken));

        var patternSet = Enumerable.Repeat(new string('x', 4000), 13).ToImmutableArray();
        var patterned = Definition(directory.Path, "Pattern one") with { Includes = patternSet };
        await store.SaveAsync(patterned, TestContext.Current.CancellationToken);
        await store.SaveAsync(Definition(directory.Path, "Pattern two") with { Includes = patternSet }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            Definition(directory.Path, "Pattern overflow") with { Includes = patternSet },
            TestContext.Current.CancellationToken));

        var saved = await store.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, saved.Count);
        Assert.DoesNotContain(saved, static definition => definition.Name == "Pattern overflow");
    }

    [Fact]
    public async Task StoreRejectsDirectoryAtJsonFilePath()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, JsonMirrorDefinitionStore.FileName);
        Directory.CreateDirectory(path);
        var store = new JsonMirrorDefinitionStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            Definition(directory.Path, "Blocked directory"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoreRejectsReparseAncestorBeforeWritingOutsideItsRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory();
        var target = Path.Combine(directory.Path, "outside-target");
        var junction = Path.Combine(directory.Path, "store-link");
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);
        try
        {
            var store = new JsonMirrorDefinitionStore(Path.Combine(junction, JsonMirrorDefinitionStore.FileName));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
                Definition(directory.Path, "Blocked junction"),
                TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    private static Task WriteAsync(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
    }

    private static string SerializeDefinitions(params MirrorDefinition[] definitions) => JsonSerializer.Serialize(
        definitions.Select(DurableDefinition.FromDefinition),
        DurableJsonOptions);

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("The directory-junction test helper did not start.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"The directory-junction test helper failed: {process.StandardError.ReadToEnd()}");
        Assert.True((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0);
    }

    private static PackageDataPaths Paths(string root) => new(
        Path.Combine(root, "LocalState"),
        Path.Combine(root, "LocalCache"),
        Path.Combine(root, "TempState"),
        false);

    private static MirrorDefinition Definition(string localRoot, string name) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        name,
        MirrorDirection.Download,
        localRoot,
        "/remote");

    private static void AssertDefinitionEqual(MirrorDefinition expected, MirrorDefinition actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ProfileId, actual.ProfileId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Direction, actual.Direction);
        Assert.Equal(expected.LocalRoot, actual.LocalRoot);
        Assert.Equal(expected.RemoteRoot, actual.RemoteRoot);
        Assert.Equal(expected.EffectiveIncludes.ToArray(), actual.EffectiveIncludes.ToArray());
        Assert.Equal(expected.EffectiveExcludes.ToArray(), actual.EffectiveExcludes.ToArray());
        Assert.Equal(expected.DeleteExtraneous, actual.DeleteExtraneous);
        Assert.Equal(expected.ParallelFiles, actual.ParallelFiles);
        Assert.Equal(expected.SegmentsPerFile, actual.SegmentsPerFile);
        Assert.Equal(expected.RateLimitBytesPerSecond, actual.RateLimitBytesPerSecond);
    }

    private sealed record DurableDefinition(
        Guid Id,
        Guid ProfileId,
        string Name,
        MirrorDirection Direction,
        string LocalRoot,
        string RemoteRoot,
        ImmutableArray<string> Includes,
        ImmutableArray<string> Excludes,
        bool DeleteExtraneous,
        int ParallelFiles,
        int SegmentsPerFile,
        long? RateLimitBytesPerSecond)
    {
        public static DurableDefinition FromDefinition(MirrorDefinition definition) => new(
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

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "lftp-pilot-mirror-store-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
