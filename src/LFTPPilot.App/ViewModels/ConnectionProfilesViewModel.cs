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

    public ConnectionProfilesViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        ConnectCommand = new AsyncRelayCommand(_ => ConnectAsync(), _ => SelectedProfile is not null, ReportError);
        CreateAndConnectCommand = new AsyncRelayCommand(_ => CreateAndConnectAsync(), _ => !string.IsNullOrWhiteSpace(Host), ReportError);
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => SelectedProfile is not null, ReportError);
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), _ => SelectedProfile is not null, ReportError);
    }

    public event EventHandler<WorkspaceSessionSeed>? SessionConnected;
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public IReadOnlyList<ConnectionProtocol> Protocols { get; } = Enum.GetValues<ConnectionProtocol>();
    public IReadOnlyList<AuthenticationKind> AuthenticationKinds { get; } = Enum.GetValues<AuthenticationKind>();
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand CreateAndConnectCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }

    public ConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            ConnectCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            if (value is not null)
            {
                Credential = string.Empty;
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
        if (SelectedProfile is null)
        {
            return;
        }

        Status = $"Connecting to {SelectedProfile.Name}…";
        var ephemeral = IsCredentialAuthentication && !string.IsNullOrEmpty(Credential) ? Credential : null;
        if (RememberCredential && SelectedProfile.Authentication == AuthenticationKind.Password && ephemeral is not null)
        {
            SelectedProfile = await _agent.SaveProfileAsync(SelectedProfile, ephemeral).ConfigureAwait(true);
            ephemeral = null;
        }
        var session = await _agent.ConnectAsync(SelectedProfile, ephemeral).ConfigureAwait(true);
        Credential = string.Empty;
        Status = "Connected";
        SessionConnected?.Invoke(this, session);
    }

    private async Task CreateAndConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            return;
        }

        var profile = new ConnectionProfile(
            Guid.NewGuid(), Name.Trim(), Protocol, Host.Trim(), Port, UserName.Trim(), Authentication,
            SshKeyPath: IsSshKeyAuthentication ? SshKeyPath.Trim() : null);
        var ephemeralCredential = IsCredentialAuthentication && !string.IsNullOrEmpty(Credential) ? Credential : null;
        var storedCredential = RememberCredential && Authentication == AuthenticationKind.Password ? ephemeralCredential : null;
        var saved = await _agent.SaveProfileAsync(profile, storedCredential).ConfigureAwait(true);
        Profiles.Add(saved);
        SelectedProfile = saved;
        Credential = storedCredential is null ? ephemeralCredential ?? string.Empty : string.Empty;
        await ConnectAsync().ConfigureAwait(true);
    }

    private async Task SaveAsync()
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
        var storedCredential = RememberCredential && Authentication == AuthenticationKind.Password && !string.IsNullOrEmpty(Credential) ? Credential : null;
        var saved = await _agent.SaveProfileAsync(updated, storedCredential).ConfigureAwait(true);
        var index = Profiles.IndexOf(SelectedProfile);
        if (index >= 0) Profiles[index] = saved;
        SelectedProfile = saved;
        Credential = string.Empty;
        Status = "Connection profile saved.";
    }

    private async Task DeleteAsync()
    {
        if (SelectedProfile is null) return;
        var removed = SelectedProfile;
        try
        {
            if (!await _agent.DeleteProfileAsync(removed.Id).ConfigureAwait(true))
                throw new InvalidOperationException("The Agent declined the profile deletion request.");
        }
        catch (Exception exception)
        {
            Status = $"Delete blocked. The profile and its sessions remain available. {exception.Message}";
            return;
        }
        Profiles.Remove(removed);
        SelectedProfile = Profiles.FirstOrDefault();
        Credential = string.Empty;
        Status = "Connection profile deleted and its sessions disconnected.";
    }

    private void ReportError(Exception exception) => Status = exception.Message;
}
