namespace BucketBus.Core;

public enum TransferPhase { Prepared, DestinationCreating, Transferring, Committing }
public sealed record TemporaryReceipt(string Platform, string Name, string ParentIdentity, string ContainerIdentity, string FileIdentity);

// Deliberately contains no ConnectionProfile/Credentials object or provider response text.
public sealed record TransferRecord(Guid Id, long Revision, Guid ProfileId, long ProfileRevision, string Target,
    string Endpoint, string Bucket, string Key, string LocalPath, bool Upload, TransferState State,
    TransferPhase Phase = TransferPhase.Prepared, long Bytes = 0, long? Total = null, string? Sha256 = null,
    TemporaryReceipt? Temporary = null, bool CleanupRequired = false, TransferError Error = TransferError.None,
    DateTimeOffset Created = default, DateTimeOffset Updated = default)
{
    public bool Resolved => (State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled) && !CleanupRequired;
    public bool CanReconcile => Upload && (State is TransferState.OutcomeUnknown or TransferState.RequiresReconciliation) && Sha256 is not null && Total is not null;
    public bool CanRestart => !Upload && Temporary is not null && (State is TransferState.Interrupted or TransferState.RequiresReconciliation);
    public TransferRecord Next(TransferState state) => this with { Revision = Revision + 1, State = state, Updated = DateTimeOffset.UtcNow };
    public void Validate()
    {
        if (Id == Guid.Empty || ProfileId == Guid.Empty || Revision < 0 || ProfileRevision < 1
            || string.IsNullOrEmpty(Target) || string.IsNullOrEmpty(Bucket) || string.IsNullOrEmpty(Key)
            || !Path.IsPathFullyQualified(LocalPath) || Bytes < 0 || Total < 0
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
            || old.Created != next.Created || old.Sha256 is not null && (old.Sha256 != next.Sha256 || old.Total != next.Total)) return false;
        if (old.Resolved) return false;
        return old.State switch
        {
            TransferState.Pending => next.State is TransferState.Running or TransferState.Interrupted or TransferState.Failed or TransferState.Cancelled,
            TransferState.Running => next.State is TransferState.Running or TransferState.Completed or TransferState.Failed or TransferState.Cancelled or TransferState.OutcomeUnknown or TransferState.Interrupted or TransferState.RequiresReconciliation,
            TransferState.OutcomeUnknown => next.State is TransferState.RequiresReconciliation,
            TransferState.Interrupted => next.State is TransferState.RequiresReconciliation,
            TransferState.RequiresReconciliation => next.State is TransferState.RequiresReconciliation or TransferState.OutcomeUnknown or TransferState.Completed
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
}
