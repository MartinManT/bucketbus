namespace BucketBus.Core;

public sealed record QueueUpdate(TransferRecord Record, TransferProgress? Progress = null, TransferResult? Result = null);
public sealed record QueueRunResult(int Completed, TransferResult? StoppedBy = null, TransferRecord? LastRecord = null);

/// <summary>Explicit FIFO loop over the existing journal. One worker; no retry or startup scheduling.</summary>
public sealed class TransferQueue(ProfileManager profiles, ITransferFiles files, TransferCoordinator coordinator, ITransferJournal journal)
{
    private readonly SemaphoreSlim _enqueue = new(1, 1);
    private readonly object _state = new();
    private int _running;
    private bool _pause;
    private CancellationTokenSource? _active;
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public async Task<TransferRecord> EnqueueAsync(ConnectionProfile profile, string bucket, string key, string localPath, bool upload, CancellationToken token, long? sizeHint = null)
    {
        await _enqueue.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var snapshot = await profiles.ReadAsync(token).ConfigureAwait(false);
            if (snapshot.Status != ProfileStatus.Success || !snapshot.Snapshot!.Profiles.Contains(profile)) throw new TransferException(TransferError.ProfileChanged);
            var records = await journal.ReadAsync(token).ConfigureAwait(false);
            if (records.Any(r => !r.Resolved && (upload ? r.Upload && new Uri(r.Endpoint) == new Uri(profile.Settings.Endpoint) && r.Bucket == bucket && r.Key == key
                : !r.Upload && r.LocalPath == localPath && TransferNames.FromKey(r.Key) == TransferNames.FromKey(key))))
                throw new TransferException(TransferError.RecoveryUnavailable);
            if (!upload) TransferNames.FromKey(key);
            var now = DateTimeOffset.UtcNow;
            var record = new TransferRecord(Guid.NewGuid(), 0, profile.Id, profile.Revision, profile.Settings.TargetFingerprint(),
                profile.Settings.Endpoint, bucket, key, localPath, upload, TransferState.Queued, Total: sizeHint is >= 0 ? sizeHint : null, Created: now, Updated: now);
            if (upload)
            {
                await using var source = await files.OpenSourceAsync(localPath, token).ConfigureAwait(false);
                record = record with { Total = source.Length, Sha256 = source.Sha256, SourceIdentity = source.SourceIdentity };
            }
            snapshot = await profiles.ReadAsync(token).ConfigureAwait(false);
            if (snapshot.Status != ProfileStatus.Success || !snapshot.Snapshot!.Profiles.Contains(profile)) throw new TransferException(TransferError.ProfileChanged);
            return await journal.EnqueueAsync(record, token).ConfigureAwait(false);
        }
        finally { _enqueue.Release(); }
    }
    public Task<bool> RemoveAsync(Guid id, CancellationToken token) => journal.RemoveQueuedAsync(id, token);
    public void PauseAfterCurrent() { lock (_state) _pause = true; }
    public void CancelActive()
    {
        CancellationTokenSource? active;
        lock (_state) { _pause = true; active = _active; }
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
    }
    public async Task<QueueRunResult> RunAsync(bool allowUnlock, IProgress<QueueUpdate>? updates, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return new(0, new(TransferState.Failed, TransferError.Busy));
        var completed = 0; TransferRecord? last = null;
        lock (_state) _pause = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                lock (_state) { if (_pause) break; }
                var records = await journal.ReadAsync(token).ConfigureAwait(false);
                var next = records.Where(r => r.State == TransferState.Queued).OrderBy(r => r.QueueOrder).FirstOrDefault();
                if (next is null) break;
                using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (_state) { if (_pause) break; _active = active; }
                TransferResult result;
                try
                {
                    updates?.Report(new(next with { State = TransferState.Running }));
                    result = await coordinator.ExecuteQueuedAsync(next.Id, allowUnlock,
                        new JobProgress(next, updates), active.Token).ConfigureAwait(false);
                }
                finally { lock (_state) _active = null; }
                var final = (await journal.ReadAsync(CancellationToken.None).ConfigureAwait(false)).SingleOrDefault(r => r.Id == next.Id);
                if (final is null && result.Error == TransferError.RecoveryUnavailable) continue; // Removed before the atomic claim.
                if (final is null) return new(completed, new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable));
                last = final; updates?.Report(new(final, Result: result));
                if (result.State != TransferState.Completed || result.CleanupRequired) return new(completed, result, last);
                completed++;
            }
            return new(completed, LastRecord: last);
        }
        catch (OperationCanceledException) { return new(completed, new(TransferState.Interrupted)); }
        catch { return new(completed, new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable)); }
        finally { lock (_state) _active = null; Volatile.Write(ref _running, 0); }
    }
    private sealed class JobProgress(TransferRecord record, IProgress<QueueUpdate>? updates) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress progress) => updates?.Report(new(record with
            { State = TransferState.Running, Bytes = progress.Bytes, Total = record.Upload ? record.Total : progress.Total,
                Attempt = progress.Attempt, MaximumAttempts = progress.MaximumAttempts, RetryError = progress.RetryError,
                Phase = progress.RetryDelay is null ? TransferPhase.Transferring : TransferPhase.RetryDelay,
                RetryAt = progress.RetryDelay is { } delay ? DateTimeOffset.UtcNow + delay : null }, progress));
    }
}
