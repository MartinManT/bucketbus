using Avalonia.Threading;
using BucketBus.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BucketBus.Desktop.ViewModels;

public enum BrowserState { Disconnected, Connecting, Loading, Ready, Empty, Denied, Cancelled, Error }

public sealed partial class RemoteBrowserViewModel : ObservableObject, IDisposable
{
    private readonly ProfileManager _profiles;
    private readonly ReadOnlySessionController _controller;
    private IReadOnlyStorageSession? _session;
    private Func<CancellationToken, Task<StorageResult<StoragePage<BrowserRow>>>>? _next;
    private readonly List<StoragePage<BrowserRow>> _pages = [];
    private int _index = -1, _firstPage = 1;
    private long _generation, _metadataGeneration, _profilesGeneration;
    private CancellationTokenSource? _operation, _metadataOperation;
    private bool _disposed, _canFetch;
    private string? _bucket;
    private string _prefix = "";
    private ConnectionProfile? _selectedProfile;
    private BrowserRow? _selectedRow;
    [ObservableProperty] private IReadOnlyList<ConnectionProfile> _profilesList = Array.Empty<ConnectionProfile>();
    [ObservableProperty] private IReadOnlyList<BrowserRow> _rows = Array.Empty<BrowserRow>();
    [ObservableProperty] private string _bucketInput = "";
    [ObservableProperty] private string _prefixInput = "";
    [ObservableProperty] private bool _allowUnlock;
    [ObservableProperty] private BrowserState _state = BrowserState.Disconnected;
    [ObservableProperty] private string _status = "Disconnected. Select a profile and choose Connect.";
    [ObservableProperty] private string _metadata = "Select an object and choose Properties. Metadata is never fetched automatically.";
    [ObservableProperty] private bool _isMetadataLoading;
    [ObservableProperty] private bool _metadataVisible;
    public bool IsBusy => State is BrowserState.Connecting or BrowserState.Loading;
    public bool IsConnected => _session is { IsInvalidated: false };
    public string Location => !IsConnected ? "Not connected" : _bucket is null ? "Buckets" : BrowserText.Display(_bucket + " / " + _prefix);
    public string PageSummary => _index < 0 ? "No page loaded" : $"Page {_firstPage + _index} · {Rows.Count} rows · {_pages.Count}/5 cached pages · Server order; not a whole-bucket total";
    public int RetainedPageCount => _pages.Count;
    public int RetainedRowCount => _pages.Sum(p => p.Entries.Count);
    public string SelectionIdentity => SelectedRow is null ? "No selection" : SelectedRow.Type + " · Exact key (Unicode escapes): " + SelectedRow.ExactIdentity;
    public ConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            Disconnect();
            BucketInput = value?.Settings.Bucket ?? "";
            PrefixInput = value?.Settings.Prefix ?? "";
            AllowUnlock = false;
            NotifyCommands();
        }
    }
    public BrowserRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            ClearMetadata();
            OnPropertyChanged(nameof(SelectionIdentity));
            NotifyCommands();
        }
    }
    public IAsyncRelayCommand ReloadProfilesCommand { get; }
    public IAsyncRelayCommand ConnectCommand { get; }
    public IRelayCommand DisconnectCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand GoCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand UpCommand { get; }
    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand PropertiesCommand { get; }

    public RemoteBrowserViewModel(ProfileManager profiles, ReadOnlySessionController controller)
    {
        _profiles = profiles; _controller = controller;
        ReloadProfilesCommand = new AsyncRelayCommand(ReloadProfilesAsync);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => SelectedProfile is not null, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        DisconnectCommand = new RelayCommand(Disconnect);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy || IsMetadataLoading);
        GoCommand = new AsyncRelayCommand(() => NavigateAsync(string.IsNullOrEmpty(BucketInput) ? null : BucketInput, PrefixInput), () => IsConnected, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        RefreshCommand = new AsyncRelayCommand(() => NavigateAsync(_bucket, _prefix, SelectedRow), () => IsConnected, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        UpCommand = new AsyncRelayCommand(UpAsync, () => IsConnected && _bucket is not null, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => IsConnected && SelectedRow is { Kind: not BrowserRowKind.Object }, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        NextCommand = new AsyncRelayCommand(NextAsync, () => IsConnected && !IsBusy && (_index + 1 < _pages.Count || _canFetch));
        PreviousCommand = new RelayCommand(Previous, () => IsConnected && !IsBusy && _index > 0);
        PropertiesCommand = new AsyncRelayCommand(PropertiesAsync, () => IsConnected && SelectedRow is { Kind: BrowserRowKind.Object }, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        _profiles.ProfileChanged += ProfileChanged;
    }
    partial void OnStateChanged(BrowserState value) { OnPropertyChanged(nameof(IsBusy)); NotifyCommands(); }
    partial void OnIsMetadataLoadingChanged(bool value) => NotifyCommands();
    private void NotifyCommands()
    {
        ConnectCommand?.NotifyCanExecuteChanged(); GoCommand?.NotifyCanExecuteChanged(); RefreshCommand?.NotifyCanExecuteChanged();
        UpCommand?.NotifyCanExecuteChanged(); OpenCommand?.NotifyCanExecuteChanged(); NextCommand?.NotifyCanExecuteChanged();
        PreviousCommand?.NotifyCanExecuteChanged(); PropertiesCommand?.NotifyCanExecuteChanged(); CancelCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(PageSummary)); OnPropertyChanged(nameof(RetainedPageCount)); OnPropertyChanged(nameof(RetainedRowCount));
    }
    public async Task ReloadProfilesAsync()
    {
        var generation = ++_profilesGeneration;
        try
        {
            var result = await Task.Run(() => _profiles.ReadAsync());
            if (_disposed || generation != _profilesGeneration) return;
            if (result.Status != ProfileStatus.Success) { Disconnect(); Status = "Profiles could not be loaded. Open Profiles to review storage."; return; }
            var selected = SelectedProfile;
            ProfilesList = result.Snapshot!.Profiles;
            SelectedProfile = ProfilesList.SingleOrDefault(p => p.Id == selected?.Id);
        }
        catch (Exception) { if (!_disposed && generation == _profilesGeneration) { Disconnect(); Status = "Profiles could not be loaded. Code BB-PROFILE."; } }
    }
    private void ProfileChanged(Guid id)
    {
        void Apply()
        {
            if (_disposed) return;
            if (SelectedProfile?.Id == id) { Disconnect(); Status = "Profile changed. Select the updated profile and reconnect."; }
            // Metadata only; reloading never acquires credentials or connects.
            _ = ReloadProfilesAsync();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }
    private (long Generation, CancellationTokenSource Cancellation) Begin()
    {
        ++_generation; _operation?.Cancel(); ClearMetadata();
        var cancellation = new CancellationTokenSource(); _operation = cancellation;
        return (_generation, cancellation);
    }
    private bool Current(long generation) => !_disposed && generation == _generation;
    private void End(long generation, CancellationTokenSource cancellation)
    {
        if (Current(generation)) _operation = null;
        cancellation.Dispose(); NotifyCommands();
    }
    private void ClearPages()
    {
        _pages.Clear(); _index = -1; _firstPage = 1; _canFetch = false; _next = null;
        Rows = Array.Empty<BrowserRow>(); SelectedRow = null; NotifyCommands();
    }
    private void ClearMetadata()
    {
        ++_metadataGeneration; _metadataOperation?.Cancel(); _metadataOperation = null;
        IsMetadataLoading = false; MetadataVisible = false;
        Metadata = "Select an object and choose Properties. Metadata is never fetched automatically.";
    }
    public void Disconnect()
    {
        ++_generation; _operation?.Cancel(); _operation = null; ClearMetadata();
        var old = _session; _session = null; old?.Invalidate();
        if (old is not null) _ = DisposeSessionAsync(old);
        ClearPages(); _bucket = null; _prefix = "";
        State = BrowserState.Disconnected; Status = "Disconnected. Select a profile and choose Connect.";
        NotifyCommands();
    }
    private static async Task DisposeSessionAsync(IReadOnlyStorageSession session)
    { try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* No raw provider diagnostics. */ } }
    public void Cancel()
    {
        if (IsBusy)
        {
            ++_generation; _operation?.Cancel(); _operation = null; _canFetch = false;
            State = BrowserState.Cancelled; Status = "Cancelled. Refresh to restart this listing, or reconnect.";
        }
        var metadataWasLoading = IsMetadataLoading;
        ClearMetadata();
        if (metadataWasLoading) { MetadataVisible = true; Metadata = "Metadata request cancelled."; }
        NotifyCommands();
    }
    private async Task ConnectAsync()
    {
        var profile = SelectedProfile;
        if (profile is null || _disposed) return;
        Disconnect();
        var (generation, cancellation) = Begin();
        var bucket = string.IsNullOrEmpty(BucketInput) ? null : BucketInput;
        var prefix = PrefixInput;
        var allowUnlock = AllowUnlock;
        State = BrowserState.Connecting; Status = "Acquiring credentials for the selected profile…";
        try
        {
            var result = await Task.Run(() => _controller.OpenAsync(profile.Id, profile.Revision, allowUnlock, cancellation.Token));
            if (!Current(generation)) { if (result.Session is not null) await DisposeSessionAsync(result.Session); return; }
            if (result.Session is null)
            {
                State = result.Error == StorageError.Cancelled ? BrowserState.Cancelled : BrowserState.Error;
                Status = result.CredentialStatus is { } credential ? BrowserText.Credential(credential) : BrowserText.Error(result.Error); return;
            }
            _session = result.Session;
            PrepareListing(bucket, prefix);
            await FetchAsync(generation, cancellation.Token);
        }
        catch (OperationCanceledException) { if (Current(generation)) Failure(StorageError.Cancelled); }
        catch (Exception) { if (Current(generation)) Failure(StorageError.ServiceFailure); }
        finally { End(generation, cancellation); }
    }
    private void PrepareListing(string? bucket, string prefix)
    {
        ClearPages(); _bucket = bucket; _prefix = bucket is null ? "" : prefix;
        BucketInput = bucket ?? ""; PrefixInput = _prefix;
        if (bucket is null)
        {
            var listing = _session!.ListBuckets();
            _next = async ct => Map(await listing.NextAsync(ct).ConfigureAwait(false), b => new BrowserRow(b.Name, BrowserRowKind.Bucket, Modified: b.Created));
        }
        else
        {
            var listing = _session!.ListObjects(bucket, prefix);
            _next = async ct => Map(await listing.NextAsync(ct).ConfigureAwait(false), e => e is ObjectEntry obj
                ? new BrowserRow(obj.Key, BrowserRowKind.Object, obj.Size, obj.Modified) : new BrowserRow(e.Key, BrowserRowKind.Prefix));
        }
        _canFetch = true; NotifyCommands();
    }
    private static StorageResult<StoragePage<BrowserRow>> Map<T>(StorageResult<StoragePage<T>> result, Func<T, BrowserRow> map)
    {
        if (!result.IsSuccess) return StorageResult<StoragePage<BrowserRow>>.Failure(result.Error);
        if (result.Value is null || result.Value.Entries.Count > 1000) return StorageResult<StoragePage<BrowserRow>>.Failure(StorageError.ProtocolError);
        return StorageResult<StoragePage<BrowserRow>>.Success(new(Array.AsReadOnly(result.Value.Entries.Select(map).ToArray()), result.Value.IsComplete));
    }
    private async Task NavigateAsync(string? bucket, string prefix, BrowserRow? restoreSelection = null)
    {
        if (!IsConnected || _disposed) return;
        var (generation, cancellation) = Begin();
        try { PrepareListing(bucket, prefix); await FetchAsync(generation, cancellation.Token, restoreSelection); }
        catch (OperationCanceledException) { if (Current(generation)) Failure(StorageError.Cancelled); }
        catch (Exception) { if (Current(generation)) Failure(StorageError.ServiceFailure); }
        finally { End(generation, cancellation); }
    }
    private async Task FetchAsync(long generation, CancellationToken token, BrowserRow? restoreSelection = null)
    {
        State = BrowserState.Loading; Status = "Loading one page…";
        var next = _next!;
        var result = await Task.Run(() => next(token), token);
        if (!Current(generation)) return;
        if (!result.IsSuccess) { Failure(result.Error); return; }
        _pages.Add(result.Value!);
        if (_pages.Count > 5) { _pages.RemoveAt(0); _firstPage++; }
        _index = _pages.Count - 1; _canFetch = !result.Value!.IsComplete;
        ShowPage(restoreSelection);
    }
    private void ShowPage(BrowserRow? restoreSelection = null)
    {
        Rows = _pages[_index].Entries;
        SelectedRow = restoreSelection is null ? null : Rows.FirstOrDefault(r => r.Key == restoreSelection.Key && r.Kind == restoreSelection.Kind);
        State = Rows.Count == 0 ? BrowserState.Empty : BrowserState.Ready;
        Status = Rows.Count == 0 ? (_pages[_index].IsComplete ? "Empty listing." : "This page is empty; more pages are available.")
            : _pages[_index].IsComplete ? "Page loaded. End of listing." : "Page loaded. More pages are available.";
        if (restoreSelection is not null && SelectedRow is null) Status += " The previous selection is not on this page.";
        NotifyCommands();
    }
    private void Failure(StorageError error)
    {
        _canFetch = false;
        if (error == StorageError.SessionExpired)
        {
            var old = _session; _session = null; old?.Invalidate();
            if (old is not null) _ = DisposeSessionAsync(old);
            ClearPages(); ClearMetadata();
        }
        State = error switch { StorageError.AccessDenied => BrowserState.Denied, StorageError.Cancelled => BrowserState.Cancelled, _ => BrowserState.Error };
        Status = BrowserText.Error(error); NotifyCommands();
    }
    private async Task NextAsync()
    {
        if (IsBusy || !IsConnected) return;
        if (_index + 1 < _pages.Count) { ClearMetadata(); _index++; ShowPage(); return; }
        if (!_canFetch) return;
        var (generation, cancellation) = Begin();
        try { await FetchAsync(generation, cancellation.Token); }
        catch (OperationCanceledException) { if (Current(generation)) Failure(StorageError.Cancelled); }
        catch (Exception) { if (Current(generation)) Failure(StorageError.ServiceFailure); }
        finally { End(generation, cancellation); }
    }
    private void Previous() { if (!IsBusy && _index > 0) { ClearMetadata(); _index--; ShowPage(); } }
    private Task OpenAsync() => SelectedRow switch
    {
        { Kind: BrowserRowKind.Bucket } row => NavigateAsync(row.Key, ""),
        { Kind: BrowserRowKind.Prefix } row => NavigateAsync(_bucket, row.Key),
        _ => Task.CompletedTask
    };
    private Task UpAsync()
    {
        if (_bucket is null) return Task.CompletedTask;
        if (_prefix.Length == 0) return NavigateAsync(null, "");
        // Remove only one delimiter component. Repeated/leading slashes are significant.
        var end = _prefix.EndsWith('/') ? _prefix.Length - 1 : _prefix.Length;
        var separator = _prefix.LastIndexOf('/', Math.Max(0, end - 1), end);
        return NavigateAsync(_bucket, separator < 0 ? "" : _prefix[..(separator + 1)]);
    }
    private async Task PropertiesAsync()
    {
        var row = SelectedRow; var session = _session; var bucket = _bucket;
        if (row?.Kind != BrowserRowKind.Object || session is null || bucket is null) return;
        ClearMetadata();
        var navigation = _generation; var generation = _metadataGeneration;
        using var cancellation = new CancellationTokenSource(); _metadataOperation = cancellation;
        IsMetadataLoading = true; MetadataVisible = true; Metadata = "Loading object metadata…";
        try
        {
            var result = await Task.Run(() => session.GetMetadataAsync(bucket, row.Key, cancellation.Token));
            if (!Current(navigation) || generation != _metadataGeneration || !ReferenceEquals(row, SelectedRow)) return;
            Metadata = result.IsSuccess && result.Value is { } value
                ? $"Key (Unicode escapes): {BrowserText.Display(value.Key, true)}\nSize: {value.Size?.ToString() ?? "Unknown"}\nModified: {value.Modified?.ToString("O") ?? "Unknown"}\nContent type: {BrowserText.Display(value.ContentType ?? "Unknown")}\nETag (opaque): {BrowserText.Display(value.ETag ?? "Unknown")}\nVersion: {BrowserText.Display(value.VersionId ?? "Unknown")}"
                    + string.Concat(value.UserMetadata.Select(p => "\n" + BrowserText.Display(p.Key) + ": " + BrowserText.Display(p.Value)))
                : BrowserText.Error(result.Error);
        }
        catch (OperationCanceledException) { if (Current(navigation) && generation == _metadataGeneration) Metadata = "Metadata request cancelled."; }
        catch (Exception) { if (Current(navigation) && generation == _metadataGeneration) Metadata = "Metadata request failed. Code BB-METADATA."; }
        finally
        {
            if (generation == _metadataGeneration) { _metadataOperation = null; IsMetadataLoading = false; }
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        Disconnect(); _disposed = true; ++_profilesGeneration; _profiles.ProfileChanged -= ProfileChanged;
    }
}
