using System.Collections.ObjectModel;
using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class ConnectionProfilesViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private ConnectionProfile? _selectedProfile;
    private string _name = "New site";
    private string _host = string.Empty;
    private string _userName = string.Empty;
    private int _port = 22;
    private ConnectionProtocol _protocol = ConnectionProtocol.Sftp;
    private AuthenticationKind _authentication = AuthenticationKind.AskOnConnect;
    private string? _status;
    private string _credential = string.Empty;
    private bool _rememberCredential;
    private string _sshKeyPath = string.Empty;
    private SftpHostKeyReview? _pendingHostKeyReview;
    private TaskCompletionSource<bool>? _hostKeyReviewDecision;
    private CancellationTokenSource? _connectionOperationCancellation;

    public ConnectionProfilesViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        ConnectCommand = new AsyncRelayCommand(_ => ConnectAsync(), _ => SelectedProfile is not null && IsConnectionEditingEnabled, ReportError);
        CreateAndConnectCommand = new AsyncRelayCommand(_ => CreateAndConnectAsync(), _ => !string.IsNullOrWhiteSpace(Host) && IsConnectionEditingEnabled, ReportError);
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => SelectedProfile is not null && IsConnectionEditingEnabled, ReportError);
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), _ => SelectedProfile is not null && IsConnectionEditingEnabled, ReportError);
        ApproveHostKeyReviewCommand = new RelayCommand(_ => CompleteHostKeyReview(approved: true), _ => HasPendingHostKeyReview);
        CancelHostKeyReviewCommand = new RelayCommand(_ => CompleteHostKeyReview(approved: false), _ => HasPendingHostKeyReview);
    }

    public event EventHandler<WorkspaceSessionSeed>? SessionConnected;
    public event EventHandler? StateRefreshRequested;
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public IReadOnlyList<ConnectionProtocol> Protocols { get; } = Enum.GetValues<ConnectionProtocol>();
    public IReadOnlyList<AuthenticationKind> AuthenticationKinds { get; } = Enum.GetValues<AuthenticationKind>();
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand CreateAndConnectCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand ApproveHostKeyReviewCommand { get; }
    public RelayCommand CancelHostKeyReviewCommand { get; }

    public ConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            var previousProfileId = _selectedProfile?.Id;
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            if (previousProfileId != value?.Id)
            {
                Credential = string.Empty;
                RememberCredential = false;
            }

            ConnectCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            if (value is not null)
            {
                Name = value.Name;
                Host = value.Host;
                UserName = value.UserName;
                Port = value.Port;
                Protocol = value.Protocol;
                Authentication = value.Authentication;
                SshKeyPath = value.SshKeyPath ?? string.Empty;
            }
        }
    }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Host { get => _host; set { if (SetProperty(ref _host, value)) CreateAndConnectCommand.NotifyCanExecuteChanged(); } }
    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public ConnectionProtocol Protocol { get => _protocol; set => SetProperty(ref _protocol, value); }
    public AuthenticationKind Authentication
    {
        get => _authentication;
        set
        {
            if (!SetProperty(ref _authentication, value)) return;
            if (!IsCredentialAuthentication) Credential = string.Empty;
            if (!CanRememberCredential) RememberCredential = false;
            OnPropertyChanged(nameof(IsCredentialAuthentication));
            OnPropertyChanged(nameof(IsSshKeyAuthentication));
            OnPropertyChanged(nameof(CanRememberCredential));
        }
    }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Credential { get => _credential; set => SetProperty(ref _credential, value); }
    public bool RememberCredential { get => _rememberCredential; set => SetProperty(ref _rememberCredential, value); }
    public string SshKeyPath { get => _sshKeyPath; set => SetProperty(ref _sshKeyPath, value); }
    public bool IsCredentialAuthentication => Authentication is AuthenticationKind.Password or AuthenticationKind.AskOnConnect;
    public bool IsSshKeyAuthentication => Authentication == AuthenticationKind.SshKey;
    public bool CanRememberCredential => Authentication == AuthenticationKind.Password;
    public SftpHostKeyReview? PendingHostKeyReview => _pendingHostKeyReview;
    public bool HasPendingHostKeyReview => PendingHostKeyReview is not null;
    public bool IsHostKeyEnrollment => PendingHostKeyReview?.State == SftpHostKeyState.EnrollmentRequired;
    public bool IsHostKeyChanged => PendingHostKeyReview?.State == SftpHostKeyState.Changed;
    public bool IsConnectionEditingEnabled => !HasPendingHostKeyReview && _connectionOperationCancellation is null;
    public string HostKeyReviewHeading => IsHostKeyChanged ? "SFTP host key changed" : "Trust this SFTP host key?";
    public string HostKeyReviewMessage => IsHostKeyChanged
        ? "The server presented a different key than the one previously trusted. Verify both fingerprints through a separate trusted channel before replacing it."
        : "This server has not been trusted yet. Verify the fingerprint through a separate trusted channel before continuing.";
    public string HostKeyApprovalLabel => IsHostKeyChanged ? "Replace trusted key" : "Trust and continue";

    public void Load(IEnumerable<ConnectionProfile> profiles)
    {
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault();
    }

    public void Upsert(ConnectionProfile profile)
    {
        var existing = Profiles.FirstOrDefault(candidate => candidate.Id == profile.Id);
        if (existing is null) Profiles.Add(profile);
        else Profiles[Profiles.IndexOf(existing)] = profile;
    }

    private async Task ConnectAsync()
    {
        using var operation = BeginConnectionOperation();
        var agentMutationDispatched = false;
        try
        {
            await ConnectCoreAsync(operation.Token, () => agentMutationDispatched = true).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            ReportUncertainOutcome(
                "Connection cancellation requested. An Agent request may have completed, so connection and credential state are unknown. Refreshing workspace state.");
        }
        catch (Exception exception) when (agentMutationDispatched)
        {
            Credential = string.Empty;
            ReportUncertainOutcome(
                $"Connection could not be confirmed. Profile, credential, trust, and connection state may have changed. {exception.Message} Refreshing workspace state.");
        }
        finally
        {
            EndConnectionOperation(operation);
        }
    }

    private async Task ConnectCoreAsync(
        CancellationToken cancellationToken,
        Action? markAgentMutationDispatched = null)
    {
        var profile = SelectedProfile;
        if (profile is null) return;

        Status = profile.Protocol == ConnectionProtocol.Sftp
            ? $"Inspecting the SFTP host key for {profile.Name}…"
            : $"Connecting to {profile.Name}…";
        var ephemeral = IsCredentialAuthentication && !string.IsNullOrEmpty(Credential) ? Credential : null;
        if (!await EnsureSftpHostKeyTrustedAsync(profile, cancellationToken, markAgentMutationDispatched).ConfigureAwait(true))
        {
            Status = "Host-key review cancelled. The credential was not sent.";
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (RememberCredential && profile.Authentication == AuthenticationKind.Password && ephemeral is not null)
        {
            markAgentMutationDispatched?.Invoke();
            var saved = await _agent.SaveProfileAsync(profile, ephemeral, cancellationToken).ConfigureAwait(true);
            ReplaceProfile(profile, saved);
            SelectedProfile = saved;
            profile = saved;
            ephemeral = null;
        }
        Status = $"Connecting to {profile.Name}…";
        markAgentMutationDispatched?.Invoke();
        var session = await _agent.ConnectAsync(profile, ephemeral, cancellationToken).ConfigureAwait(true);
        Credential = string.Empty;
        Status = "Connected";
        SessionConnected?.Invoke(this, session);
    }

    private async Task CreateAndConnectAsync()
    {
        using var operation = BeginConnectionOperation();
        try
        {
            if (string.IsNullOrWhiteSpace(Host)) return;

            var profile = new ConnectionProfile(
                Guid.NewGuid(), Name.Trim(), Protocol, Host.Trim(), Port, UserName.Trim(), Authentication,
                SshKeyPath: IsSshKeyAuthentication ? SshKeyPath.Trim() : null);
            var ephemeralCredential = IsCredentialAuthentication && !string.IsNullOrEmpty(Credential) ? Credential : null;
            var rememberCredential = RememberCredential && profile.Authentication == AuthenticationKind.Password;
            // Save metadata first. An entered SFTP credential stays in App memory
            // until the presented host key has been explicitly trusted.
            var saved = await _agent.SaveProfileAsync(profile, cancellationToken: operation.Token).ConfigureAwait(true);
            Profiles.Add(saved);
            SelectedProfile = saved;
            // Selecting a genuinely new profile intentionally clears credential options.
            // This profile was created from the current form, so restore only the user's
            // explicit remember choice captured for that new password-authenticated profile.
            RememberCredential = rememberCredential && saved.Authentication == AuthenticationKind.Password;
            operation.Token.ThrowIfCancellationRequested();
            Credential = ephemeralCredential ?? string.Empty;
            await ConnectCoreAsync(operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Credential = string.Empty;
            ReportUncertainOutcome(
                "Create/connect cancellation requested. An Agent request may have completed, so profile, connection, and credential state are unknown. Refreshing workspace state.");
        }
        catch (Exception exception)
        {
            Credential = string.Empty;
            ReportUncertainOutcome(
                $"Create/connect could not be confirmed. Profile, credential, trust, and connection state may have changed. {exception.Message} Refreshing workspace state.");
        }
        finally
        {
            EndConnectionOperation(operation);
        }
    }

    private async Task SaveAsync()
    {
        using var operation = BeginConnectionOperation();
        try
        {
            if (SelectedProfile is null) return;
            var updated = SelectedProfile with
            {
                Name = Name.Trim(),
                Host = Host.Trim(),
                Port = Port,
                UserName = UserName.Trim(),
                Protocol = Protocol,
                Authentication = Authentication,
                SshKeyPath = IsSshKeyAuthentication ? SshKeyPath.Trim() : null,
            };
            var enteredCredential = RememberCredential && Authentication == AuthenticationKind.Password && !string.IsNullOrEmpty(Credential)
                ? Credential
                : null;
            var deferSftpCredential = updated.Protocol == ConnectionProtocol.Sftp && enteredCredential is not null;
            var saved = await _agent.SaveProfileAsync(
                updated,
                deferSftpCredential ? null : enteredCredential,
                operation.Token).ConfigureAwait(true);
            ReplaceProfile(SelectedProfile, saved);
            SelectedProfile = saved;
            operation.Token.ThrowIfCancellationRequested();
            if (deferSftpCredential)
            {
                Credential = enteredCredential!;
                if (!await EnsureSftpHostKeyTrustedAsync(saved, operation.Token).ConfigureAwait(true))
                {
                    Status = "Connection profile saved. Host-key review cancelled; the entered credential was not stored.";
                    return;
                }

                var credentialSaved = await _agent.SaveProfileAsync(saved, enteredCredential, operation.Token).ConfigureAwait(true);
                ReplaceProfile(saved, credentialSaved);
                SelectedProfile = credentialSaved;
            }

            Credential = string.Empty;
            Status = "Connection profile saved.";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Credential = string.Empty;
            ReportUncertainOutcome(
                "Save cancellation requested. An Agent request may have completed, so profile and credential state are unknown. Refreshing workspace state.");
        }
        catch (Exception exception)
        {
            Credential = string.Empty;
            ReportUncertainOutcome(
                $"Save could not be confirmed. Profile, credential, and trust state may have changed. {exception.Message} Refreshing workspace state.");
        }
        finally
        {
            EndConnectionOperation(operation);
        }
    }

    private async Task<bool> EnsureSftpHostKeyTrustedAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken,
        Action? markAgentMutationDispatched = null)
    {
        if (profile.Protocol != ConnectionProtocol.Sftp) return true;

        var expectedIdentity = ConnectionIdentity.FromProfile(profile);
        var inspection = await _agent.InspectSftpHostKeyAsync(profile, cancellationToken).ConfigureAwait(true);
        SftpHostKeyWireValidation.ValidateInspection(inspection, expectedIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        if (inspection.State == SftpHostKeyState.Trusted)
        {
            if (inspection.Review is not null)
                throw new InvalidDataException("A trusted SFTP host key unexpectedly required review.");
            return true;
        }

        var review = inspection.Review ?? throw new InvalidDataException("The SFTP host-key inspection did not include its required review.");
        if (review.ProfileId != profile.Id || review.State != inspection.State)
            throw new InvalidDataException("The SFTP host-key review did not match the selected connection.");
        if (!await RequestHostKeyReviewAsync(review).ConfigureAwait(true)) return false;

        cancellationToken.ThrowIfCancellationRequested();
        Status = review.State == SftpHostKeyState.Changed
            ? "Replacing the explicitly reviewed SFTP host key…"
            : "Enrolling the explicitly reviewed SFTP host key…";
        markAgentMutationDispatched?.Invoke();
        _ = await _agent.ApproveSftpHostKeyAsync(
            review,
            replaceExisting: review.State == SftpHostKeyState.Changed,
            cancellationToken).ConfigureAwait(true);
        return true;
    }

    private async Task<bool> RequestHostKeyReviewAsync(SftpHostKeyReview review)
    {
        if (_hostKeyReviewDecision is not null)
            throw new InvalidOperationException("Another SFTP host-key review is already active.");

        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostKeyReviewDecision = decision;
        SetPendingHostKeyReview(review);
        Status = review.State == SftpHostKeyState.Changed
            ? "Connection paused. The changed SFTP host key must be explicitly replaced or cancelled."
            : "Connection paused. Review the new SFTP host key or cancel.";
        try
        {
            return await decision.Task.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_hostKeyReviewDecision, decision)) _hostKeyReviewDecision = null;
            SetPendingHostKeyReview(null);
        }
    }

    public void CancelPendingHostKeyReview(bool clearCredential = false)
    {
        _connectionOperationCancellation?.Cancel();
        CompleteHostKeyReview(approved: false);
        if (clearCredential) Credential = string.Empty;
    }

    private CancellationTokenSource BeginConnectionOperation()
    {
        if (_connectionOperationCancellation is not null)
            throw new InvalidOperationException("Another connection operation is already active.");
        var operation = new CancellationTokenSource();
        _connectionOperationCancellation = operation;
        NotifyConnectionOperationChanged();
        return operation;
    }

    private void EndConnectionOperation(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(_connectionOperationCancellation, operation)) return;
        _connectionOperationCancellation = null;
        NotifyConnectionOperationChanged();
    }

    private void NotifyConnectionOperationChanged()
    {
        OnPropertyChanged(nameof(IsConnectionEditingEnabled));
        ConnectCommand.NotifyCanExecuteChanged();
        CreateAndConnectCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void CompleteHostKeyReview(bool approved) => _hostKeyReviewDecision?.TrySetResult(approved);

    private void SetPendingHostKeyReview(SftpHostKeyReview? review)
    {
        if (ReferenceEquals(_pendingHostKeyReview, review)) return;
        _pendingHostKeyReview = review;
        OnPropertyChanged(nameof(PendingHostKeyReview));
        OnPropertyChanged(nameof(HasPendingHostKeyReview));
        OnPropertyChanged(nameof(IsHostKeyEnrollment));
        OnPropertyChanged(nameof(IsHostKeyChanged));
        OnPropertyChanged(nameof(IsConnectionEditingEnabled));
        OnPropertyChanged(nameof(HostKeyReviewHeading));
        OnPropertyChanged(nameof(HostKeyReviewMessage));
        OnPropertyChanged(nameof(HostKeyApprovalLabel));
        ConnectCommand.NotifyCanExecuteChanged();
        CreateAndConnectCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ApproveHostKeyReviewCommand.NotifyCanExecuteChanged();
        CancelHostKeyReviewCommand.NotifyCanExecuteChanged();
    }

    private void ReplaceProfile(ConnectionProfile previous, ConnectionProfile replacement)
    {
        var index = Profiles.IndexOf(previous);
        if (index >= 0) Profiles[index] = replacement;
    }

    private async Task DeleteAsync()
    {
        using var operation = BeginConnectionOperation();
        try
        {
            if (SelectedProfile is null) return;
            var removed = SelectedProfile;
            if (!await _agent.DeleteProfileAsync(removed.Id, operation.Token).ConfigureAwait(true))
                throw new InvalidOperationException("The Agent declined the profile deletion request.");
            Profiles.Remove(removed);
            SelectedProfile = Profiles.FirstOrDefault();
            Credential = string.Empty;
            Status = "Connection profile deleted and its sessions disconnected.";
            RequestStateRefresh();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Credential = string.Empty;
            ReportUncertainOutcome(
                "Profile deletion cancellation requested. The Agent may have completed the deletion, so profile and session state are unknown. Refreshing workspace state.");
        }
        catch (Exception exception)
        {
            Credential = string.Empty;
            ReportUncertainOutcome(
                $"Profile deletion could not be confirmed. Profile, credential, trust, and session state may have changed. {exception.Message} Refreshing workspace state.");
        }
        finally
        {
            EndConnectionOperation(operation);
        }
    }

    private void ReportError(Exception exception) => Status = exception.Message;

    private void ReportUncertainOutcome(string status)
    {
        Status = status;
        RequestStateRefresh();
    }

    private void RequestStateRefresh() => StateRefreshRequested?.Invoke(this, EventArgs.Empty);
}
