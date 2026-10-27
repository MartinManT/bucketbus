namespace BucketBus.Core;

/// <summary>One explicit transfer, with durable barriers before effects. Never replays on startup.</summary>
public sealed class TransferCoordinator(ProfileManager profiles, ITransferFiles files, IObjectTransferFactory factory, ITransferJournal journal, DownloadRetryPolicy? downloadRetries = null)
{
    private readonly DownloadRetryPolicy _downloadRetries = downloadRetries ?? new();
    private int _active;
    public Task<IReadOnlyList<TransferRecord>> HistoryAsync(CancellationToken token) => journal.ReadAsync(token);
    public Task DismissAsync(Guid id, CancellationToken token) => journal.DismissAsync(id, token);

    public Task<TransferResult> RunAsync(ConnectionProfile profile, string bucket, string key, string localPath,
        bool upload, bool allowUnlock, IProgress<TransferProgress>? progress, CancellationToken token) =>
        ExecuteAsync(profile, bucket, key, localPath, upload, allowUnlock, progress, null, token);

    public async Task<TransferResult> ExecuteQueuedAsync(Guid id, bool allowUnlock, IProgress<TransferProgress>? progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _active, 1) != 0) return new(TransferState.Failed, TransferError.Busy);
        try
        {
            var record = await journal.ClaimQueuedAsync(id, token).ConfigureAwait(false);
            if (record is null) return new(TransferState.Failed, TransferError.RecoveryUnavailable);
            ProfileReadResult snapshot;
            try { snapshot = await profiles.ReadAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException)
            {
                await journal.SaveAsync(record.Next(TransferState.Cancelled), CancellationToken.None).ConfigureAwait(false);
                return new(TransferState.Cancelled);
            }
            var profile = snapshot.Status == ProfileStatus.Success ? snapshot.Snapshot?.Profiles.SingleOrDefault(p => p.Id == record.ProfileId
                && p.Revision == record.ProfileRevision && p.Settings.TargetFingerprint() == record.Target && p.Settings.Endpoint == record.Endpoint) : null;
            if (profile is null)
            {
                await journal.SaveAsync(record.Next(TransferState.Failed) with { Error = TransferError.ProfileChanged }, CancellationToken.None).ConfigureAwait(false);
                return new(TransferState.Failed, TransferError.ProfileChanged);
            }
            return await ExecuteAsync(profile, record.Bucket, record.Key, record.LocalPath, record.Upload, allowUnlock, progress, record, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(TransferState.Interrupted); }
        catch { return new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable); }
        finally { Volatile.Write(ref _active, 0); }
    }

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
                record = record.Next(TransferState.Pending) with { Phase = TransferPhase.Prepared, Temporary = null, Bytes = 0, Total = null, CleanupRequired = false, Error = TransferError.None, Attempt = 0, MaximumAttempts = 1, RetryAt = null, RetryError = TransferError.None, RetryLimitReached = false };
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
            record = record.Next(result.State == TransferState.Completed ? TransferState.Completed : TransferState.OutcomeUnknown) with { Error = result.Error, CleanupRequired = result.State != TransferState.Completed && record.CleanupRequired,
                Multipart = result.State == TransferState.Completed && record.Multipart is { } completed ? completed with { Phase = MultipartPhase.Verified } : record.Multipart };
            await journal.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
            return result with { State = record.State };
        }
        catch (JournalException) { return new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable); }
        catch (OperationCanceledException) { return new(TransferState.RequiresReconciliation, TransferError.RecoveryUnavailable); }
        catch { return new(TransferState.RequiresReconciliation, TransferError.RecoveryUnavailable); }
        finally { Volatile.Write(ref _active, 0); }
    }

    public async Task<TransferResult> RecoverMultipartAsync(Guid id, bool abort, bool confirmed, bool allowUnlock,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        if (!confirmed) return new(TransferState.Interrupted, TransferError.RecoveryUnavailable, CleanupRequired: true);
        if (Interlocked.Exchange(ref _active, 1) != 0) return new(TransferState.Failed, TransferError.Busy);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        DurableProgress? durable = null; Guid profileId = Guid.Empty;
        void Changed(Guid changed) { if (changed == profileId) lifetime.Cancel(); }
        profiles.ProfileChanged += Changed;
        try
        {
            var ct = lifetime.Token;
            var record = (await journal.ReadAsync(ct).ConfigureAwait(false)).SingleOrDefault(r => r.Id == id);
            if (record is null || !(abort ? record.CanAbortMultipart : record.CanResumeMultipart))
                return new(TransferState.Interrupted, TransferError.RecoveryUnavailable, CleanupRequired: true);
            profileId = record.ProfileId;
            var snapshot = await profiles.ReadAsync(ct).ConfigureAwait(false);
            var profile = snapshot.Status == ProfileStatus.Success ? snapshot.Snapshot?.Profiles.SingleOrDefault(p => p.Id == record.ProfileId
                && p.Revision == record.ProfileRevision && p.Settings.Endpoint == record.Endpoint && p.Settings.TargetFingerprint() == record.Target) : null;
            if (profile is null) return new(record.State, TransferError.ProfileChanged, CleanupRequired: true);
            record = record.Next(TransferState.RequiresReconciliation); await journal.SaveAsync(record, ct).ConfigureAwait(false);
            durable = new(journal, record, progress, lifetime);
            var credential = await profiles.ReadCredentialAsync(profile.Id, profile.Revision, allowUnlock, ct).ConfigureAwait(false);
            if (credential.Status != CredentialStatus.Found || credential.Value is null)
                return new(record.State, TransferError.CredentialsUnavailable, CleanupRequired: true);
            var latest = await profiles.ReadAsync(ct).ConfigureAwait(false);
            if (latest.Status != ProfileStatus.Success || !latest.Snapshot!.Profiles.Contains(profile))
                return new(record.State, TransferError.ProfileChanged, CleanupRequired: true);
            await using var remote = factory.Create(profile, credential.Value);
            TransferResult result;
            if (abort) result = await remote.AbortMultipartAsync(record, durable.SaveMultipartAsync, ct).ConfigureAwait(false);
            else
            {
                // Reopen, hash and compare before ListParts or any further upload. Never trust saved part counts alone.
                await using var source = await files.OpenSourceAsync(record.LocalPath, ct).ConfigureAwait(false);
                if (source.Length != record.Total || source.Sha256 != record.Sha256 || source.SourceIdentity != record.SourceIdentity)
                    return new(record.State, TransferError.SourceChanged, CleanupRequired: true);
                await durable.UpdateAsync(r => r.Next(TransferState.Running)).ConfigureAwait(false);
                result = await remote.UploadMultipartAsync(record, source, durable.SaveMultipartAsync, progress, ct, resume: true).ConfigureAwait(false);
            }
            await durable.UpdateAsync(r => r.Next(result.State) with { Error = result.Error, CleanupRequired = result.CleanupRequired }).ConfigureAwait(false);
            return result;
        }
        catch (JournalException) { return new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable, CleanupRequired: true); }
        catch (TransferException e) { return new(TransferState.Interrupted, e.Error, CleanupRequired: true); }
        catch { return new(TransferState.Interrupted, TransferError.RecoveryUnavailable, CleanupRequired: true); }
        finally { profiles.ProfileChanged -= Changed; Volatile.Write(ref _active, 0); }
    }

    private async Task<TransferResult> ExecuteAsync(ConnectionProfile profile, string bucket, string key, string localPath,
        bool upload, bool allowUnlock, IProgress<TransferProgress>? progress, TransferRecord? restart, CancellationToken token)
    {
        if (restart is null && Interlocked.Exchange(ref _active, 1) != 0) return new(TransferState.Failed, TransferError.Busy);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        void Changed(Guid id) { if (id == profile.Id) lifetime.Cancel(); }
        profiles.ProfileChanged += Changed;
        DurableProgress? durable = null;
        TransferResult result;
        var uploadStarted = false;
        try
        {
            var ct = lifetime.Token;
            if (restart is not null) durable = new DurableProgress(journal, restart, progress, lifetime);
            var snapshot = await profiles.ReadAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (snapshot.Status != ProfileStatus.Success || !snapshot.Snapshot!.Profiles.Contains(profile))
                throw new TransferException(TransferError.ProfileChanged);
            if (restart is null || restart.QueueOrder is not null)
            {
                var history = await journal.ReadAsync(ct).ConfigureAwait(false);
                if (history.Any(r => r.Id != restart?.Id && r.State != TransferState.Queued && !r.Resolved && (upload ? r.Upload && new Uri(r.Endpoint) == new Uri(profile.Settings.Endpoint) && r.Bucket == bucket && r.Key == key
                    : !r.Upload && r.LocalPath == localPath && TransferNames.FromKey(r.Key) == TransferNames.FromKey(key))))
                    throw new TransferException(TransferError.RecoveryUnavailable);
            }
            var now = DateTimeOffset.UtcNow;
            var record = restart ?? new TransferRecord(Guid.NewGuid(), 0, profile.Id, profile.Revision,
                profile.Settings.TargetFingerprint(), profile.Settings.Endpoint, bucket, key, localPath, upload, TransferState.Pending, Created: now, Updated: now);
            if (restart is null) await journal.SaveAsync(record, ct).ConfigureAwait(false);
            durable = new DurableProgress(journal, record, progress, lifetime);
            if (upload)
            {
                var credential = await profiles.ReadCredentialAsync(profile.Id, profile.Revision, allowUnlock, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (credential.Status != CredentialStatus.Found || credential.Value is null) throw new TransferException(TransferError.CredentialsUnavailable);
                await using var remote = factory.Create(profile, credential.Value);
                await using var source = await files.OpenSourceAsync(localPath, ct).ConfigureAwait(false);
                if (record.QueueOrder is not null && (source.Sha256 != record.Sha256 || source.Length != record.Total
                    || source.SourceIdentity != record.SourceIdentity)) throw new TransferException(TransferError.SourceChanged);
                var latest = await profiles.ReadAsync(ct).ConfigureAwait(false);
                if (latest.Status != ProfileStatus.Success || !latest.Snapshot!.Profiles.Contains(profile))
                    throw new TransferException(TransferError.ProfileChanged);
                var multipart = source.Length >= MultipartPlan.Threshold ? new MultipartUpload(MultipartPlan.Create(source.Length), MultipartPhase.Creating) : null;
                await durable.UpdateAsync(r => r.Next(TransferState.Running) with { Phase = TransferPhase.Transferring, Total = source.Length, Sha256 = source.Sha256, Attempt = 1, MaximumAttempts = 1,
                    SourceIdentity = source.SourceIdentity, Multipart = multipart, CleanupRequired = multipart is not null }).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested(); uploadStarted = true;
                result = multipart is null ? await remote.UploadAsync(bucket, key, source, durable, ct, record.Id).ConfigureAwait(false)
                    : await remote.UploadMultipartAsync(durable.Current, source, durable.SaveMultipartAsync, durable, ct).ConfigureAwait(false);
            }
            else result = await DownloadWithRetriesAsync(profile, bucket, key, localPath, allowUnlock, durable, ct).ConfigureAwait(false);
        }
        catch (JournalException) { result = new(uploadStarted ? TransferState.RequiresReconciliation : TransferState.Failed, TransferError.JournalUnavailable); }
        catch (OperationCanceledException) { result = new(uploadStarted ? TransferState.OutcomeUnknown : TransferState.Cancelled); }
        catch (TransferException e) { result = new(uploadStarted ? TransferState.OutcomeUnknown : TransferState.Failed, e.Error); }
        catch { result = new(uploadStarted ? TransferState.OutcomeUnknown : TransferState.Failed, TransferError.LocalFailure); }
        finally
        {
            profiles.ProfileChanged -= Changed;
        }
        try
        {
            result = result with { Attempts = durable?.Current.Attempt ?? result.Attempts,
                CleanupRequired = result.CleanupRequired || durable?.Current.Multipart is { Phase: not (MultipartPhase.Verified or MultipartPhase.Aborted) } };
            if (durable is not null)
            {
                var final = result;
                await durable.UpdateAsync(r => r.Next(final.State) with { Error = final.Error, CleanupRequired = final.CleanupRequired, RetryAt = null, RetryLimitReached = final.RetryLimitReached }).ConfigureAwait(false);
            }
            return result;
        }
        catch { return new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable, CleanupRequired: result.CleanupRequired); }
        finally { if (restart is null) Volatile.Write(ref _active, 0); }
    }

    private async Task<TransferResult> DownloadWithRetriesAsync(ConnectionProfile profile, string bucket, string key, string directory,
        bool allowUnlock, DurableProgress durable, CancellationToken token)
    {
        TemporaryReceipt? previous = null;
        for (var attempt = 1; attempt <= DownloadRetryPolicy.TotalAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var latest = await profiles.ReadAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (latest.Status != ProfileStatus.Success || !latest.Snapshot!.Profiles.Contains(profile)) throw new TransferException(TransferError.ProfileChanged);
            var credential = await profiles.ReadCredentialAsync(profile.Id, profile.Revision, allowUnlock, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (credential.Status != CredentialStatus.Found || credential.Value is null) throw new TransferException(TransferError.CredentialsUnavailable);
            await durable.UpdateAsync(r => r.Next(TransferState.Running) with
            {
                Phase = TransferPhase.DestinationCreating, Attempt = attempt, MaximumAttempts = DownloadRetryPolicy.TotalAttempts,
                RetryAt = null, Error = TransferError.None, Bytes = 0, Total = attempt == 1 ? r.Total : null
            }).ConfigureAwait(false);
            ITransferDestination? destination = null; TransferResult result; var committing = false; var cleanupFailed = false;
            try
            {
                token.ThrowIfCancellationRequested();
                // The native adapter checks the prior parent identity and absence again on the pinned parent, before creating output.
                destination = await files.CreateDestinationAsync(directory, TransferNames.FromKey(key), token, previous).ConfigureAwait(false);
                await durable.UpdateAsync(r => r.Next(TransferState.Running) with { Phase = TransferPhase.Transferring, Temporary = destination.Receipt }).ConfigureAwait(false);
                await using (var remote = factory.Create(profile, credential.Value))
                    result = await remote.DownloadAsync(bucket, key, destination.Content, new AttemptProgress(durable, attempt), token).ConfigureAwait(false);
                if (result.State == TransferState.Completed)
                {
                    await durable.UpdateAsync(r => r.Next(TransferState.Running) with { Phase = TransferPhase.Committing }).ConfigureAwait(false);
                    committing = true; await destination.CommitAsync(token).ConfigureAwait(false); committing = false;
                }
            }
            catch (JournalException) { throw; }
            catch (OperationCanceledException) { result = new(committing ? TransferState.RequiresReconciliation : TransferState.Cancelled); }
            catch (TransferException e)
            { result = new(committing && e.Error != TransferError.Conflict || previous is not null && destination is null ? TransferState.RequiresReconciliation : TransferState.Failed, e.Error); }
            catch { result = new(committing ? TransferState.RequiresReconciliation : TransferState.Failed, TransferError.LocalFailure); }
            finally
            {
                if (destination is not null)
                    try { await destination.DisposeAsync().ConfigureAwait(false); } catch { cleanupFailed = true; }
            }
            result = result with { Attempts = attempt, CleanupRequired = cleanupFailed || destination?.CleanupRequired == true };
            if (result.State != TransferState.Failed || !DownloadRetryPolicy.IsEligible(result.Error)) return result;
            if (result.CleanupRequired) return result with { State = TransferState.RequiresReconciliation, Error = TransferError.CleanupRequired };
            if (attempt == DownloadRetryPolicy.TotalAttempts) return result with { RetryLimitReached = true };
            var receipt = durable.Current.Temporary;
            if (receipt is null) return new(TransferState.RequiresReconciliation, TransferError.RecoveryUnavailable, Attempts: attempt);
            try { await files.ValidateRetryDestinationAsync(durable.Current, token).ConfigureAwait(false); }
            catch (TransferException e) { return new(TransferState.RequiresReconciliation, e.Error, Attempts: attempt); }
            catch (OperationCanceledException) { throw; }
            catch { return new(TransferState.RequiresReconciliation, TransferError.RecoveryUnavailable, Attempts: attempt); }
            var delay = _downloadRetries.DelayAfter(attempt);
            await durable.UpdateAsync(r => r.Next(TransferState.Running) with
                { Phase = TransferPhase.RetryDelay, Error = result.Error, RetryError = result.Error, RetryAt = DateTimeOffset.UtcNow + delay, CleanupRequired = false }).ConfigureAwait(false);
            durable.Report(new(durable.Current.Bytes, durable.Current.Total,
                $"Network interrupted — retrying in {delay.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}s (next attempt {attempt + 1}/3)",
                attempt, DownloadRetryPolicy.TotalAttempts, delay, result.Error));
            await _downloadRetries.WaitAsync(delay, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            previous = receipt;
        }
        throw new InvalidOperationException("Download attempt budget exhausted.");
    }
    private sealed class AttemptProgress(DurableProgress progress, int attempt) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => progress.Report(value with
            { Attempt = attempt, MaximumAttempts = DownloadRetryPolicy.TotalAttempts, Stage = value.Stage + $" — attempt {attempt}/3" });
    }

    private sealed class DurableProgress(ITransferJournal journal, TransferRecord initial, IProgress<TransferProgress>? display, CancellationTokenSource cancellation) : IProgress<TransferProgress>
    {
        private readonly object _gate = new();
        private TransferRecord _record = initial;
        private long _last;
        private bool _failed;
        public TransferRecord Current { get { lock (_gate) return _record; } }
        public Task SaveMultipartAsync(MultipartUpload multipart) => UpdateAsync(r => r.Next(TransferState.Running) with
        {
            Multipart = multipart, Bytes = multipart.AcknowledgedBytes,
            Phase = multipart.CompletionUncertain ? TransferPhase.Committing : TransferPhase.Transferring,
            CleanupRequired = multipart.Phase is not (MultipartPhase.Verified or MultipartPhase.Aborted)
        });
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
