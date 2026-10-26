namespace BucketBus.Core;

/// <summary>One explicit transfer, with durable barriers before effects. Never replays on startup.</summary>
public sealed class TransferCoordinator(ProfileManager profiles, ITransferFiles files, IObjectTransferFactory factory, ITransferJournal journal)
{
    private int _active;
    public Task<IReadOnlyList<TransferRecord>> HistoryAsync(CancellationToken token) => journal.ReadAsync(token);
    public Task DismissAsync(Guid id, CancellationToken token) => journal.DismissAsync(id, token);

    public Task<TransferResult> RunAsync(ConnectionProfile profile, string bucket, string key, string localPath,
        bool upload, bool allowUnlock, IProgress<TransferProgress>? progress, CancellationToken token) =>
        ExecuteAsync(profile, bucket, key, localPath, upload, allowUnlock, progress, null, token);

    public async Task<TransferResult> RecoverAsync(Guid id, bool confirmRestart, bool allowUnlock,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _active, 1) != 0) return new(TransferState.Failed, TransferError.Busy);
        try
        {
            var record = (await journal.ReadAsync(token).ConfigureAwait(false)).SingleOrDefault(r => r.Id == id);
            if (record is null || !(record.CanReconcile || record.CanRestart && confirmRestart))
                return new(TransferState.RequiresReconciliation, TransferError.RecoveryUnavailable);
            var snapshot = await profiles.ReadAsync(token).ConfigureAwait(false);
            var profile = snapshot.Snapshot?.Profiles.SingleOrDefault(p => p.Id == record.ProfileId
                && p.Revision == record.ProfileRevision && p.Settings.TargetFingerprint() == record.Target);
            if (profile is null) return new(record.State, TransferError.ProfileChanged);
            // Save the user's explicit recovery intent before credentials, cleanup or network access.
            record = record.Next(TransferState.RequiresReconciliation);
            await journal.SaveAsync(record, token).ConfigureAwait(false);
            if (!record.Upload)
            {
                try { await files.RecoverDestinationAsync(record, token).ConfigureAwait(false); }
                catch (TransferException e) { return new(record.State, e.Error, CleanupRequired: true); }
                // Same operation identity; restart current remote content from byte zero, never append.
                record = record.Next(TransferState.Pending) with { Phase = TransferPhase.Prepared, Temporary = null, Bytes = 0, Total = null, CleanupRequired = false, Error = TransferError.None };
                await journal.SaveAsync(record, token).ConfigureAwait(false);
                return await ExecuteAsync(profile, record.Bucket, record.Key, record.LocalPath, false, allowUnlock, progress, record, token).ConfigureAwait(false);
            }
            var credential = await profiles.ReadCredentialAsync(profile.Id, profile.Revision, allowUnlock, token).ConfigureAwait(false);
            if (credential.Status != CredentialStatus.Found || credential.Value is null) return new(record.State, TransferError.CredentialsUnavailable);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            void Changed(Guid changed) { if (changed == profile.Id) lifetime.Cancel(); }
            profiles.ProfileChanged += Changed;
            TransferResult result;
            try
            {
                var latest = await profiles.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (latest.Status != ProfileStatus.Success || !latest.Snapshot!.Profiles.Contains(profile)) return new(record.State, TransferError.ProfileChanged);
                await using var remote = factory.Create(profile, credential.Value);
                result = await remote.ReconcileUploadAsync(record, lifetime.Token).ConfigureAwait(false);
            }
            finally { profiles.ProfileChanged -= Changed; }
            // Only positive identity + complete content evidence resolves an uncertain PUT.
            record = record.Next(result.State == TransferState.Completed ? TransferState.Completed : TransferState.OutcomeUnknown) with { Error = result.Error };
            await journal.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
            return result with { State = record.State };
        }
        catch (JournalException) { return new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable); }
        catch (OperationCanceledException) { return new(TransferState.RequiresReconciliation, TransferError.RecoveryUnavailable); }
        catch { return new(TransferState.RequiresReconciliation, TransferError.RecoveryUnavailable); }
        finally { Volatile.Write(ref _active, 0); }
    }

    private async Task<TransferResult> ExecuteAsync(ConnectionProfile profile, string bucket, string key, string localPath,
        bool upload, bool allowUnlock, IProgress<TransferProgress>? progress, TransferRecord? restart, CancellationToken token)
    {
        if (restart is null && Interlocked.Exchange(ref _active, 1) != 0) return new(TransferState.Failed, TransferError.Busy);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        void Changed(Guid id) { if (id == profile.Id) lifetime.Cancel(); }
        profiles.ProfileChanged += Changed;
        ITransferDestination? destination = null;
        DurableProgress? durable = null;
        TransferResult result;
        var uploadStarted = false; var committing = false; var cleanupFailed = false;
        try
        {
            var ct = lifetime.Token;
            var snapshot = await profiles.ReadAsync(ct).ConfigureAwait(false);
            if (snapshot.Status != ProfileStatus.Success || !snapshot.Snapshot!.Profiles.Contains(profile))
                throw new TransferException(TransferError.ProfileChanged);
            if (restart is null)
            {
                var history = await journal.ReadAsync(ct).ConfigureAwait(false);
                if (history.Any(r => !r.Resolved && (upload ? r.Upload && r.Target == profile.Settings.TargetFingerprint() && r.Bucket == bucket && r.Key == key
                    : !r.Upload && r.LocalPath == localPath && TransferNames.FromKey(r.Key) == TransferNames.FromKey(key))))
                    throw new TransferException(TransferError.RecoveryUnavailable);
            }
            var now = DateTimeOffset.UtcNow;
            var record = restart ?? new TransferRecord(Guid.NewGuid(), 0, profile.Id, profile.Revision,
                profile.Settings.TargetFingerprint(), profile.Settings.Endpoint, bucket, key, localPath, upload, TransferState.Pending, Created: now, Updated: now);
            if (restart is null) await journal.SaveAsync(record, ct).ConfigureAwait(false);
            durable = new DurableProgress(journal, record, progress, lifetime);
            var credential = await profiles.ReadCredentialAsync(profile.Id, profile.Revision, allowUnlock, ct).ConfigureAwait(false);
            if (credential.Status != CredentialStatus.Found || credential.Value is null)
                throw new TransferException(TransferError.CredentialsUnavailable);
            ct.ThrowIfCancellationRequested();
            await using var remote = factory.Create(profile, credential.Value);
            if (upload)
            {
                await using var source = await files.OpenSourceAsync(localPath, ct).ConfigureAwait(false);
                var latest = await profiles.ReadAsync(ct).ConfigureAwait(false);
                if (latest.Status != ProfileStatus.Success || !latest.Snapshot!.Profiles.Contains(profile))
                    throw new TransferException(TransferError.ProfileChanged);
                await durable.UpdateAsync(r => r.Next(TransferState.Running) with { Phase = TransferPhase.Transferring, Total = source.Length, Sha256 = source.Sha256 }).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested(); uploadStarted = true;
                result = await remote.UploadAsync(bucket, key, source, durable, ct, record.Id).ConfigureAwait(false);
            }
            else
            {
                await durable.UpdateAsync(r => r.Next(TransferState.Running) with { Phase = TransferPhase.DestinationCreating }).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                destination = await files.CreateDestinationAsync(localPath, TransferNames.FromKey(key), ct).ConfigureAwait(false);
                await durable.UpdateAsync(r => r.Next(TransferState.Running) with { Phase = TransferPhase.Transferring, Temporary = destination.Receipt }).ConfigureAwait(false);
                result = await remote.DownloadAsync(bucket, key, destination.Content, durable, ct).ConfigureAwait(false);
                if (result.State == TransferState.Completed)
                {
                    await durable.UpdateAsync(r => r.Next(TransferState.Running) with { Phase = TransferPhase.Committing }).ConfigureAwait(false);
                    committing = true;
                    await destination.CommitAsync(ct).ConfigureAwait(false);
                    committing = false;
                }
            }
        }
        catch (JournalException) { result = new(uploadStarted || committing ? TransferState.RequiresReconciliation : TransferState.Failed, TransferError.JournalUnavailable); }
        catch (OperationCanceledException) { result = new(uploadStarted ? TransferState.OutcomeUnknown : committing ? TransferState.RequiresReconciliation : TransferState.Cancelled); }
        catch (TransferException e) { result = new(uploadStarted ? TransferState.OutcomeUnknown : committing && e.Error != TransferError.Conflict ? TransferState.RequiresReconciliation : TransferState.Failed, e.Error); }
        catch { result = new(uploadStarted ? TransferState.OutcomeUnknown : committing ? TransferState.RequiresReconciliation : TransferState.Failed, TransferError.LocalFailure); }
        finally
        {
            if (destination is not null)
                try { await destination.DisposeAsync().ConfigureAwait(false); } catch { cleanupFailed = true; }
            profiles.ProfileChanged -= Changed;
        }
        try
        {
            result = result with { CleanupRequired = cleanupFailed || destination?.CleanupRequired == true };
            if (durable is not null)
            {
                var final = result;
                await durable.UpdateAsync(r => r.Next(final.State) with { Error = final.Error, CleanupRequired = final.CleanupRequired }).ConfigureAwait(false);
            }
            return result;
        }
        catch { return new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable, CleanupRequired: result.CleanupRequired); }
        finally { if (restart is null) Volatile.Write(ref _active, 0); }
    }

    private sealed class DurableProgress(ITransferJournal journal, TransferRecord initial, IProgress<TransferProgress>? display, CancellationTokenSource cancellation) : IProgress<TransferProgress>
    {
        private readonly object _gate = new();
        private TransferRecord _record = initial;
        private long _last;
        private bool _failed;
        public Task UpdateAsync(Func<TransferRecord, TransferRecord> update) => Task.Run(() => Update(update));
        private void Update(Func<TransferRecord, TransferRecord> update)
        {
            lock (_gate)
            {
                if (_failed) throw new JournalException();
                try
                {
                    var next = update(_record);
                    journal.SaveAsync(next, CancellationToken.None).GetAwaiter().GetResult(); _record = next;
                }
                catch { _failed = true; cancellation.Cancel(); throw new JournalException(); }
            }
        }
        public void Report(TransferProgress value)
        {
            lock (_gate)
            {
                if (_record.State != TransferState.Running || _failed) return;
                var now = Environment.TickCount64;
                if (now - _last >= 1000 || value.Total == value.Bytes)
                {
                    Update(r => r.Next(TransferState.Running) with { Bytes = value.Bytes, Total = r.Upload ? r.Total : value.Total }); _last = now;
                }
                display?.Report(value);
            }
        }
    }
}
