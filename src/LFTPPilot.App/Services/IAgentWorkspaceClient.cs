using LFTPPilot.App.Models;
using LFTPPilot.Core;

namespace LFTPPilot.App.Services;

/// <summary>
/// UI-facing boundary for the background Agent. The named-pipe adapter can replace the
/// demo implementation without exposing transport details to view models.
/// </summary>
public interface IAgentWorkspaceClient : IAsyncDisposable
{
    event EventHandler<EngineEvent>? EventReceived;
    event EventHandler? StateInvalidated;
    bool IsConnected { get; }
    Task<UiWorkspaceBootstrap> LoadAsync(CancellationToken cancellationToken = default);
    Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? credential = null, CancellationToken cancellationToken = default);
    Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<SftpHostKeyInspection> InspectSftpHostKeyAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    Task<SftpHostKeyApproveResult> ApproveSftpHostKeyAsync(SftpHostKeyReview review, bool replaceExisting, CancellationToken cancellationToken = default);
    Task<WorkspaceSessionSeed> ConnectAsync(
        ConnectionProfile profile,
        string? ephemeralCredential = null,
        CancellationToken cancellationToken = default,
        Guid? existingSessionId = null);
    Task<bool> DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileEntry>> BrowseAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default);
    Task<RemoteSearchPage> StartRemoteSearchAsync(
        RemoteSearchSpec search,
        CancellationToken cancellationToken = default) =>
        Task.FromException<RemoteSearchPage>(new NotSupportedException("Recursive remote search is not available through this client."));
    Task<RemoteSearchPage> GetRemoteSearchAsync(
        RemoteSearchSpec search,
        string? continuationToken = null,
        int pageSize = RemoteSearchPolicy.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        Task.FromException<RemoteSearchPage>(new NotSupportedException("Recursive remote search is not available through this client."));
    Task<bool> CancelRemoteSearchAsync(
        Guid searchId,
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new NotSupportedException("Recursive remote search is not available through this client."));
    Task<FileMutationResult> CreateDirectoryAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default);
    Task<FileMutationResult> MoveEntryAsync(Guid sessionId, PaneKind pane, string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task<FileMutationResult> DeleteEntriesAsync(Guid sessionId, PaneKind pane, IReadOnlyList<string> paths, bool recursive, bool confirmed, CancellationToken cancellationToken = default);
    Task<JobSnapshot> EnqueueTransferAsync(Guid sessionId, TransferPlan plan, CancellationToken cancellationToken = default);
    Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<JobSnapshot> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<MirrorDefinition> SaveMirrorDefinitionAsync(
        MirrorDefinition definition,
        CancellationToken cancellationToken = default) =>
        Task.FromException<MirrorDefinition>(new NotSupportedException("Saved mirror definitions are not available through this client."));
    Task<bool> DeleteMirrorDefinitionAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new NotSupportedException("Saved mirror definitions are not available through this client."));
    Task<MirrorUiPreview> PreviewMirrorAsync(MirrorDefinition definition, CancellationToken cancellationToken = default);
    Task<JobSnapshot> ApproveMirrorAsync(MirrorUiPreview preview, bool deletionsApproved, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ExecuteConsoleAsync(Guid sessionId, string command, CancellationToken cancellationToken = default);
    Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default);
    Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default);
    Task<RemoteEditSession> StartRemoteEditAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);
    Task<RemoteEditReview> ReviewRemoteEditAsync(string editId, CancellationToken cancellationToken = default);
    Task<RemoteEditActionResult> ResolveRemoteEditAsync(string editId, string reviewToken, RemoteEditResolution resolution, CancellationToken cancellationToken = default);
    Task<bool> CompleteRemoteEditAsync(string editId, CancellationToken cancellationToken = default);
    Task StopAgentAsync(CancellationToken cancellationToken = default);
    Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task OpenUpdateInstallerAsync(CancellationToken cancellationToken = default);
}

internal static class SftpHostKeyWireValidation
{
    private const string FingerprintPrefix = "SHA256:";

    public static void ValidateInspection(SftpHostKeyInspection inspection, ConnectionIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (expectedIdentity.Protocol != ConnectionProtocol.Sftp)
            throw new ArgumentException("An SFTP connection identity is required.", nameof(expectedIdentity));
        if (!Enum.IsDefined(inspection.State))
            throw new InvalidDataException("The Agent returned an unsupported SFTP host-key state.");
        if (inspection.State == SftpHostKeyState.Trusted)
        {
            if (inspection.Review is not null)
                throw new InvalidDataException("A trusted SFTP host key cannot require review.");
            return;
        }

        var review = inspection.Review ??
            throw new InvalidDataException("The Agent omitted the required SFTP host-key review.");
        if (review.State != inspection.State)
            throw new InvalidDataException("The Agent returned inconsistent SFTP host-key review state.");
        ValidateReview(review, expectedIdentity);
    }

    public static void ValidateReview(SftpHostKeyReview review, ConnectionIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateReview(review, expectedIdentity.ProfileId);
        if (expectedIdentity.Protocol != ConnectionProtocol.Sftp ||
            !string.Equals(review.Endpoint, expectedIdentity.SftpHostKeyEndpoint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Agent returned an SFTP host-key review for a different endpoint.");
        }
    }

    public static void ValidateReview(SftpHostKeyReview review, Guid expectedProfileId)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (expectedProfileId == Guid.Empty || review.ProfileId != expectedProfileId || review.ReviewId == Guid.Empty)
            throw new InvalidDataException("The Agent returned an SFTP host-key review for a different profile.");
        if (review.State is not SftpHostKeyState.EnrollmentRequired and not SftpHostKeyState.Changed)
            throw new InvalidDataException("The Agent returned an SFTP host-key review in an unsupported state.");

        ValidateEndpoint(review.Endpoint);
        ValidateAlgorithm(review.PresentedAlgorithm, "presented algorithm");
        ValidateFingerprint(review.PresentedFingerprintSha256, "presented fingerprint");
        ValidateApprovalToken(review.ApprovalToken);
        if (review.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The Agent returned an expired SFTP host-key review.");

        if (review.State == SftpHostKeyState.EnrollmentRequired)
        {
            if (review.TrustedAlgorithm is not null || review.TrustedFingerprintSha256 is not null)
                throw new InvalidDataException("A new SFTP host-key enrollment cannot include an existing trusted key.");
            return;
        }

        ValidateAlgorithm(review.TrustedAlgorithm, "trusted algorithm");
        ValidateFingerprint(review.TrustedFingerprintSha256, "trusted fingerprint");
        if (string.Equals(review.PresentedAlgorithm, review.TrustedAlgorithm, StringComparison.Ordinal) &&
            string.Equals(review.PresentedFingerprintSha256, review.TrustedFingerprintSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A changed SFTP host-key review did not identify a different key.");
        }
    }

    private static void ValidateEndpoint(string? endpoint)
    {
        const string prefix = "sftp://";
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length > 512 ||
            !endpoint.StartsWith(prefix, StringComparison.Ordinal) ||
            endpoint.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "sftp", StringComparison.Ordinal) ||
            uri.HostNameType == UriHostNameType.Unknown ||
            uri.Port is < 1 or > 65_535 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Agent returned an invalid SFTP host-key endpoint.");
        }

        var authority = endpoint[prefix.Length..];
        if (authority.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0)
            throw new InvalidDataException("The Agent returned an invalid SFTP host-key endpoint.");

        string host;
        string portText;
        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = authority.IndexOf(']');
            if (closingBracket <= 1 || closingBracket + 2 > authority.Length ||
                authority[closingBracket + 1] != ':' ||
                authority.IndexOf('[', 1) >= 0 || authority.IndexOf(']', closingBracket + 1) >= 0)
            {
                throw new InvalidDataException("The Agent returned an invalid SFTP host-key endpoint.");
            }

            host = authority[1..closingBracket];
            portText = authority[(closingBracket + 2)..];
            if (Uri.CheckHostName(host) != UriHostNameType.IPv6)
                throw new InvalidDataException("The Agent returned an invalid SFTP host-key endpoint.");
        }
        else
        {
            var separator = authority.LastIndexOf(':');
            if (separator <= 0 || authority[..separator].Contains(':') ||
                authority.Contains('[') || authority.Contains(']'))
            {
                throw new InvalidDataException("The Agent returned an invalid SFTP host-key endpoint.");
            }

            host = authority[..separator];
            portText = authority[(separator + 1)..];
            if (Uri.CheckHostName(host) is not UriHostNameType.Dns and not UriHostNameType.IPv4)
                throw new InvalidDataException("The Agent returned an invalid SFTP host-key endpoint.");
        }

        if (host.Length > 253 || !string.Equals(host, host.ToLowerInvariant(), StringComparison.Ordinal) ||
            !int.TryParse(portText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65_535 ||
            !string.Equals(portText, port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Agent returned a non-canonical SFTP host-key endpoint.");
        }
    }

    private static void ValidateAlgorithm(string? algorithm, string field)
    {
        // RFC 4251 name-lists use non-empty printable US-ASCII names with comma reserved as the separator.
        if (algorithm is not { Length: >= 1 and <= 128 } ||
            algorithm.Any(static character => character is < '\x21' or > '\x7e' or ','))
        {
            throw new InvalidDataException($"The Agent returned an invalid SFTP host-key {field}.");
        }
    }

    private static void ValidateFingerprint(string? fingerprint, string field)
    {
        if (fingerprint is not { Length: 50 } ||
            !fingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The Agent returned an invalid SFTP host-key {field}.");
        }

        var payload = fingerprint[FingerprintPrefix.Length..];
        if (payload.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '+' and not '/'))
        {
            throw new InvalidDataException($"The Agent returned an invalid SFTP host-key {field}.");
        }

        try
        {
            var digest = Convert.FromBase64String(payload + "=");
            if (digest.Length != 32 ||
                !string.Equals(Convert.ToBase64String(digest).TrimEnd('='), payload, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The Agent returned an invalid SFTP host-key {field}.");
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"The Agent returned an invalid SFTP host-key {field}.", exception);
        }
    }

    private static void ValidateApprovalToken(string? approvalToken)
    {
        if (approvalToken is not { Length: 64 } || approvalToken.Any(static character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("The Agent returned an invalid SFTP host-key approval token.");
    }
}
