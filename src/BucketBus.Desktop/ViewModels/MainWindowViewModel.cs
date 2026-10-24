using BucketBus.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BucketBus.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private InitializationStatus _status = InitializationStatus.Starting;
    private bool _profilesVisible;
    public ProfileManagementViewModel? Profiles { get; }
    public LocalBrowserViewModel? Local { get; }
    public TransferViewModel? Transfers { get; }
    public RemoteBrowserViewModel? Browser { get; }
    public bool ProfilesVisible { get => _profilesVisible; private set => SetProperty(ref _profilesVisible, value); }
    public IAsyncRelayCommand ShowProfilesCommand { get; }
    public IRelayCommand CloseProfilesCommand { get; }

    public MainWindowViewModel(ILogger<MainWindowViewModel> logger, ProfileManagementViewModel? profiles = null, RemoteBrowserViewModel? browser = null, LocalBrowserViewModel? local = null, TransferViewModel? transfers = null)
    {
        _logger = logger;
        Profiles = profiles;
        Browser = browser;
        Local = local;
        Transfers = transfers;
        if (Browser is not null) Browser.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Browser.IsConnected)) OnPropertyChanged(nameof(StatusMessage)); };
        ShowProfilesCommand = new AsyncRelayCommand(async () =>
        {
            if (Profiles is null) return;
            Browser?.Cancel();
            ProfilesVisible = true;
            await Profiles.RefreshAsync();
        });
        CloseProfilesCommand = new RelayCommand(() =>
        {
            if (Profiles?.IsBusy == true) return;
            Profiles?.ClearSecrets();
            ProfilesVisible = false;
            if (Browser is not null) _ = Browser.ReloadProfilesAsync();
        });
        _logger.LogInformation(new EventId(1000, "ShellStarting"), "Shell initialization started.");
    }

    public bool IsLoading => _status.Phase == InitializationPhase.Starting;
    public bool HasError => _status.Phase == InitializationPhase.Failed;
    public string StatusMessage => _status.Phase switch
    {
        InitializationPhase.Starting => "Starting BucketBus…",
        InitializationPhase.Ready => Browser?.IsConnected == true ? "Ready · Connected" : "Ready · Not connected",
        _ => "Startup failed · BB-STARTUP"
    };

    public void CompleteInitialization()
    {
        if (_status.Phase == InitializationPhase.Ready)
            return;

        _status = _status.Complete();
        NotifyStatusChanged();
        _logger.LogInformation(new EventId(1001, "ShellReady"), "Shell initialization completed.");
    }

    public void ReportInitializationFailure(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        _status = _status.Fail();
        NotifyStatusChanged();
        // Do not retain or forward the exception: messages, Data and inner errors can contain secrets.
        _logger.LogError(new EventId(1002, "ShellFailed"), "Shell initialization failed. Code BB-STARTUP.");
    }

    private void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(StatusMessage));
    }
}
