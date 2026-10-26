using System.Diagnostics;
using Avalonia.Threading;
using BucketBus.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BucketBus.Desktop.ViewModels;

public sealed class TransferViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly TransferCoordinator _coordinator;
    private readonly LocalBrowserViewModel _local;
    private readonly RemoteBrowserViewModel _remote;
    private CancellationTokenSource? _operation;
    private bool _busy, _disposed;
    private string _source = "", _destination = "", _status = "One file at a time. Transfers are recorded; interrupted work is never automatically replayed.";
    private readonly DisplayOptions _display;
    private long? _progressBytes, _progressTotal;
    public string ExactBytes => _progressBytes is { } bytes ? ByteSizeFormatter.Exact(bytes) + (_progressTotal is { } total ? " / " + ByteSizeFormatter.Exact(total) : "") : "";
    private double _percent;
    private bool _journalAvailable = true, _confirmRestart;
    private TransferHistoryRow? _selectedHistory;
    private string _historyStatus = "History has not been loaded.";
    public System.Collections.ObjectModel.ObservableCollection<TransferHistoryRow> History { get; } = [];
    public TransferHistoryRow? SelectedHistory
    {
        get => _selectedHistory;
        set { if (SetProperty(ref _selectedHistory, value)) { ConfirmRestart = false; OnPropertyChanged(nameof(RecoveryDetails)); NotifyCommands(); } }
    }
    public bool ConfirmRestart { get => _confirmRestart; set { SetProperty(ref _confirmRestart, value); NotifyCommands(); } }
    public bool AllowRecoveryUnlock { get; set; }
    public string HistoryStatus { get => _historyStatus; private set => SetProperty(ref _historyStatus, value); }
    public string RecoveryDetails => SelectedHistory?.Details ?? "Select a record to inspect its target and recovery options.";
    public IAsyncRelayCommand ReloadHistoryCommand { get; }
    public IAsyncRelayCommand ReconcileCommand { get; }
    public IAsyncRelayCommand RestartCommand { get; }
    public IAsyncRelayCommand DismissCommand { get; }

    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); NotifyCommands(); } }
    public string Source { get => _source; private set => SetProperty(ref _source, value); }
    public string Destination { get => _destination; private set => SetProperty(ref _destination, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Bytes
    {
        get
        {
            if (_progressBytes is not { } bytes) return "";
            var text = ByteSizeFormatter.Format(bytes, _display.Current.FileSizes);
            if (_progressTotal is not { } total) return text;
            if (_display.Current.FileSizes == FileSizeDisplay.ExactBytes || bytes < 1024 && total < 1024) text = text[..^2];
            return text + " / " + ByteSizeFormatter.Format(total, _display.Current.FileSizes);
        }
    }
    public double Percent { get => _percent; private set => SetProperty(ref _percent, value); }
    public IAsyncRelayCommand UploadCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public TransferViewModel(TransferCoordinator coordinator, LocalBrowserViewModel local, RemoteBrowserViewModel remote, DisplayOptions? display = null)
    {
        _coordinator = coordinator; _local = local; _remote = remote;
        _display = display ?? new(); _display.PropertyChanged += DisplayChanged;
        ReloadHistoryCommand = new AsyncRelayCommand(InitializeAsync, () => !_disposed && !IsBusy);
        ReconcileCommand = new AsyncRelayCommand(() => RecoverAsync(false), () => HistoryReady && SelectedHistory?.Record.CanReconcile == true);
        RestartCommand = new AsyncRelayCommand(() => RecoverAsync(true), () => HistoryReady && ConfirmRestart && SelectedHistory?.Record.CanRestart == true);
        DismissCommand = new AsyncRelayCommand(DismissAsync, () => HistoryReady && SelectedHistory?.Record.Resolved == true);
        UploadCommand = new AsyncRelayCommand(() => RunAsync(true), () => Ready && _local.SelectedRows.Count == 1
            && _local.SelectedRows[0].Entry.Kind == LocalEntryKind.File && !_local.IsBusy);
        DownloadCommand = new AsyncRelayCommand(() => RunAsync(false), () => Ready && _remote.SelectedRow?.Kind == BrowserRowKind.Object
            && _local.DirectoryPath.Length != 0 && _local.State is LocalBrowserState.Ready or LocalBrowserState.Empty);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => IsBusy);
        _local.PropertyChanged += Changed; _remote.PropertyChanged += Changed;
        _local.SelectedRows.CollectionChanged += SelectionChanged;
    }
    private bool Ready => _journalAvailable && !_disposed && !IsBusy && _remote.IsConnected && _remote.ActiveBucket is not null && !_remote.IsBusy;
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NotifyCommands();
    private void SelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => NotifyCommands();
    private void NotifyCommands() { UploadCommand.NotifyCanExecuteChanged(); DownloadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ReloadHistoryCommand.NotifyCanExecuteChanged(); ReconcileCommand.NotifyCanExecuteChanged(); RestartCommand.NotifyCanExecuteChanged(); DismissCommand.NotifyCanExecuteChanged(); }
    private bool HistoryReady => !_disposed && !IsBusy && _journalAvailable;
    public async Task InitializeAsync()
    {
        try
        {
            var records = await _coordinator.HistoryAsync(CancellationToken.None);
            if (_disposed) return;
            var selected = SelectedHistory?.Record.Id;
            History.Clear(); foreach (var record in records) History.Add(new(record, _display));
            SelectedHistory = History.FirstOrDefault(r => r.Record.Id == selected);
            _journalAvailable = true;
            HistoryStatus = $"{History.Count} recorded transfers. Recovery requires an explicit action; nothing was replayed.";
        }
        catch { _journalAvailable = false; HistoryStatus = "Transfer journal unavailable. Transfers are blocked. Close other BucketBus instances; preserve the journal for diagnosis. BB-JOURNAL."; }
        NotifyCommands();
    }
    private async Task RecoverAsync(bool restart)
    {
        if (SelectedHistory is not { } selected || !HistoryReady || restart && !ConfirmRestart) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation; IsBusy = true;
        try
        {
            Status = restart ? "Checking destination and restarting from byte zero…" : "Reconciling saved operation identity and full content…";
            var result = await _coordinator.RecoverAsync(selected.Record.Id, restart, AllowRecoveryUnlock, null, cancellation.Token);
            if (!_disposed) Status = Describe(result);
        }
        catch { Status = "Recovery remains unresolved. No automatic replay was attempted."; }
        finally { _operation = null; IsBusy = false; ConfirmRestart = false; await InitializeAsync(); }
    }
    private async Task DismissAsync()
    {
        if (SelectedHistory is not { } selected || !HistoryReady || !selected.Record.Resolved) return;
        IsBusy = true;
        try { await _coordinator.DismissAsync(selected.Record.Id, CancellationToken.None); }
        catch { HistoryStatus = "History could not be dismissed; the journal was preserved. BB-JOURNAL."; }
        finally { IsBusy = false; await InitializeAsync(); }
    }
    private async Task RunAsync(bool upload)
    {
        var profile = _remote.ActiveProfile; var bucket = _remote.ActiveBucket;
        if (profile is null || bucket is null || _disposed || IsBusy) return;
        var directory = _local.DirectoryPath;
        var prefix = _remote.ActivePrefix;
        var key = upload ? prefix + _local.SelectedRows[0].Entry.Name : _remote.SelectedRow!.Key;
        var path = upload ? _local.SelectedRows[0].Entry.FullPath : directory;
        Source = BrowserText.Display(upload ? path : bucket + "/" + key);
        Destination = BrowserText.Display(upload ? bucket + "/" + key : Path.Combine(directory, key[(key.LastIndexOf('/') + 1)..]));
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        IsBusy = true; Percent = 0; _progressBytes = 0; _progressTotal = null; NotifyByteDisplay(); Status = "Preparing transfer…";
        try
        {
            var progress = new ThrottledProgress(p =>
            {
                if (_operation != cancellation || _disposed) return;
                _progressBytes = p.Bytes; _progressTotal = p.Total; NotifyByteDisplay();
                Percent = p.Total > 0 ? Math.Min(100, 100d * p.Bytes / p.Total.Value) : 0;
                Status = p.Stage + "…";
            });
            var result = await _coordinator.RunAsync(profile, bucket, key, path, upload, _remote.AllowUnlock, progress, cancellation.Token);
            progress.Flush();
            _operation = null; // discard queued progress after terminal state
            if (_disposed) return;
            Status = Describe(result);
            if (result.State == TransferState.Completed)
            {
                Percent = 100;
                // Browsing is independent: never navigate the user back to a previous location.
                if (upload && _remote.ActiveProfile == profile && _remote.ActiveBucket == bucket && _remote.ActivePrefix == prefix && !_remote.IsBusy)
                    await _remote.RefreshCommand.ExecuteAsync(null);
                if (!upload && _local.DirectoryPath == directory && !_local.IsBusy) await _local.RefreshCommand.ExecuteAsync(null);
            }
        }
        catch (Exception) { Status = "Outcome Unknown. Inspect the destination before retrying. Code BB-TRANSFER."; }
        finally { _operation = null; IsBusy = false; await InitializeAsync(); }
    }
    private void NotifyByteDisplay() { OnPropertyChanged(nameof(Bytes)); OnPropertyChanged(nameof(ExactBytes)); }
    private void DisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        NotifyByteDisplay(); foreach (var row in History) row.RefreshDisplay(); OnPropertyChanged(nameof(RecoveryDetails));
    }
    public static string Describe(TransferResult result)
    {
        var state = result.State switch
        {
            TransferState.Completed => "Completed. ", TransferState.Cancelled => "Cancelled. ",
            TransferState.OutcomeUnknown => "Outcome Unknown. Reconciliation is required; no replay or deletion was attempted. ",
            TransferState.RequiresReconciliation => "Requires Reconciliation. The operation remains unresolved. ",
            TransferState.Interrupted => "Interrupted. No restart was attempted. ",
            _ => "Failed. "
        };
        var reason = result.Error switch
        {
            TransferError.Conflict => "Destination already exists or a conditional-write conflict occurred. No unconditional replacement was attempted.",
            TransferError.ConditionalWriteUnsupported => "Provider does not support this conditional upload request or a required upload header. No unconditional replacement was attempted.",
            TransferError.AuthenticationFailed => "Provider rejected the credentials or request signature. Review the profile credentials and signing region.",
            TransferError.InvalidName => "Unsafe or unrepresentable filename/key; choose another object or directory.",
            TransferError.UnsupportedFilesystem => "This filesystem cannot provide the required safe commit.",
            TransferError.PermissionDenied => "Permission denied.", TransferError.DiskFull => "Insufficient disk space.",
            TransferError.SourceChanged => "The local source changed during transfer.", TransferError.IntegrityMismatch => "Content integrity verification failed.",
            TransferError.Timeout => "Transfer deadline exceeded.", TransferError.NetworkFailure => "Network or TLS operation failed.",
            TransferError.CredentialsUnavailable => "Explicit credentials unavailable; review the profile.",
            TransferError.ProfileChanged => "Profile changed; reconnect before transferring.",
            TransferError.RemoteRejected => "Provider rejected the operation or its response could not be verified.",
            TransferError.JournalUnavailable => "Durable recording failed. Transfers are blocked; preserve the journal and restart BucketBus to inspect recovery. ",
            TransferError.RecoveryUnavailable => "Safe recovery could not be established. Restart is unavailable; retain the record for inspection. ",
            TransferError.LocalFailure => "Local file operation failed.", TransferError.Busy => "Another transfer is active.", _ => ""
        };
        return state + reason + result.Verification + (result.CleanupRequired ? " Owned temporary-file cleanup requires attention." : "");
    }
    public void Dispose()
    {
        _display.PropertyChanged -= DisplayChanged;
        _disposed = true; _operation?.Cancel();
        _local.PropertyChanged -= Changed; _remote.PropertyChanged -= Changed; _local.SelectedRows.CollectionChanged -= SelectionChanged;
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await Task.WhenAll(UploadCommand.ExecutionTask ?? Task.CompletedTask, DownloadCommand.ExecutionTask ?? Task.CompletedTask, ReconcileCommand.ExecutionTask ?? Task.CompletedTask, RestartCommand.ExecutionTask ?? Task.CompletedTask, DismissCommand.ExecutionTask ?? Task.CompletedTask, ReloadHistoryCommand.ExecutionTask ?? Task.CompletedTask).ConfigureAwait(false); }
        catch { /* Terminal errors are shown by the command; shutdown must not log raw details. */ }
    }
    private sealed class ThrottledProgress(Action<TransferProgress> update) : IProgress<TransferProgress>
    {
        private long _last;
        private TransferProgress? _latest;
        public void Flush() { if (Volatile.Read(ref _latest) is { } value) update(value); }
        public void Report(TransferProgress value)
        {
            Volatile.Write(ref _latest, value);
            var now = Stopwatch.GetTimestamp(); var last = Interlocked.Read(ref _last);
            if (last != 0 && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(100)) return;
            Interlocked.Exchange(ref _last, now); Dispatcher.UIThread.Post(() => update(value));
        }
    }
}

public sealed class TransferHistoryRow(TransferRecord record, DisplayOptions? display = null) : ObservableObject
{
    public TransferRecord Record { get; } = record;
    private readonly DisplayOptions _display = display ?? new();
    public void RefreshDisplay() { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(Details)); }
    public string Label => $"{Record.Created.LocalDateTime:g} · {(Record.Upload ? "Upload" : "Download")} · {StateLabel} · {BrowserText.Display(Record.Key)} · {ByteSizeFormatter.Format(Record.Bytes, _display.Current.FileSizes)}";
    private string StateLabel => Record.State switch { TransferState.OutcomeUnknown => "Outcome Unknown", TransferState.RequiresReconciliation => "Requires Reconciliation", _ => Record.State.ToString() };
    public string Details => $"Operation {Record.Id:N} · {StateLabel} · {ByteSizeFormatter.Format(Record.Bytes, _display.Current.FileSizes)} recorded (exact: {ByteSizeFormatter.Exact(Record.Bytes)}){(Record.Total is { } total ? " / total " + ByteSizeFormatter.Format(total, _display.Current.FileSizes) + " (exact: " + ByteSizeFormatter.Exact(total) + ")" : " / total Unknown")}\n"
        + $"Profile {Record.ProfileId} revision {Record.ProfileRevision} · {BrowserText.Display(Record.Endpoint)}\n"
        + $"{BrowserText.Display(Record.Upload ? Record.LocalPath : Record.Bucket + "/" + Record.Key)} → {BrowserText.Display(Record.Upload ? Record.Bucket + "/" + Record.Key : Path.Combine(Record.LocalPath, Record.Key[(Record.Key.LastIndexOf('/') + 1)..]))}\n"
        + $"Last recorded phase: {Record.Phase}. {TransferViewModel.Describe(new(Record.State, Record.Error, CleanupRequired: Record.CleanupRequired))}\n"
        + (Record.CanReconcile ? "Reconcile reads the full object. Missing, changed or inaccessible content remains unresolved. Upload restart is unavailable."
            : Record.CanRestart ? "Restart downloads current remote content from byte zero after checking the destination and recorded temporary identity. A conflicting final file or missing ownership evidence blocks restart."
            : Record.Resolved ? "Resolved history can be dismissed without deleting files or remote objects." : "Automatic recovery and upload restart are unavailable; retain this record for inspection.")
        + (Record.CleanupRequired ? " Temporary cleanup still requires attention." : "");
}
