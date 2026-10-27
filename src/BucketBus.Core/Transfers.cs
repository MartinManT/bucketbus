namespace BucketBus.Core;

public enum TransferState { Completed, Failed, Cancelled, OutcomeUnknown, Pending, Running, Interrupted, RequiresReconciliation, Queued }
public enum TransferError
{
    None, Conflict, InvalidName, UnsupportedFilesystem, PermissionDenied, DiskFull, LocalFailure,
    IntegrityMismatch, SourceChanged, NetworkFailure, Timeout, CredentialsUnavailable, ProfileChanged,
    ConditionalWriteUnsupported, RemoteRejected, Busy, CleanupRequired, AuthenticationFailed, JournalUnavailable, RecoveryUnavailable, ConnectionInterrupted, TruncatedResponse, RateLimited, TransientService, ObjectMissing, TlsFailure, DestinationChanged
}
public sealed record TransferResult(TransferState State, TransferError Error = TransferError.None,
    string Verification = "", bool CleanupRequired = false, int Attempts = 1, bool RetryLimitReached = false);
public sealed record TransferProgress(long Bytes, long? Total, string Stage, int Attempt = 1, int MaximumAttempts = 1, TimeSpan? RetryDelay = null, TransferError RetryError = TransferError.None);
public sealed class TransferException(TransferError error) : Exception("Transfer operation failed.")
{ public TransferError Error { get; } = error; }

public interface ITransferSource : IAsyncDisposable
{
    Stream Content { get; }
    long Length { get; }
    string Sha256 { get; }
    string Md5 { get; }
    string? SourceIdentity => null;
    Task ValidateAsync(CancellationToken token);
    Task ValidateIdentityAsync(CancellationToken token) => ValidateAsync(token);
}
public interface ITransferDestination : IAsyncDisposable
{
    Stream Content { get; }
    // The commit is the cancellation boundary: once committed the result is Completed.
    Task CommitAsync(CancellationToken token);
    bool CleanupRequired { get; }
    TemporaryReceipt? Receipt => null;
}
public interface ITransferFiles
{
    Task<ITransferSource> OpenSourceAsync(string path, CancellationToken token);
    Task<ITransferDestination> CreateDestinationAsync(string directory, string name, CancellationToken token, TemporaryReceipt? previous = null);
    Task ValidateRetryDestinationAsync(TransferRecord record, CancellationToken token) => throw new TransferException(TransferError.RecoveryUnavailable);
    Task RecoverDestinationAsync(TransferRecord record, CancellationToken token) => throw new TransferException(TransferError.RecoveryUnavailable);
}
public interface IObjectTransferSession : IAsyncDisposable
{
    Task<TransferResult> UploadAsync(string bucket, string key, ITransferSource source, IProgress<TransferProgress>? progress, CancellationToken token, Guid? operationId = null);
    Task<TransferResult> UploadMultipartAsync(TransferRecord record, ITransferSource source, Func<MultipartUpload, Task> persist,
        IProgress<TransferProgress>? progress, CancellationToken token, bool resume = false) =>
        Task.FromResult(new TransferResult(TransferState.Interrupted, TransferError.RecoveryUnavailable, CleanupRequired: true));
    Task<TransferResult> AbortMultipartAsync(TransferRecord record, Func<MultipartUpload, Task> persist, CancellationToken token) =>
        Task.FromResult(new TransferResult(TransferState.Interrupted, TransferError.RecoveryUnavailable, CleanupRequired: true));
    Task<TransferResult> ReconcileUploadAsync(TransferRecord record, CancellationToken token) => Task.FromResult(new TransferResult(TransferState.OutcomeUnknown, TransferError.RecoveryUnavailable));
    Task<TransferResult> DownloadAsync(string bucket, string key, Stream destination, IProgress<TransferProgress>? progress, CancellationToken token);
}
public interface IObjectTransferFactory
{ IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials); }

public static class TransferNames
{
    // A single visible leaf, never a trusted path; no silent sanitizing or normalization.
    public static string FromKey(string key)
    {
        if (key.Split('/').Any(p => p is "." or "..")) throw new TransferException(TransferError.InvalidName);
        var name = key[(key.LastIndexOf('/') + 1)..];
        Validate(name);
        return name;
    }
    public static void Validate(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0
            || name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new TransferException(TransferError.InvalidName);
        try { if (new System.Text.UTF8Encoding(false, true).GetByteCount(name) > 240) throw new TransferException(TransferError.InvalidName); }
        catch (System.Text.EncoderFallbackException) { throw new TransferException(TransferError.InvalidName); }
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]))
            throw new TransferException(TransferError.InvalidName);
    }
}
