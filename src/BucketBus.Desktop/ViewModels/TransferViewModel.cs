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
    private string _source = "", _destination = "", _status = "One file at a time. Uploads request create-only; no queue or crash recovery.", _bytes = "";
    private double _percent;
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); NotifyCommands(); } }
    public string Source { get => _source; private set => SetProperty(ref _source, value); }
    public string Destination { get => _destination; private set => SetProperty(ref _destination, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Bytes { get => _bytes; private set => SetProperty(ref _bytes, value); }
    public double Percent { get => _percent; private set => SetProperty(ref _percent, value); }
    public IAsyncRelayCommand UploadCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public TransferViewModel(TransferCoordinator coordinator, LocalBrowserViewModel local, RemoteBrowserViewModel remote)
    {
        _coordinator = coordinator; _local = local; _remote = remote;
        UploadCommand = new AsyncRelayCommand(() => RunAsync(true), () => Ready && _local.SelectedRows.Count == 1
            && _local.SelectedRows[0].Entry.Kind == LocalEntryKind.File && !_local.IsBusy);
        DownloadCommand = new AsyncRelayCommand(() => RunAsync(false), () => Ready && _remote.SelectedRow?.Kind == BrowserRowKind.Object
            && _local.DirectoryPath.Length != 0 && _local.State is LocalBrowserState.Ready or LocalBrowserState.Empty);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => IsBusy);
        _local.PropertyChanged += Changed; _remote.PropertyChanged += Changed;
        _local.SelectedRows.CollectionChanged += SelectionChanged;
    }
    private bool Ready => !_disposed && !IsBusy && _remote.IsConnected && _remote.ActiveBucket is not null && !_remote.IsBusy;
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NotifyCommands();
    private void SelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => NotifyCommands();
    private void NotifyCommands() { UploadCommand.NotifyCanExecuteChanged(); DownloadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); }
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
        IsBusy = true; Percent = 0; Bytes = "0 B"; Status = "Preparing transfer…";
        try
        {
            var progress = new ThrottledProgress(p =>
            {
                if (_operation != cancellation || _disposed) return;
                Bytes = p.Total is { } total ? $"{p.Bytes:N0} / {total:N0} B" : $"{p.Bytes:N0} B";
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
        finally { _operation = null; IsBusy = false; }
    }
    public static string Describe(TransferResult result)
    {
        var state = result.State switch
        {
            TransferState.Completed => "Completed. ", TransferState.Cancelled => "Cancelled. ",
            TransferState.OutcomeUnknown => "Outcome Unknown. Inspect the remote object before retrying; it was not automatically replayed or deleted. ",
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
            TransferError.LocalFailure => "Local file operation failed.", TransferError.Busy => "Another transfer is active.", _ => ""
        };
        return state + reason + result.Verification + (result.CleanupRequired ? " Owned temporary-file cleanup requires attention." : "");
    }
    public void Dispose()
    {
        _disposed = true; _operation?.Cancel();
        _local.PropertyChanged -= Changed; _remote.PropertyChanged -= Changed; _local.SelectedRows.CollectionChanged -= SelectionChanged;
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await Task.WhenAll(UploadCommand.ExecutionTask ?? Task.CompletedTask, DownloadCommand.ExecutionTask ?? Task.CompletedTask).ConfigureAwait(false); }
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
