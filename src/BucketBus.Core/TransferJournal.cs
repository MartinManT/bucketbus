namespace BucketBus.Core;

public enum TransferPhase { Prepared, DestinationCreating, Transferring, Committing, RetryDelay }
public sealed record TemporaryReceipt(string Platform, string Name, string ParentIdentity, string ContainerIdentity, string FileIdentity);

// Deliberately contains no ConnectionProfile/Credentials object or provider response text.
public sealed record TransferRecord(Guid Id, long Revision, Guid ProfileId, long ProfileRevision, string Target,
    string Endpoint, string Bucket, string Key, string LocalPath, bool Upload, TransferState State,
    TransferPhase Phase = TransferPhase.Prepared, long Bytes = 0, long? Total = null, string? Sha256 = null,
    TemporaryReceipt? Temporary = null, bool CleanupRequired = false, TransferError Error = TransferError.None,
    DateTimeOffset Created = default, DateTimeOffset Updated = default, long? QueueOrder = null, string? SourceIdentity = null,
    int Attempt = 0, int MaximumAttempts = 1, TransferError RetryError = TransferError.None, DateTimeOffset? RetryAt = null, bool RetryLimitReached = false, MultipartUpload? Multipart = null)
{
    public bool Resolved => (State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled) && !CleanupRequired;
    public bool CanResumeMultipart => Upload && !Resolved && State != TransferState.Running && Multipart?.CanResume == true && SourceIdentity is not null;
    public bool CanAbortMultipart => Upload && !Resolved && State != TransferState.Running && Multipart?.CanAbort == true;
    public bool CanReconcile => Upload && (Multipart is null || Multipart.CompletionUncertain) && (State is TransferState.OutcomeUnknown or TransferState.RequiresReconciliation) && Sha256 is not null && Total is not null;
    public bool CanRestart => !Upload && Phase != TransferPhase.DestinationCreating && Temporary is not null && (State is TransferState.Interrupted or TransferState.RequiresReconciliation);
    public TransferRecord Next(TransferState state) => this with { Revision = Revision + 1, State = state, Updated = DateTimeOffset.UtcNow };
    public void Validate()
    {
        if (Multipart is not null)
        {
            if (!Upload || Total is null || Sha256 is null || State == TransferState.Queued
                || State == TransferState.Completed && Multipart.Phase != MultipartPhase.Verified) throw new JournalException();
            Multipart.Validate(Total.Value);
        }
        if (Id == Guid.Empty || ProfileId == Guid.Empty || Revision < 0 || ProfileRevision < 1
            || string.IsNullOrEmpty(Target) || string.IsNullOrEmpty(Bucket) || string.IsNullOrEmpty(Key)
            || !Path.IsPathFullyQualified(LocalPath) || Bytes < 0 || Total < 0
            || QueueOrder is <= 0 || State == TransferState.Queued && (QueueOrder is null || Phase != TransferPhase.Prepared || Temporary is not null || Bytes != 0 || Upload && Sha256 is null)
            || Attempt < 0 || MaximumAttempts is < 1 or > 3 || Attempt > MaximumAttempts || !Enum.IsDefined(RetryError)
            || Upload && (MaximumAttempts != 1 || Attempt > 1 || Phase == TransferPhase.RetryDelay || RetryAt is not null || RetryLimitReached)
            || State == TransferState.Running && Phase == TransferPhase.RetryDelay && (Attempt < 1 || RetryAt is null || Temporary is null)
            || RetryLimitReached && (Upload || Attempt != MaximumAttempts || !DownloadRetryPolicy.IsEligible(Error))
            || !Enum.IsDefined(State) || !Enum.IsDefined(Phase) || !Enum.IsDefined(Error)
            || !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"
            || Sha256 is not null && (!IsHash(Sha256) || Total is null))
            throw new JournalException();
    }
    private static bool IsHash(string hash) { try { return Convert.FromBase64String(hash).Length == 32; } catch { return false; } }
    public static bool Allows(TransferRecord old, TransferRecord next)
    {
        if (old.Id != next.Id || old.Revision + 1 != next.Revision || old.ProfileId != next.ProfileId
            || old.ProfileRevision != next.ProfileRevision || old.Target != next.Target || old.Endpoint != next.Endpoint
            || old.Bucket != next.Bucket || old.Key != next.Key || old.LocalPath != next.LocalPath || old.Upload != next.Upload
            || old.QueueOrder != next.QueueOrder || old.SourceIdentity is not null && old.SourceIdentity != next.SourceIdentity
            || old.Created != next.Created || old.Sha256 is not null && (old.Sha256 != next.Sha256 || old.Total != next.Total)) return false;
        if (old.Multipart is { } previous && (next.Multipart is not { } current || previous.Plan != current.Plan
            || previous.UploadId is not null && previous.UploadId != current.UploadId
            || previous.Confirmed.Length > current.Confirmed.Length
            || !previous.Confirmed.SequenceEqual(current.Confirmed.Take(previous.Confirmed.Length))
            || previous.CompletionUncertain && current.Phase is not (MultipartPhase.Completing or MultipartPhase.Verifying or MultipartPhase.Verified or MultipartPhase.CompletionRejected))) return false;
        if (old.Resolved) return false;
        if (old.State == TransferState.Running && next.State == TransferState.Running)
        {
            if (next.Attempt != old.Attempt && !(old.Attempt == 0 && next.Attempt == 1)
                && !(!old.Upload && old.Phase == TransferPhase.RetryDelay && next.Phase == TransferPhase.DestinationCreating && next.Attempt == old.Attempt + 1)) return false;
            if (old.Attempt > 0 && next.MaximumAttempts != old.MaximumAttempts) return false;
        }
        return old.State switch
        {
            TransferState.Queued => next.State is TransferState.Pending,
            TransferState.Pending => next.State is TransferState.Running or TransferState.Interrupted or TransferState.Failed or TransferState.Cancelled,
            TransferState.Running => next.State is TransferState.Running or TransferState.Completed or TransferState.Failed or TransferState.Cancelled or TransferState.OutcomeUnknown or TransferState.Interrupted or TransferState.RequiresReconciliation,
            TransferState.OutcomeUnknown => next.State is TransferState.RequiresReconciliation,
            TransferState.Interrupted => next.State is TransferState.RequiresReconciliation,
            TransferState.Cancelled or TransferState.Failed => old.Multipart is not null && next.State == TransferState.RequiresReconciliation,
            TransferState.RequiresReconciliation => next.State is TransferState.RequiresReconciliation or TransferState.OutcomeUnknown or TransferState.Completed
                || old.Multipart is not null && next.State is TransferState.Running or TransferState.Interrupted or TransferState.Cancelled or TransferState.Failed
                || !old.Upload && next.State is TransferState.Pending,
            _ => false
        };
    }
}
public sealed class JournalException() : Exception("Transfer journal unavailable; transfers are blocked (BB-JOURNAL).");
public interface ITransferJournal
{
    // First call acquires exclusive application ownership and recovers states, never performs transfer I/O.
    Task<IReadOnlyList<TransferRecord>> ReadAsync(CancellationToken token);
    Task SaveAsync(TransferRecord record, CancellationToken token);
    Task DismissAsync(Guid id, CancellationToken token);
    Task<TransferRecord> EnqueueAsync(TransferRecord record, CancellationToken token) => throw new JournalException();
    Task<bool> RemoveQueuedAsync(Guid id, CancellationToken token) => throw new JournalException();
    Task<TransferRecord?> ClaimQueuedAsync(Guid id, CancellationToken token) => throw new JournalException();
}
