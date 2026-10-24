namespace BucketBus.Core;

public enum StorageError
{
    None, InvalidRequest, AccessDenied, AuthenticationFailed, NotFound, Timeout, Cancelled,
    TlsFailure, TransportFailure, ProtocolError, RedirectRejected, RetryLimit, PaginationLimit, SessionExpired, ServiceFailure
}

// Errors carry only allowlisted categories: no SDK exceptions, endpoint/key text or response bodies.
public sealed record StorageResult<T>(T? Value, StorageError Error)
{
    public bool IsSuccess => Error == StorageError.None;
    public static StorageResult<T> Success(T value) => new(value, StorageError.None);
    public static StorageResult<T> Failure(StorageError error) => new(default, error);
}
public sealed record BucketEntry(string Name, DateTimeOffset? Created);
public abstract record StorageEntry(string Key);
public sealed record ObjectEntry(string Key, long? Size, DateTimeOffset? Modified, string? ETag) : StorageEntry(Key);
public sealed record PrefixEntry(string Key) : StorageEntry(Key);
public sealed record ObjectMetadata(string Key, long? Size, DateTimeOffset? Modified, string? ETag,
    string? ContentType, string? VersionId, IReadOnlyDictionary<string, string> UserMetadata);
public sealed record StoragePage<T>(IReadOnlyList<T> Entries, bool IsComplete);

/// <summary>A single traversal. Tokens stay opaque and owned by the adapter, not the UI.</summary>
public interface IReadOnlyListing<T>
{
    Task<StorageResult<StoragePage<T>>> NextAsync(CancellationToken cancellationToken = default);
}
public interface IReadOnlyStorageSession : IAsyncDisposable
{
    ConnectionProfile Profile { get; }
    bool IsInvalidated { get; }
    IReadOnlyListing<BucketEntry> ListBuckets();
    IReadOnlyListing<StorageEntry> ListObjects(string bucket, string prefix = "");
    Task<StorageResult<ObjectMetadata>> GetMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default);
    void Invalidate();
}
public interface IReadOnlyStorageSessionFactory
{
    // Construction does not discover credentials or make network requests.
    IReadOnlyStorageSession Create(ConnectionProfile profile, Credentials credentials, Func<CancellationToken, Task<bool>> isCurrent);
}
public sealed record SessionOpenResult(IReadOnlyStorageSession? Session, StorageError Error, CredentialStatus? CredentialStatus = null);
