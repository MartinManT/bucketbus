using System.Diagnostics;
using Avalonia.Threading;
using BucketBus.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BucketBus.Desktop.ViewModels;

public sealed class TransferViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly TransferCoordinator _coordinator;
    private readonly TransferQueue _queue;
    private bool _enqueuing, _queueRun;
    private int _completedThisRun;
    private readonly LocalBrowserViewModel _local;
    private readonly RemoteBrowserViewModel _remote;
    private CancellationTokenSource? _operation, _preparation;
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
        set { if (SetProperty(ref _selectedHistory, value)) { ConfirmRestart = false; ConfirmMultipart = false; OnPropertyChanged(nameof(RecoveryDetails)); NotifyCommands(); } }
    }
    public bool ConfirmRestart { get => _confirmRestart; set { SetProperty(ref _confirmRestart, value); NotifyCommands(); } }
    private bool _confirmMultipart;
    public bool ConfirmMultipart { get => _confirmMultipart; set { SetProperty(ref _confirmMultipart, value); NotifyCommands(); } }
    public IAsyncRelayCommand ResumeMultipartCommand { get; }
    public IAsyncRelayCommand AbortMultipartCommand { get; }
    public bool AllowRecoveryUnlock { get; set; }
    public string HistoryStatus { get => _historyStatus; private set => SetProperty(ref _historyStatus, value); }
    private Guid? _recoveryFeedbackId;
    private string? _recoveryFeedback;
    // Keep the last explicit action result with its operation across a history reload.
    public string RecoveryFeedback => _recoveryFeedbackId == SelectedHistory?.Record.Id && _recoveryFeedback is not null
        ? _recoveryFeedback : RecoveryGuidance;
    private void SetRecoveryFeedback(Guid id, string message)
    {
        _recoveryFeedbackId = id; _recoveryFeedback = message;
        OnPropertyChanged(nameof(RecoveryFeedback));
    }
    public string RecoveryDetails => SelectedHistory?.Details ?? "Select a record to inspect its target and recovery options.";
    private bool _historyExpanded;
    public bool IsHistoryExpanded { get => _historyExpanded; set => SetProperty(ref _historyExpanded, value); }
    public bool ShowRemoveQueuedAction => SelectedHistory?.Record.State == TransferState.Queued;
    public bool ShowDownloadConfirmation => SelectedHistory?.Record.CanRestart == true;
    public bool ShowMultipartConfirmation => SelectedHistory?.Record is { } record && (record.CanResumeMultipart || record.CanAbortMultipart);
    public string RecoveryGuidance => SelectedHistory?.Record switch
    {
        null => "Select a transfer to see its recovery options.",
        { Resolved: true } => "This transfer is resolved. You can dismiss its history entry or add a new transfer.",
        { State: TransferState.Queued } => "Choose Start / Continue to run this queued job, or Remove queued job to discard it.",
        { CanReconcile: true } => "Completion uncertain: choose Reconcile upload before re-adding.",
        { CanResumeMultipart: true } => "Confirm Resume, or Abort to clean up before re-adding.",
        { CanAbortMultipart: true, Multipart.Phase: MultipartPhase.SendingPart } => "Part unacknowledged: confirm Abort, then re-add after cleanup.",
        { CanAbortMultipart: true } => "Confirm and choose Abort incomplete upload. After cleanup succeeds, add the file again to start over.",
        { Multipart.UploadId: null, Multipart: not null } => "No upload ID was saved. Safe resume and abort are unavailable; provider inspection is required. Keep this record.",
        { CanRestart: true } => "Confirm and choose Restart download to begin again from byte zero after destination checks.",
        _ => "Safe recovery is unavailable for this record. Keep it for inspection; a new transfer to the same destination remains blocked."
    };
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
    public bool IsEnqueuing { get => _enqueuing; private set { SetProperty(ref _enqueuing, value); NotifyCommands(); } }
    public string AttentionSummary => !_journalAvailable ? "Journal unavailable" :
        $"{History.Count(r => !r.Record.Resolved && r.Record.State is not (TransferState.Queued or TransferState.Running or TransferState.Pending))} unresolved · {History.Count(r => r.Record.State == TransferState.Failed)} failed";
    public string QueueSummary => $"{History.Count(r => r.Record.State == TransferState.Queued)} queued · {(IsBusy ? "1 active" : "paused / idle")} · {_completedThisRun} completed this run";
    public IAsyncRelayCommand StartQueueCommand { get; }
    public IRelayCommand PauseQueueCommand { get; }
    public IAsyncRelayCommand RemoveQueuedCommand { get; }
    public IAsyncRelayCommand UploadCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public TransferViewModel(TransferCoordinator coordinator, TransferQueue queue, LocalBrowserViewModel local, RemoteBrowserViewModel remote, DisplayOptions? display = null)
    {
        _coordinator = coordinator; _queue = queue; _local = local; _remote = remote;
        _display = display ?? new(); _display.PropertyChanged += DisplayChanged;
        ReloadHistoryCommand = new AsyncRelayCommand(InitializeAsync, () => !_disposed && !IsBusy);
        ReconcileCommand = new AsyncRelayCommand(() => RecoverAsync(false), () => HistoryReady && SelectedHistory?.Record.CanReconcile == true);
        RestartCommand = new AsyncRelayCommand(() => RecoverAsync(true), () => HistoryReady && ConfirmRestart && SelectedHistory?.Record.CanRestart == true);
        ResumeMultipartCommand = new AsyncRelayCommand(() => RecoverMultipartAsync(false), () => HistoryReady && ConfirmMultipart && SelectedHistory?.Record.CanResumeMultipart == true);
        AbortMultipartCommand = new AsyncRelayCommand(() => RecoverMultipartAsync(true), () => HistoryReady && ConfirmMultipart && SelectedHistory?.Record.CanAbortMultipart == true);
        DismissCommand = new AsyncRelayCommand(DismissAsync, () => HistoryReady && SelectedHistory?.Record.Resolved == true);
        StartQueueCommand = new AsyncRelayCommand(StartQueueAsync, () => HistoryReady && History.Any(r => r.Record.State == TransferState.Queued));
        PauseQueueCommand = new RelayCommand(() => { _queue.PauseAfterCurrent(); Status = "Pausing after the current transfer completes."; }, () => _queueRun);
        RemoveQueuedCommand = new AsyncRelayCommand(RemoveQueuedAsync, () => !_disposed && _journalAvailable && SelectedHistory?.Record.State == TransferState.Queued);
        UploadCommand = new AsyncRelayCommand(() => EnqueueAsync(true), () => Ready && _local.SelectedRows.Count > 0
            && _local.SelectedRows.All(r => r.Entry.Kind == LocalEntryKind.File && _local.Rows.Contains(r)) && !_local.IsBusy);
        DownloadCommand = new AsyncRelayCommand(() => EnqueueAsync(false), () => Ready && DownloadSelection().Length > 0
            && DownloadSelection().All(r => r.Kind == BrowserRowKind.Object && _remote.Rows.Contains(r))
            && _local.DirectoryPath.Length != 0 && _local.State is LocalBrowserState.Ready or LocalBrowserState.Empty);
        CancelCommand = new RelayCommand(() => { _queue.CancelActive(); _operation?.Cancel(); }, () => IsBusy);
        _local.PropertyChanged += Changed; _remote.PropertyChanged += Changed;
        _local.SelectedRows.CollectionChanged += SelectionChanged; _remote.SelectedRows.CollectionChanged += SelectionChanged;
    }
    private bool Ready => _journalAvailable && !_disposed && !IsEnqueuing && _remote.IsConnected && _remote.ActiveBucket is not null && !_remote.IsBusy;
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NotifyCommands();
    private void SelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => NotifyCommands();
    private void NotifyCommands() { OnPropertyChanged(nameof(ShowRemoveQueuedAction)); OnPropertyChanged(nameof(RecoveryGuidance)); OnPropertyChanged(nameof(RecoveryFeedback)); OnPropertyChanged(nameof(ShowDownloadConfirmation)); OnPropertyChanged(nameof(ShowMultipartConfirmation)); ResumeMultipartCommand.NotifyCanExecuteChanged(); AbortMultipartCommand.NotifyCanExecuteChanged(); UploadCommand.NotifyCanExecuteChanged(); DownloadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ReloadHistoryCommand.NotifyCanExecuteChanged(); ReconcileCommand.NotifyCanExecuteChanged(); RestartCommand.NotifyCanExecuteChanged(); DismissCommand.NotifyCanExecuteChanged(); StartQueueCommand.NotifyCanExecuteChanged(); PauseQueueCommand.NotifyCanExecuteChanged(); RemoveQueuedCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(QueueSummary)); OnPropertyChanged(nameof(AttentionSummary)); }
    private bool HistoryReady => !_disposed && !IsBusy && _journalAvailable;
    public async Task InitializeAsync()
    {
        try
        {
            var records = await _coordinator.HistoryAsync(CancellationToken.None);
            if (_disposed) return;
            var selected = SelectedHistory?.Record.Id;
            var ordered = records.OrderBy(r => r.QueueOrder is not null ? 0 : 1).ThenBy(r => r.QueueOrder).ThenByDescending(r => r.Created).ToArray();
            History.Clear(); var position = 0;
            foreach (var record in ordered)
                History.Add(new(record, _display) { Position = record.State == TransferState.Queued ? ++position : null });
            SelectedHistory = History.FirstOrDefault(r => r.Record.Id == selected);
            _journalAvailable = true;
            HistoryStatus = $"{History.Count} recorded transfers. Start / Continue runs only queued jobs; recovery remains explicit.";
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
    private async Task RecoverMultipartAsync(bool abort)
    {
        if (SelectedHistory is not { } selected || !HistoryReady || !ConfirmMultipart) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation; IsBusy = true;
        var updates = new Progress<TransferProgress>(p =>
        {
            if (_disposed || _operation != cancellation) return;
            Status = p.Stage; SetRecoveryFeedback(selected.Record.Id, p.Stage); _progressBytes = p.Bytes; _progressTotal = p.Total; NotifyByteDisplay();
            Percent = p.Total > 0 ? Math.Min(99, 100d * p.Bytes / p.Total.Value) : 0;
        });
        try
        {
            Status = abort ? "Checking and aborting only the recorded incomplete upload…" : "Checking source identity and reconciling remote parts…";
            SetRecoveryFeedback(selected.Record.Id, Status);
            var result = await _coordinator.RecoverMultipartAsync(selected.Record.Id, abort, true, AllowRecoveryUnlock, updates, cancellation.Token);
            if (!_disposed)
            {
                Status = Describe(result);
                SetRecoveryFeedback(selected.Record.Id, abort ? AbortFeedback(result) : Status);
                if (result.State == TransferState.Completed) Percent = 100;
            }
        }
        catch
        {
            Status = "Multipart recovery remains unresolved; retain the record for inspection.";
            SetRecoveryFeedback(selected.Record.Id, Status);
        }
        finally { _operation = null; IsBusy = false; ConfirmMultipart = false; await InitializeAsync(); }
    }
    private static string AbortFeedback(TransferResult result) => result switch
    {
        { State: TransferState.Cancelled, Error: TransferError.None, CleanupRequired: false } => "Incomplete upload aborted and cleanup confirmed. Add the file again to start over.",
        { Error: TransferError.ProfileChanged } => "Abort blocked: the saved profile revision or target has changed. Reconnecting does not rebind this transfer. Keep the record for inspection.",
        { Error: TransferError.CredentialsUnavailable } => "Abort blocked: credentials for the saved profile are unavailable. Check the vault or session credentials. No abort request was sent.",
        _ => "Abort not confirmed. " + Describe(result)
    };
    private async Task DismissAsync()
    {
        if (SelectedHistory is not { } selected || !HistoryReady || !selected.Record.Resolved) return;
        IsBusy = true;
        try { await _coordinator.DismissAsync(selected.Record.Id, CancellationToken.None); }
        catch { HistoryStatus = "History could not be dismissed; the journal was preserved. BB-JOURNAL."; }
        finally { IsBusy = false; await InitializeAsync(); }
    }
    private BrowserRow[] DownloadSelection() => _remote.SelectedRows.Count > 0 ? _remote.SelectedRows.ToArray()
        : _remote.SelectedRow is { } row ? [row] : [];
    private async Task EnqueueAsync(bool upload)
    {
        var profile = _remote.ActiveProfile; var bucket = _remote.ActiveBucket;
        if (profile is null || bucket is null || _disposed || IsEnqueuing) return;
        var prefix = _remote.ActivePrefix; var directory = _local.DirectoryPath;
        // Snapshot explicitly selected, currently visible identities once, before any await.
        var localSelection = _local.SelectedRows.ToHashSet(); var remoteSelection = DownloadSelection().ToHashSet();
        var targets = upload ? _local.Rows.Where(r => localSelection.Contains(r) && r.Entry.Kind == LocalEntryKind.File)
            .Select(r => (Key: prefix + r.Entry.Name, Path: r.Entry.FullPath, Size: r.Entry.Size)).ToArray()
            : _remote.Rows.Where(r => remoteSelection.Contains(r) && r.Kind == BrowserRowKind.Object)
                .Select(r => (Key: r.Key, Path: directory, Size: r.Size)).ToArray();
        if (targets.Length == 0) return;
        using var preparation = new CancellationTokenSource(); _preparation = preparation;
        IsEnqueuing = true; var added = 0;
        Status = upload ? "Preparing queue entries; reading source fingerprints…" : "Recording selected downloads…";
        try
        {
            foreach (var target in targets)
            {
                if (_disposed) break;
                await _queue.EnqueueAsync(profile, bucket, target.Key, target.Path, upload, preparation.Token, target.Size); added++;
            }
            Status = $"Added {added} queued {(upload ? "uploads" : "downloads")}. Start / Continue to process them.";
        }
        catch (OperationCanceledException) { Status = $"Queue preparation cancelled; {added} recorded jobs retained."; }
        catch (TransferException e) when (e.Error == TransferError.RecoveryUnavailable)
        {
            IsHistoryExpanded = true;
            Status = $"Added {added} jobs. This destination has an unresolved transfer. Select its history record below to see the available recovery actions before adding it again.";
        }
        catch (TransferException e) { Status = $"Added {added} jobs; enqueue stopped. " + Describe(new(TransferState.Failed, e.Error)); }
        catch { Status = $"Added {added} jobs; durable enqueue failed. Existing jobs were retained. BB-JOURNAL."; }
        finally { _preparation = null; IsEnqueuing = false; await InitializeAsync(); }
    }
    private async Task RemoveQueuedAsync()
    {
        if (SelectedHistory is not { Record.State: TransferState.Queued } selected) return;
        try { Status = await _queue.RemoveAsync(selected.Record.Id, CancellationToken.None) ? "Removed never-started queued job; no data deleted." : "Job already started or was removed; reload its status."; }
        catch { Status = "Could not remove queued job. BB-JOURNAL."; }
        await InitializeAsync();
    }
    private async Task StartQueueAsync()
    {
        if (!HistoryReady) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation; _queueRun = true; _completedThisRun = 0; IsBusy = true;
        Status = "Processing queued jobs sequentially…";
        var updates = new QueueDisplayUpdate(update =>
        {
            if (_disposed || _operation != cancellation) return;
            var row = History.FirstOrDefault(r => r.Record.Id == update.Record.Id);
            if (row is null) { row = new(update.Record, _display); History.Add(row); }
            row.UpdateRecord(update.Record);
            if (update.Result is not null)
            {
                if (update.Result.State == TransferState.Completed) { _completedThisRun++; Percent = 100; }
                Status = Describe(update.Result);

            }
            else
            {
                Source = BrowserText.Display(Path.GetFileName(update.Record.Upload ? update.Record.LocalPath : update.Record.Key));
                Destination = update.Record.Upload ? "S3 upload · expand history for exact target" : "Local download · expand history for exact target";
                if (update.Progress is { } progress) Status = progress.Stage;
                _progressBytes = update.Progress?.Bytes ?? 0; _progressTotal = update.Progress?.Total ?? update.Record.Total; NotifyByteDisplay();
                Percent = _progressTotal > 0 ? Math.Min(99, 100d * _progressBytes.Value / _progressTotal.Value) : 0;
            }
            var position = 0; foreach (var item in History) item.Position = item.Record.State == TransferState.Queued ? ++position : null;
            NotifyCommands();
        });
        try
        {
            var result = await _queue.RunAsync(AllowRecoveryUnlock, updates, cancellation.Token);
            _completedThisRun = result.Completed;
            if (result.LastRecord is { } last)
            {
                _progressBytes = last.Bytes; _progressTotal = last.Total; NotifyByteDisplay();
                Percent = last.State == TransferState.Completed ? 100 : last.Total > 0 ? Math.Min(99, 100d * last.Bytes / last.Total.Value) : 0;
                if (last.State == TransferState.Completed) await RefreshAffectedAsync(last);
            }
            Status = result.StoppedBy is { } stopped ? "Queue stopped. " + Describe(stopped)
                : $"Queue paused / idle. {result.Completed} completed this run. Continue requires an explicit action.";
        }
        catch { Status = "Queue stopped; inspect durable records. BB-JOURNAL."; }
        finally { _operation = null; _queueRun = false; IsBusy = false; await InitializeAsync(); }
    }
    private async Task RefreshAffectedAsync(TransferRecord record)
    {
        if (record.Upload && _remote.ActiveProfile?.Id == record.ProfileId && _remote.ActiveBucket == record.Bucket
            && record.Key.StartsWith(_remote.ActivePrefix, StringComparison.Ordinal) && !_remote.IsBusy)
            await _remote.RefreshCommand.ExecuteAsync(null);
        else if (!record.Upload && _local.DirectoryPath == record.LocalPath && !_local.IsBusy) await _local.RefreshCommand.ExecuteAsync(null);
    }
    private sealed class QueueDisplayUpdate(Action<QueueUpdate> update) : IProgress<QueueUpdate>
    {
        private long _last; private int _attempt;
        public void Report(QueueUpdate value)
        {
            var now = Stopwatch.GetTimestamp();
            if (value.Progress is null) { _last = 0; _attempt = 0; }
            else
            {
                if (_last != 0 && _attempt == value.Progress.Attempt && value.Progress.RetryDelay is null && value.Progress.Bytes != value.Progress.Total && Stopwatch.GetElapsedTime(_last, now) < TimeSpan.FromMilliseconds(100)) return;
                _last = now; _attempt = value.Progress.Attempt;
            }
            if (Dispatcher.UIThread.CheckAccess()) update(value); else Dispatcher.UIThread.Post(() => update(value));
        }
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
            TransferState.Queued => "Queued; never started. ", TransferState.Running => "Running. ", TransferState.Pending => "Starting. ",
            TransferState.Completed => "Completed. ", TransferState.Cancelled => "Cancelled. ",
            TransferState.OutcomeUnknown => "Outcome Unknown. Reconciliation is required; no replay or deletion was attempted. ",
            TransferState.RequiresReconciliation => "Requires Reconciliation. The operation remains unresolved. ",
            TransferState.Interrupted => "Interrupted. No restart was attempted. ",
            _ => "Failed. "
        };
        if (result.RetryLimitReached) state = $"Failed — retry limit reached after {result.Attempts} attempts. ";
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
            TransferError.ConnectionInterrupted => "Connection interrupted.", TransferError.TruncatedResponse => "Remote response ended before the declared length.",
            TransferError.RateLimited => "Provider throttled the request (HTTP 429).", TransferError.TransientService => "Provider returned a retryable service error (HTTP 500/502/503/504).",
            TransferError.ObjectMissing => "Object not found; no automatic retry.", TransferError.TlsFailure => "TLS validation or handshake failed; no automatic retry.",
            TransferError.DestinationChanged => "Destination parent identity changed; retry stopped.",
            TransferError.CleanupRequired => "Temporary cleanup is uncertain; no further attempt was started.",
            TransferError.CredentialsUnavailable => "Explicit credentials unavailable; review the profile.",
            TransferError.ProfileChanged => "Profile changed; reconnect before transferring.",
            TransferError.RemoteRejected => "Provider rejected the operation or its response could not be verified.",
            TransferError.JournalUnavailable => "Durable recording failed. Transfers are blocked; preserve the journal and restart BucketBus to inspect recovery. ",
            TransferError.RecoveryUnavailable => "Safe recovery could not be established. Restart is unavailable; retain the record for inspection. ",
            TransferError.LocalFailure => "Local file operation failed.", TransferError.Busy => "Another transfer is active.", _ => ""
        };
        return state + reason + result.Verification + (result.CleanupRequired ? " Incomplete multipart or owned temporary-file cleanup requires attention." : "");
    }
    public void Dispose()
    {
        _queue.CancelActive();
        _display.PropertyChanged -= DisplayChanged;
        _disposed = true; _operation?.Cancel(); _preparation?.Cancel();
        _local.PropertyChanged -= Changed; _remote.PropertyChanged -= Changed; _local.SelectedRows.CollectionChanged -= SelectionChanged; _remote.SelectedRows.CollectionChanged -= SelectionChanged;
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await Task.WhenAll(ResumeMultipartCommand.ExecutionTask ?? Task.CompletedTask, AbortMultipartCommand.ExecutionTask ?? Task.CompletedTask, UploadCommand.ExecutionTask ?? Task.CompletedTask, DownloadCommand.ExecutionTask ?? Task.CompletedTask, ReconcileCommand.ExecutionTask ?? Task.CompletedTask, RestartCommand.ExecutionTask ?? Task.CompletedTask, DismissCommand.ExecutionTask ?? Task.CompletedTask, ReloadHistoryCommand.ExecutionTask ?? Task.CompletedTask, StartQueueCommand.ExecutionTask ?? Task.CompletedTask, RemoveQueuedCommand.ExecutionTask ?? Task.CompletedTask).ConfigureAwait(false); }
        catch { /* Terminal errors are shown by the command; shutdown must not log raw details. */ }
    }

}

public sealed class TransferHistoryRow(TransferRecord record, DisplayOptions? display = null) : ObservableObject
{
    private TransferRecord _record = record;
    public TransferRecord Record => _record;
    private int? _position;
    public int? Position { get => _position; set { if (SetProperty(ref _position, value)) OnPropertyChanged(nameof(Label)); } }
    public void UpdateRecord(TransferRecord value) { _record = value; OnPropertyChanged(nameof(Record)); RefreshDisplay(); }
    private readonly DisplayOptions _display = display ?? new();
    public void RefreshDisplay() { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(Details)); }
    public string Label => $"{(Position is { } position ? "#" + position + " · " : "")}{(Record.Upload ? "Upload" : "Download")} · {StateLabel} · {BrowserText.Display(Record.Upload ? Path.GetFileName(Record.LocalPath) : _display.Current.RemoteNames == RemoteNameDisplay.FullKey ? Record.Key : Record.Key[(Record.Key.LastIndexOf('/') + 1)..])} · {ByteSizeFormatter.Format(Record.Bytes, _display.Current.FileSizes)} / {ByteSizeFormatter.Format(Record.Total, _display.Current.FileSizes)}";
    private string StateLabel => Record.State switch { TransferState.OutcomeUnknown => "Outcome Unknown", TransferState.RequiresReconciliation => "Requires Reconciliation", _ => Record.State.ToString() };
    public string Details => $"Operation {Record.Id:N} · {StateLabel} · {ByteSizeFormatter.Format(Record.Bytes, _display.Current.FileSizes)} recorded (exact: {ByteSizeFormatter.Exact(Record.Bytes)}){(Record.Total is { } total ? " / total " + ByteSizeFormatter.Format(total, _display.Current.FileSizes) + " (exact: " + ByteSizeFormatter.Exact(total) + ")" : " / total Unknown")}\n"
        + $"Profile {Record.ProfileId} revision {Record.ProfileRevision} · {BrowserText.Display(Record.Endpoint)}\n"
        + $"{BrowserText.Display(Record.Upload ? Record.LocalPath : Record.Bucket + "/" + Record.Key)} → {BrowserText.Display(Record.Upload ? Record.Bucket + "/" + Record.Key : Path.Combine(Record.LocalPath, Record.Key[(Record.Key.LastIndexOf('/') + 1)..]))}\n"
        + $"Attempt {Record.Attempt}/{Record.MaximumAttempts}{(Record.RetryLimitReached ? " · retry limit reached" : "")} · Last recorded phase: {Record.Phase}. {TransferViewModel.Describe(new(Record.State, Record.Error, CleanupRequired: Record.CleanupRequired))}\n"
        + (Record.RetryError != TransferError.None ? $"Previous retry cause: {Record.RetryError}.\n" : "")
        + (Record.Multipart is { } multipart ? $"Multipart: {multipart.Phase} · {multipart.Confirmed.Length}/{multipart.Plan.PartCount} parts acknowledged. "
            + (multipart.UploadId is null ? "No durable upload ID: initiation ownership is uncertain and provider inspection may be required. BucketBus cannot abort it. " : "")
            + (Record.CanResumeMultipart ? "Confirm and resume checks the unchanged source and ListParts before sending more data. " : "Resume is unavailable when ownership, a part acknowledgement or completion is uncertain. ")
            + (Record.CanAbortMultipart ? "Confirm abort removes only the recorded incomplete upload; it never deletes an object.\n" : "Automatic abort is unavailable.\n") : "")
        + (Record.State == TransferState.Queued ? "Never started. Start / Continue runs this job in FIFO order; it can be removed without deleting data."
            : Record.CanReconcile ? "Reconcile reads the full object. Missing, changed or inaccessible content remains unresolved. Upload restart is unavailable."
            : Record.CanRestart ? "Restart downloads current remote content from byte zero after checking the destination and recorded temporary identity. A conflicting final file or missing ownership evidence blocks restart."
            : Record.Resolved ? "Resolved history can be dismissed without deleting files or remote objects." : "Automatic recovery and upload restart are unavailable; retain this record for inspection.")
        + (Record.CleanupRequired ? " Temporary cleanup still requires attention." : "");
}
