namespace BucketBus.Core;

// Intentionally not a record: generated ToString must never reveal credential values.
// Managed strings cannot be reliably zeroed. Keep lifetimes short and never log these objects.
public sealed class Credentials
{
    public Credentials(string accessKey, string secretKey, string? sessionToken = null)
    {
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey)
            || accessKey.Length > 256 || secretKey.Length > 2048 || sessionToken is { Length: > 16384 }
            || accessKey.Any(char.IsControl) || secretKey.Any(char.IsControl)
            || sessionToken?.Any(char.IsControl) == true)
            throw new ArgumentException("Invalid credential fields.");
        AccessKey = accessKey;
        SecretKey = secretKey;
        SessionToken = string.IsNullOrEmpty(sessionToken) ? null : sessionToken;
    }
    public string AccessKey { get; }
    public string SecretKey { get; }
    public string? SessionToken { get; }
    public override string ToString() => "[credentials redacted]";
}

public enum CredentialStatus { Found, Success, Missing, Locked, Unavailable, Denied, Cancelled, StorageFailure, RetrievalFailure }
public sealed class CredentialResult(CredentialStatus status, Credentials? value = null)
{
    public CredentialStatus Status { get; } = status;
    public Credentials? Value { get; } = value;
    public override string ToString() => Status.ToString();
}

public interface ICredentialStore
{
    Task<CredentialResult> ReadAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default);
    Task<CredentialStatus> WriteAsync(CredentialReference reference, Credentials credentials, bool allowUnlock, CancellationToken cancellationToken = default);
    Task<CredentialStatus> DeleteAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default);
}

public sealed class SessionCredentialStore : ICredentialStore, IDisposable
{
    private readonly Dictionary<string, Credentials> _values = [];
    private readonly object _gate = new();
    public Task<CredentialResult> ReadAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(cancellationToken.IsCancellationRequested
            ? new CredentialResult(CredentialStatus.Cancelled)
            : !reference.IsValid || reference.Storage != CredentialStorage.Session
                ? new CredentialResult(CredentialStatus.Denied)
                : _values.TryGetValue(reference.VaultKey, out var value)
                    ? new CredentialResult(CredentialStatus.Found, value) : new CredentialResult(CredentialStatus.Missing));
    }
    public Task<CredentialStatus> WriteAsync(CredentialReference reference, Credentials credentials, bool allowUnlock, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(CredentialStatus.Cancelled);
            if (!reference.IsValid || reference.Storage != CredentialStorage.Session) return Task.FromResult(CredentialStatus.Denied);
            _values[reference.VaultKey] = credentials;
            return Task.FromResult(CredentialStatus.Success);
        }
    }
    public Task<CredentialStatus> DeleteAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(CredentialStatus.Cancelled);
            if (!reference.IsValid || reference.Storage != CredentialStorage.Session) return Task.FromResult(CredentialStatus.Denied);
            return Task.FromResult(_values.Remove(reference.VaultKey) ? CredentialStatus.Success : CredentialStatus.Missing);
        }
    }
    public void Dispose() { lock (_gate) _values.Clear(); }
}
