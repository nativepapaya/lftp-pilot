using Windows.UI.StartScreen;
using LFTPPilot.Windows.Activation;

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
            ValidateEntry(entry);
            JumpListItem item = JumpListItem.CreateWithArguments(entry.Arguments, entry.DisplayName);
            item.GroupName = entry.GroupName;
            jumpList.Items.Add(item);
        }
        await jumpList.SaveAsync().AsTask(cancellationToken);
    }

    internal static void ValidateEntry(JumpListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.DisplayName) || entry.DisplayName.Length > 80 ||
            string.IsNullOrWhiteSpace(entry.GroupName) || entry.GroupName.Length > 80 ||
            string.IsNullOrWhiteSpace(entry.Arguments) || entry.Arguments.Length > 512 ||
            !Uri.TryCreate(entry.Arguments, UriKind.Absolute, out var uri) ||
            !ProtocolActivationParser.TryParse(uri, out _))
        {
            throw new ArgumentException("Jump List entries must use bounded labels and an allowlisted LFTP Pilot activation.", nameof(entry));
        }
    }
}
