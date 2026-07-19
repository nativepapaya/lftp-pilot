using LFTPPilot.Core;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Tests;

public sealed class AppPreferencesStoreTests
{
    [Fact]
    public async Task PreferencesRoundTripThroughAtomicPackageStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "lftp-pilot-preferences-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "ui-preferences.json");
            var store = new JsonAppPreferencesStore(path);
            var expected = new AppPreferences(
                AppThemePreference.Dark,
                FileListDensity.Comfortable,
                ShowHiddenLocal: true,
                ShowHiddenRemote: false,
                ExpandActivityCenter: false,
                DefaultParallelFiles: 6,
                DefaultDownloadSegments: 8,
                DownloadLimitKiB: 2048,
                UploadLimitKiB: 1024);

            await store.SaveAsync(expected, TestContext.Current.CancellationToken);
            var actual = await store.GetAsync(TestContext.Current.CancellationToken);

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreferencesRejectOutOfRangeTransferDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "lftp-pilot-preferences-tests", Guid.NewGuid().ToString("N"), "ui-preferences.json");
        var store = new JsonAppPreferencesStore(path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            new AppPreferences(DefaultParallelFiles: 17),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreferencesFailClosedOnMalformedJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "lftp-pilot-preferences-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "ui-preferences.json");
            await File.WriteAllTextAsync(path, "{ definitely-not-json", TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() => new JsonAppPreferencesStore(path).GetAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
