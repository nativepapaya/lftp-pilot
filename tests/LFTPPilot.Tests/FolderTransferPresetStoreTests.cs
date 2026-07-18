using System.Diagnostics;
using LFTPPilot.Core;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Tests;

public sealed class FolderTransferPresetStoreTests
{
    [Fact]
    public async Task PresetsRoundTripInNameOrderAndSupportUpdateAndDelete()
    {
        using var directory = new TestDirectory();
        var store = new JsonFolderTransferPresetStore(Path.Combine(directory.Path, "presets.json"));
        var zulu = new FolderTransferPreset(
            Guid.NewGuid(), "Zulu", ["*.zip"], ["cache/**"], 4, 8);
        var alpha = new FolderTransferPreset(
            Guid.NewGuid(), "Alpha", ["docs/**"], [], 2, 3);

        await store.SaveAsync(zulu, TestContext.Current.CancellationToken);
        await store.SaveAsync(alpha, TestContext.Current.CancellationToken);
        AssertPresetsEqual([alpha, zulu], await store.GetAllAsync(TestContext.Current.CancellationToken));

        var updated = zulu with { Name = "Beta", ParallelFiles = 6 };
        await store.SaveAsync(updated, TestContext.Current.CancellationToken);
        AssertPresetsEqual([alpha, updated], await store.GetAllAsync(TestContext.Current.CancellationToken));
        await store.DeleteAsync(alpha.Id, TestContext.Current.CancellationToken);
        AssertPresetsEqual([updated], await store.GetAllAsync(TestContext.Current.CancellationToken));
    }

    private static void AssertPresetsEqual(
        IReadOnlyList<FolderTransferPreset> expected,
        IReadOnlyList<FolderTransferPreset> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Id, actual[index].Id);
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].ParallelFiles, actual[index].ParallelFiles);
            Assert.Equal(expected[index].DownloadSegmentsPerFile, actual[index].DownloadSegmentsPerFile);
            Assert.Equal(expected[index].EffectiveIncludes, actual[index].EffectiveIncludes);
            Assert.Equal(expected[index].EffectiveExcludes, actual[index].EffectiveExcludes);
        }
    }

    [Fact]
    public async Task PresetStoreRejectsDuplicateNamesAndUnmappedJson()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "presets.json");
        var store = new JsonFolderTransferPresetStore(path);
        var first = new FolderTransferPreset(Guid.NewGuid(), "Release", ["*.msix"]);
        await store.SaveAsync(first, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            first with { Id = Guid.NewGuid(), Name = "release" },
            TestContext.Current.CancellationToken));

        await File.WriteAllTextAsync(path,
            "[{\"id\":\"43b65c38-d4b6-4e3d-a279-29de12809fc9\",\"name\":\"Unsafe\",\"unknown\":true}]",
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAllAsync(TestContext.Current.CancellationToken));

        await File.WriteAllTextAsync(path,
            "[{\"id\":\"43b65c38-d4b6-4e3d-a279-29de12809fc9\",\"name\":\"First\",\"name\":\"Shadow\"}]",
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAllAsync(TestContext.Current.CancellationToken));

        await File.WriteAllBytesAsync(
            path,
            new byte[FolderTransferPolicy.MaximumSerializedStoreBytes + 1],
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PresetStoreRejectsDirectoryAndReparseDestinations()
    {
        using var directory = new TestDirectory();
        var directoryPath = Path.Combine(directory.Path, "as-directory.json");
        Directory.CreateDirectory(directoryPath);
        var directoryStore = new JsonFolderTransferPresetStore(directoryPath);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            directoryStore.GetAllAsync(TestContext.Current.CancellationToken));

        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(directory.Path, "outside-target");
        var junction = Path.Combine(directory.Path, "store-link");
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);
        try
        {
            var store = new JsonFolderTransferPresetStore(Path.Combine(junction, "presets.json"));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
                new(Guid.NewGuid(), "Blocked"), TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

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

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "LFTPPilot.FolderPresetTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
