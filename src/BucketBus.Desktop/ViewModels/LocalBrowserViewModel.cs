using System.Collections.ObjectModel;
using System.Globalization;
using BucketBus.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BucketBus.Desktop.ViewModels;

public enum LocalSortField { Name, Size, Modified, Type }
public enum LocalBrowserState { Idle, Loading, Ready, Empty, Error, Cancelled }
public sealed record LocalRow(LocalEntry Entry)
{
    public string Name => BrowserText.Display(Entry.Name);
    public string Identity => BrowserText.Display(Entry.FullPath, true);
    public string Type => Entry.Kind switch
    {
        LocalEntryKind.Directory => "Folder", LocalEntryKind.File => "File",
        LocalEntryKind.SymbolicLink => Entry.CanEnter ? "Folder link" : "Link",
        LocalEntryKind.Other => "Special", _ => "Unavailable"
    };
    public string SizeText => Entry.Size is { } size ? size.ToString("N0", CultureInfo.CurrentCulture) + " B" : "—";
    public string ModifiedText => Entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "Unknown";
}

public sealed class LocalBrowserViewModel : ObservableObject, IDisposable
{
    private readonly ILocalDirectoryBrowser _files;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private long _generation;
    private bool _disposed, _partial, _descending;
    private string _pathInput, _directory = "", _status = "Choose a directory.";
    private LocalBrowserState _state;
    private IReadOnlyList<LocalRow> _rows = [];
    private IReadOnlyList<string> _roots = [];
    private LocalSortField _sort = LocalSortField.Name;
    public string PathInput { get => _pathInput; set => SetProperty(ref _pathInput, value); }
    public string DirectoryPath { get => _directory; private set { if (SetProperty(ref _directory, value)) UpCommand.NotifyCanExecuteChanged(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public LocalBrowserState State { get => _state; private set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(IsBusy)); CancelCommand.NotifyCanExecuteChanged(); } } }
    public bool IsBusy => State == LocalBrowserState.Loading;
    public IReadOnlyList<LocalRow> Rows { get => _rows; private set => SetProperty(ref _rows, value); }
    public IReadOnlyList<string> Roots { get => _roots; private set => SetProperty(ref _roots, value); }
    public ObservableCollection<LocalRow> SelectedRows { get; } = [];
    public string SelectionSummary => $"{SelectedRows.Count:N0} selected";
    public string ListingSummary => IsBusy ? "Loading directory…" : State == LocalBrowserState.Idle ? "No directory loaded." : (_partial ? $"Partial listing: first {Rows.Count:N0} entries; sorting applies only to these entries."
        : $"{Rows.Count:N0} entries · Complete listing at time of enumeration.") + " Hidden files included.";
    public string SortSummary => $"Sorted by {_sort}, {(_descending ? "descending" : "ascending")}; folders first.";
    public IAsyncRelayCommand GoCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand UpCommand { get; }
    public IAsyncRelayCommand HomeCommand { get; }
    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand<string> NavigateCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand<LocalSortField> SortCommand { get; }

    public LocalBrowserViewModel(ILocalDirectoryBrowser files)
    {
        _files = files; _pathInput = files.HomeDirectory;
        GoCommand = new AsyncRelayCommand(() => NavigateAsync(PathInput), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        NavigateCommand = new AsyncRelayCommand<string>(path => NavigateAsync(path ?? ""), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        RefreshCommand = new AsyncRelayCommand(() => NavigateAsync(DirectoryPath, true), () => DirectoryPath.Length != 0,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        UpCommand = new AsyncRelayCommand(() => NavigateAsync(Parent() ?? DirectoryPath), () => Parent() is not null,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        HomeCommand = new AsyncRelayCommand(() => NavigateAsync(files.HomeDirectory), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        OpenCommand = new AsyncRelayCommand(() => NavigateAsync(SelectedRows[0].Entry.FullPath),
            () => SelectedRows.Count == 1 && SelectedRows[0].Entry.CanEnter && !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        SortCommand = new RelayCommand<LocalSortField>(Sort);
        SelectedRows.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(SelectionSummary)); OpenCommand.NotifyCanExecuteChanged(); };
    }
    public async Task InitializeAsync()
    {
        if (_disposed || DirectoryPath.Length != 0) return;
        var generation = _generation;
        // Root discovery is read-only and only invoked by actual app startup, never by construction/design.
        try { Roots = await _files.GetRootsAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { if (!_disposed) Roots = []; }
        if (!_disposed && generation == _generation) await NavigateAsync(_files.HomeDirectory);
    }
    private string? Parent()
    {
        try { return DirectoryPath.Length == 0 ? null : Directory.GetParent(DirectoryPath)?.FullName; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
    public async Task NavigateAsync(string path, bool keepSelection = false)
    {
        if (_disposed) return;
        var selection = keepSelection ? SelectedRows.Select(r => r.Entry.FullPath).ToHashSet(StringComparer.Ordinal) : [];
        _operation?.Cancel();
        var generation = ++_generation;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        Rows = []; SelectedRows.Clear(); _partial = false;
        State = LocalBrowserState.Loading; Status = "Loading directory…";
        OnPropertyChanged(nameof(ListingSummary)); OpenCommand.NotifyCanExecuteChanged();
        try
        {
            var result = await _files.ReadAsync(path, cancellation.Token);
            if (_disposed || generation != _generation) return;
            if (result.Error != LocalBrowseError.InvalidPath) { DirectoryPath = result.Directory; PathInput = result.Directory; }
            _partial = result.Error != LocalBrowseError.None || result.IsPartial || result.Entries.Count > LocalBrowsingLimits.MaximumEntries;
            Rows = result.Entries.Take(LocalBrowsingLimits.MaximumEntries).Select(e => new LocalRow(e)).ToArray();
            ApplySort(selection);
            State = result.Error switch
            {
                LocalBrowseError.None => Rows.Count == 0 ? LocalBrowserState.Empty : LocalBrowserState.Ready,
                LocalBrowseError.Cancelled => LocalBrowserState.Cancelled, _ => LocalBrowserState.Error
            };
            Status = result.Error switch
            {
                LocalBrowseError.None => Rows.Count == 0 ? "Empty directory." : _partial ? "Directory limit reached; listing is partial." : "Directory loaded.",
                LocalBrowseError.InvalidPath => "Enter an absolute directory path without . or .. segments.",
                LocalBrowseError.AccessDenied => "Directory access denied. Check permissions or choose another location.",
                LocalBrowseError.NotFound => "Directory no longer exists or its drive is disconnected.",
                LocalBrowseError.Cancelled => "Listing cancelled. Refresh to retry.",
                _ => "Directory unavailable. Check the path or drive and refresh."
            };
            if (result.Error != LocalBrowseError.None && Rows.Count > 0) Status += " Listing is incomplete.";
            if (Rows.Any(r => !r.Entry.MetadataAvailable)) Status += " Some entries disappeared or their metadata is unavailable.";
            if (keepSelection && SelectedRows.Count != selection.Count) Status += " Some selected entries are no longer listed.";
        }
        catch (OperationCanceledException) { if (!_disposed && generation == _generation) { _partial = true; State = LocalBrowserState.Cancelled; Status = "Listing cancelled. Refresh to retry."; } }
        catch (Exception) { if (!_disposed && generation == _generation) { _partial = true; State = LocalBrowserState.Error; Status = "Could not read directory. Code BB-LOCAL."; } }
        finally
        {
            if (generation == _generation)
            {
                _operation = null;
                if (State == LocalBrowserState.Loading) State = LocalBrowserState.Cancelled;
                OnPropertyChanged(nameof(ListingSummary)); RefreshCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged();
            }
        }
    }
    private void Sort(LocalSortField field)
    {
        if (_sort == field) _descending = !_descending;
        else { _sort = field; _descending = false; }
        ApplySort(SelectedRows.Select(r => r.Entry.FullPath).ToHashSet(StringComparer.Ordinal));
    }
    private void ApplySort(HashSet<string> selection)
    {
        var comparer = Comparer<LocalRow>.Create((a, b) =>
        {
            var group = b.Entry.CanEnter.CompareTo(a.Entry.CanEnter);
            if (group != 0) return group;
            var comparison = _sort switch
            {
                LocalSortField.Size => Nullable.Compare(a.Entry.Size, b.Entry.Size),
                LocalSortField.Modified => Nullable.Compare(a.Entry.Modified, b.Entry.Modified),
                LocalSortField.Type => StringComparer.CurrentCultureIgnoreCase.Compare(a.Type, b.Type),
                _ => StringComparer.CurrentCultureIgnoreCase.Compare(a.Entry.Name, b.Entry.Name)
            };
            if (_descending) comparison = -comparison;
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Entry.Name, b.Entry.Name);
        });
        Rows = Rows.Order(comparer).ToArray();
        SelectedRows.Clear(); foreach (var row in Rows) if (selection.Contains(row.Entry.FullPath)) SelectedRows.Add(row);
        OnPropertyChanged(nameof(SortSummary));
    }
    public void Cancel()
    {
        ++_generation; _operation?.Cancel(); _operation = null;
        _partial = true; State = LocalBrowserState.Cancelled; Status = "Listing cancelled. Refresh to retry.";
        OnPropertyChanged(nameof(ListingSummary)); RefreshCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged();
    }
    public void Dispose() { if (_disposed) return; Cancel(); _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
