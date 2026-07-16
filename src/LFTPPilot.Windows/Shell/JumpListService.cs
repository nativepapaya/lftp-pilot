using Windows.UI.StartScreen;

namespace LFTPPilot.Windows.Shell;

public sealed record JumpListEntry(string DisplayName, string Arguments, string GroupName = "LFTP Pilot");

public sealed class JumpListService
{
    public async Task ReplaceAsync(IEnumerable<JumpListEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!JumpList.IsSupported()) return;
        JumpList jumpList = await JumpList.LoadCurrentAsync().AsTask(cancellationToken);
        jumpList.Items.Clear();
        foreach (JumpListEntry entry in entries.Take(12))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.DisplayName) || entry.DisplayName.Length > 80 ||
                string.IsNullOrWhiteSpace(entry.GroupName) || entry.GroupName.Length > 80 ||
                string.IsNullOrWhiteSpace(entry.Arguments) || entry.Arguments.Length > 512)
                throw new ArgumentException("Jump List entries must have bounded display names and arguments.", nameof(entries));
            JumpListItem item = JumpListItem.CreateWithArguments(entry.Arguments, entry.DisplayName);
            item.GroupName = entry.GroupName;
            jumpList.Items.Add(item);
        }
        await jumpList.SaveAsync().AsTask(cancellationToken);
    }
}
