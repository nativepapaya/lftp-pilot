using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class FilePaneDragDropRegistryTests
{
    private static readonly Guid SessionId = Guid.Parse("c69a5527-77ee-4e41-bc4c-0f09de893181");

    [Fact]
    public void OpaqueTokenCarriesTypedSourcesOnlyToOppositePaneOfOriginatingSession()
    {
        var registry = CreateRegistry();
        FilePaneTransferSource[] sources =
        [
            new(@"C:\staging\package.msix", TransferSourceKind.File),
            new(@"C:\staging\symbols", TransferSourceKind.Directory),
        ];

        Assert.True(registry.TryIssue(SessionId, PaneKind.Local, sources, out var token));
        Assert.Equal(32, token.Length);
        Assert.DoesNotContain("staging", token, StringComparison.OrdinalIgnoreCase);
        Assert.True(registry.CanAccept(token, SessionId, PaneKind.Remote));

        Assert.True(registry.TryConsume(token, SessionId, PaneKind.Remote, out var payload));
        Assert.Equal(SessionId, payload!.SessionId);
        Assert.Equal(PaneKind.Local, payload.SourcePane);
        Assert.Equal(sources, payload.Sources);
        Assert.False(registry.TryConsume(token, SessionId, PaneKind.Remote, out _));
    }

    [Fact]
    public void SamePaneCrossSessionAndUnknownTokensAreRejected()
    {
        var registry = CreateRegistry();
        Assert.True(registry.TryIssue(
            SessionId,
            PaneKind.Remote,
            [new("/release/app.msix", TransferSourceKind.File)],
            out var samePaneToken));
        Assert.False(registry.CanAccept(samePaneToken, SessionId, PaneKind.Remote));
        Assert.False(registry.TryConsume(samePaneToken, SessionId, PaneKind.Remote, out _));
        Assert.True(registry.TryConsume(samePaneToken, SessionId, PaneKind.Local, out _));
        Assert.False(registry.TryConsume(samePaneToken, SessionId, PaneKind.Local, out _));

        Assert.True(registry.TryIssue(
            SessionId,
            PaneKind.Remote,
            [new("/release/app.msix", TransferSourceKind.File)],
            out var crossSessionToken));
        Assert.False(registry.CanAccept(crossSessionToken, Guid.NewGuid(), PaneKind.Local));
        Assert.False(registry.TryConsume(crossSessionToken, Guid.NewGuid(), PaneKind.Local, out _));
        Assert.True(registry.TryConsume(crossSessionToken, SessionId, PaneKind.Local, out _));
        Assert.False(registry.TryConsume(crossSessionToken, SessionId, PaneKind.Local, out _));

        var unknownToken = Guid.NewGuid().ToString("N");
        Assert.False(registry.CanAccept(unknownToken, SessionId, PaneKind.Local));
        Assert.False(registry.TryConsume("plain text paths", SessionId, PaneKind.Local, out _));
    }

    [Fact]
    public void ExpiredTokensCannotBeAcceptedOrConsumed()
    {
        var time = new ManualTimeProvider(new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var registry = new FilePaneDragDropRegistry(time, TimeSpan.FromSeconds(30));
        Assert.True(registry.TryIssue(
            SessionId,
            PaneKind.Local,
            [new(@"C:\staging\package.msix", TransferSourceKind.File)],
            out var token));

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.False(registry.CanAccept(token, SessionId, PaneKind.Remote));
        Assert.False(registry.TryConsume(token, SessionId, PaneKind.Remote, out _));
    }

    [Fact]
    public void RegistryEvictsOldestTokenAtItsConfiguredBound()
    {
        var time = new ManualTimeProvider(new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var registry = new FilePaneDragDropRegistry(time, capacity: 1);
        Assert.True(registry.TryIssue(
            SessionId,
            PaneKind.Local,
            [new(@"C:\staging\one.bin", TransferSourceKind.File)],
            out var first));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(registry.TryIssue(
            SessionId,
            PaneKind.Local,
            [new(@"C:\staging\two.bin", TransferSourceKind.File)],
            out var second));

        Assert.False(registry.CanAccept(first, SessionId, PaneKind.Remote));
        Assert.True(registry.CanAccept(second, SessionId, PaneKind.Remote));
    }

    [Fact]
    public void RegistryRejectsUnsupportedOrUnboundedSourceDeclarations()
    {
        var registry = CreateRegistry();

        Assert.False(registry.TryIssue(
            SessionId,
            PaneKind.Local,
            [new(@"C:\staging\link", (TransferSourceKind)99)],
            out _));
        Assert.False(registry.TryIssue(
            SessionId,
            PaneKind.Local,
            [new("/unsafe\npath", TransferSourceKind.File)],
            out _));
        Assert.False(registry.TryIssue(
            SessionId,
            PaneKind.Remote,
            [new("/safe/../escape", TransferSourceKind.Directory)],
            out _));
        Assert.False(registry.TryIssue(
            SessionId,
            PaneKind.Local,
            Enumerable.Range(0, FilePaneDragDropRegistry.MaximumSources + 1)
                .Select(index => new FilePaneTransferSource($@"C:\staging\{index}.bin", TransferSourceKind.File)),
            out _));
    }

    [Fact]
    public void ExplorerStorageItemsAreAcceptedOnlyOnRemotePane()
    {
        Assert.True(FilePaneDragDropRegistry.CanAcceptExplorerStorageItems(PaneKind.Remote));
        Assert.False(FilePaneDragDropRegistry.CanAcceptExplorerStorageItems(PaneKind.Local));
        Assert.True(FilePaneDragDropRegistry.AreValidExplorerSources(
            [new(@"C:\staging\package.msix", TransferSourceKind.File)]));
        Assert.False(FilePaneDragDropRegistry.AreValidExplorerSources(
            [new("relative-package.msix", TransferSourceKind.File)]));
        Assert.False(FilePaneDragDropRegistry.AreValidExplorerSources(
            [new(@"C:\", TransferSourceKind.Directory)]));
        Assert.False(FilePaneDragDropRegistry.AreValidExplorerSources(
            [new(@"\\server\share\", TransferSourceKind.Directory)]));
        Assert.True(FilePaneDragDropRegistry.AreValidExplorerSources(
            [new(@"\\server\share\folder", TransferSourceKind.Directory)]));
    }

    [Theory]
    [InlineData(FilePaneDropRejectionKind.Empty)]
    [InlineData(FilePaneDropRejectionKind.TooManyItems)]
    [InlineData(FilePaneDropRejectionKind.UnsupportedItem)]
    [InlineData(FilePaneDropRejectionKind.InvalidLocalPath)]
    [InlineData(FilePaneDropRejectionKind.DataUnavailable)]
    public void ExplorerDropRejectionsAreTypedBoundedAndPathFree(FilePaneDropRejectionKind kind)
    {
        var rejection = FilePaneDropRejection.Create(kind);

        Assert.Equal(kind, rejection.Kind);
        Assert.NotEmpty(rejection.Message);
        Assert.True(rejection.Message.Length <= 160, $"Message length was {rejection.Message.Length}.");
        Assert.DoesNotContain('\r', rejection.Message);
        Assert.DoesNotContain('\n', rejection.Message);
        Assert.DoesNotContain(@"C:\", rejection.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FilePaneDragDropRegistry CreateRegistry() => new(
        new ManualTimeProvider(new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));
}
