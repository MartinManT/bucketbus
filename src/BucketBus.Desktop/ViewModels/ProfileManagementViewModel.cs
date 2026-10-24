using System.Collections.ObjectModel;
using BucketBus.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BucketBus.Desktop.ViewModels;

public enum CredentialChoice { KeepExisting, ClearBinding, SessionOnly, SecureStore }

public sealed partial class ProfileManagementViewModel : ObservableObject
{
    private readonly ProfileManager _manager;
    private CancellationTokenSource? _operation;
    private ConnectionProfile? _selected;
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public IReadOnlyList<AddressingStyle> AddressingChoices { get; } = Enum.GetValues<AddressingStyle>();
    public IReadOnlyList<CredentialChoice> CredentialChoices { get; } = Enum.GetValues<CredentialChoice>();
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _region = "us-east-1";
    [ObservableProperty] private AddressingStyle _addressing = AddressingStyle.Path;
    [ObservableProperty] private string _bucket = "";
    [ObservableProperty] private string _prefix = "";
    [ObservableProperty] private CredentialChoice _credentialChoice = CredentialChoice.KeepExisting;
    [ObservableProperty] private string _accessKey = "";
    [ObservableProperty] private string _secretKey = "";
    [ObservableProperty] private string _sessionToken = "";
    [ObservableProperty] private bool _allowUnlock;
    [ObservableProperty] private bool _confirmDelete;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Profiles are stored on this computer. No S3 connection is made.";
    public ConnectionProfile? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            LoadEditor(value);
        }
    }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand CheckCredentialCommand { get; }
    public IAsyncRelayCommand RetryCleanupCommand { get; }
    public IRelayCommand NewCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public ProfileManagementViewModel(ProfileManager manager)
    {
        _manager = manager;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync));
        SaveCommand = new AsyncRelayCommand(() => RunAsync(SaveAsync));
        DeleteCommand = new AsyncRelayCommand(() => RunAsync(DeleteAsync));
        CheckCredentialCommand = new AsyncRelayCommand(() => RunAsync(CheckAsync));
        RetryCleanupCommand = new AsyncRelayCommand(() => RunAsync(async token =>
            Status = await _manager.RetryCleanupAsync(AllowUnlock, token) ? "Credential cleanup complete." : "Cleanup is still required. Check your secure store and retry before closing."));
        NewCommand = new RelayCommand(() => { if (!IsBusy) { Selected = null; LoadEditor(null); } });
        CancelCommand = new RelayCommand(() => _operation?.Cancel());
    }
    public Task RefreshAsync() => RunAsync(LoadAsync);
    public void ClearSecrets() { AccessKey = ""; SecretKey = ""; SessionToken = ""; }
    private void LoadEditor(ConnectionProfile? profile)
    {
        ClearSecrets();
        ProfileName = profile?.Settings.Name ?? "";
        Endpoint = profile?.Settings.Endpoint ?? "";
        Region = profile?.Settings.Region ?? "us-east-1";
        Addressing = profile?.Settings.Addressing ?? AddressingStyle.Path;
        Bucket = profile?.Settings.Bucket ?? "";
        Prefix = profile?.Settings.Prefix ?? "";
        CredentialChoice = CredentialChoice.KeepExisting;
        ConfirmDelete = false;
        AllowUnlock = false;
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsBusy = true;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "Operation cancelled. Refresh profiles before retrying."; }
        catch (Exception) { Status = "Operation failed. Refresh profiles before retrying. Code BB-PROFILE."; }
        finally { ClearSecrets(); IsBusy = false; _operation = null; }
    }
    private async Task LoadAsync(CancellationToken token)
    {
        var result = await _manager.ReadAsync(token);
        if (result.Status != ProfileStatus.Success) { Status = ProfileMessage(result.Status); return; }
        Selected = null;
        Profiles.Clear();
        foreach (var profile in result.Snapshot!.Profiles.OrderBy(p => p.Settings.Name)) Profiles.Add(profile);
        Status = "Profiles loaded. Select a profile or choose New.";
    }
    private async Task SaveAsync(CancellationToken token)
    {
        var settings = new ProfileSettings(ProfileName, Endpoint, Region, Addressing, Bucket, Prefix);
        try { settings = settings.Validate(); }
        catch (ArgumentException) { Status = "Check the name, region and HTTPS host endpoint. Paths, credentials, queries and fragments are not allowed in endpoints."; return; }
        var replace = CredentialChoice != CredentialChoice.KeepExisting;
        var storage = CredentialChoice == CredentialChoice.SecureStore ? CredentialStorage.Secure : CredentialStorage.Session;
        Credentials? credentials = null;
        if (CredentialChoice is CredentialChoice.SessionOnly or CredentialChoice.SecureStore)
        {
            if (storage == CredentialStorage.Secure && !string.IsNullOrEmpty(SessionToken))
            { Status = "Temporary session-token credentials must use SessionOnly. Nothing was saved."; return; }
            try { credentials = new Credentials(AccessKey, SecretKey, SessionToken); }
            catch (ArgumentException) { Status = "Enter a valid access key and secret key. Nothing was saved."; return; }
        }
        var targetChanged = Selected is not null && Selected.Settings.TargetFingerprint() != settings.TargetFingerprint();
        var id = Selected?.Id ?? Guid.NewGuid();
        var result = await _manager.SaveAsync(id, Selected?.Revision ?? 0,
            settings, replace, storage, credentials, AllowUnlock, token);
        if (result.Status == ProfileStatus.Success)
        {
            await LoadAsync(CancellationToken.None);
            Selected = Profiles.SingleOrDefault(p => p.Id == id);
        }
        Status = result.CredentialStatus is { } credentialStatus ? CredentialMessage(credentialStatus) : ProfileMessage(result.Status);
        if (result.Status == ProfileStatus.Success && targetChanged && !replace)
            Status += " Target changed: credentials detached. Select this profile and explicitly enter credentials to rebind.";
        if (result.CleanupRequired) Status += " Old or unbound vault entries need cleanup. Retry cleanup before closing; a crash or exit may leave app-owned entries for manual removal.";
    }
    private async Task DeleteAsync(CancellationToken token)
    {
        if (Selected is null || !ConfirmDelete) { Status = "Select a profile and confirm its deletion first."; return; }
        var result = await _manager.DeleteAsync(Selected.Id, Selected.Revision, AllowUnlock, token);
        if (result.Status == ProfileStatus.Success) await LoadAsync(CancellationToken.None);
        Status = result.Status == ProfileStatus.Success ? "Profile deleted." : ProfileMessage(result.Status);
        if (result.CleanupRequired) Status += " Vault cleanup required. Retry before closing; retained entries may need manual removal.";
    }
    private async Task CheckAsync(CancellationToken token)
    {
        if (Selected is null) { Status = "Select a saved profile first."; return; }
        var result = await _manager.ReadCredentialAsync(Selected.Id, Selected.Revision, AllowUnlock, token);
        Status = CredentialMessage(result.Status); // Never bind the returned secret to the editor.
    }
    private static string ProfileMessage(ProfileStatus status) => status switch
    {
        ProfileStatus.Success => "Profile saved.",
        ProfileStatus.Conflict => "Profile changed elsewhere. Refresh and review before saving again.",
        ProfileStatus.UnsupportedSchema => "Profiles use a newer schema. No file was replaced. Use a compatible application version.",
        ProfileStatus.Invalid => "Invalid profile data. Existing files were preserved; review the fields or recover the profile file.",
        ProfileStatus.Cancelled => "Operation cancelled. Refresh before retrying.",
        _ => "Profile storage is unavailable or failed. Existing data was preserved. Check permissions and disk space, then refresh."
    };
    private static string CredentialMessage(CredentialStatus status) => status switch
    {
        CredentialStatus.Found => "Credential found. No S3 connection or permission check was performed.",
        CredentialStatus.Missing => "Credential missing. Session-only credentials are lost when the application exits; explicitly enter credentials again.",
        CredentialStatus.Locked => "Secure store is locked. Allow an unlock prompt and retry, or explicitly choose SessionOnly.",
        CredentialStatus.Unavailable => "Secure store unavailable. Configure your OS keyring or explicitly choose SessionOnly.",
        CredentialStatus.Denied => "Secure store access denied. No plaintext fallback was used.",
        CredentialStatus.Cancelled => "Credential operation cancelled. Refresh before retrying.",
        _ => "Credential storage or retrieval failed. No plaintext fallback was used. Check the secure store and retry."
    };
}
