using System.Net;
using System.Text;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;
using BucketBus.S3;

namespace BucketBus.Desktop.Tests;

internal sealed class BrowserFixture : IDisposable
{
    public MemoryProfiles Profiles { get; } = new();
    public BrowserVault Vault { get; } = new();
    public SessionCredentialStore MemoryCredentials { get; } = new();
    public ProfileManager Manager { get; }
    public ReadOnlySessionController Controller { get; }
    public RemoteBrowserViewModel Browser { get; }
    public BrowserFactory Factory { get; } = new();
    public BrowserFixture(string? bucket = "test-bucket", string? prefix = null)
    {
        var settings = new ProfileSettings("Synthetic profile", "https://storage.example.test", "us-east-1", AddressingStyle.Path, bucket, prefix);
        var id = Guid.NewGuid();
        Profiles.Snapshot = new(1, [new(id, 1, settings, new(id, Guid.NewGuid(), CredentialStorage.Secure, settings.TargetFingerprint()))]);
        Manager = new(Profiles, Vault, MemoryCredentials);
        Controller = new(Manager, Factory);
        Browser = new(Manager, Controller);
    }
    public async Task SelectAsync() { await Browser.ReloadProfilesAsync(); Browser.SelectedProfile = Browser.ProfilesList[0]; }
    public async Task ConnectAsync() { await SelectAsync(); await Browser.ConnectCommand.ExecuteAsync(null); }
    public void UseSdk(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response, TimeSpan? deadline = null)
    {
        Factory.CreateSession = (p, c, current) => S3ReadOnlySessionFactory.Create(p, c, current, new BrowserTransport(response), deadline ?? TimeSpan.FromSeconds(30));
    }
    public void Dispose() { Browser.Dispose(); Controller.Dispose(); MemoryCredentials.Dispose(); }
    public static HttpResponseMessage Xml(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
    public static string Objects(string content = "", bool more = false, string? token = null) =>
        "<ListBucketResult><Name>test-bucket</Name><EncodingType>url</EncodingType><IsTruncated>" + more.ToString().ToLowerInvariant()
        + "</IsTruncated>" + content + (token is null ? "" : "<NextContinuationToken>" + token + "</NextContinuationToken>") + "</ListBucketResult>";
    public static string Item(string key, string fields = "") => "<Contents><Key>" + Uri.EscapeDataString(key) + "</Key>" + fields + "</Contents>";
    public static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
}
internal sealed class MemoryProfiles : IProfileStore
{
    public ProfileSnapshot Snapshot = new(0, []);
    public Task<ProfileReadResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProfileReadResult(ProfileStatus.Success, Snapshot));
    public Task<ProfileStatus> WriteAsync(ProfileSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    { Snapshot = snapshot; return Task.FromResult(ProfileStatus.Success); }
}
internal sealed class BrowserVault : ICredentialStore
{
    public Func<CancellationToken, Task<CredentialResult>> Read = _ => Task.FromResult(new CredentialResult(CredentialStatus.Found, new("synthetic-key", "synthetic-secret")));
    public bool AllowedUnlock;
    public Task<CredentialResult> ReadAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default)
    { AllowedUnlock = allowUnlock; return Read(cancellationToken); }
    public Task<CredentialStatus> WriteAsync(CredentialReference reference, Credentials credentials, bool allowUnlock, CancellationToken cancellationToken = default) => Task.FromResult(CredentialStatus.Success);
    public Task<CredentialStatus> DeleteAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default) => Task.FromResult(CredentialStatus.Success);
}
internal sealed class BrowserFactory : IReadOnlyStorageSessionFactory
{
    public int Created;
    public Func<ConnectionProfile, Credentials, Func<CancellationToken, Task<bool>>, IReadOnlyStorageSession> CreateSession = (p, _, _) => new BrowserSession(p);
    public IReadOnlyStorageSession Create(ConnectionProfile profile, Credentials credentials, Func<CancellationToken, Task<bool>> isCurrent)
    { Interlocked.Increment(ref Created); return CreateSession(profile, credentials, isCurrent); }
}
internal sealed class BrowserSession(ConnectionProfile profile) : IReadOnlyStorageSession
{
    public ConnectionProfile Profile => profile;
    public bool IsInvalidated { get; private set; }
    public bool Disposed;
    public Func<string, string, IReadOnlyListing<StorageEntry>> Objects = (_, _) => new BrowserListing<StorageEntry>(_ => Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new([], true))));
    public Func<string, string, CancellationToken, Task<StorageResult<ObjectMetadata>>> Metadata = (_, _, _) => Task.FromResult(StorageResult<ObjectMetadata>.Failure(StorageError.NotFound));
    public IReadOnlyListing<BucketEntry> ListBuckets() => new BrowserListing<BucketEntry>(_ => Task.FromResult(StorageResult<StoragePage<BucketEntry>>.Success(new([new("test-bucket", null)], true))));
    public IReadOnlyListing<StorageEntry> ListObjects(string bucket, string prefix = "") => Objects(bucket, prefix);
    public Task<StorageResult<ObjectMetadata>> GetMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default) => Metadata(bucket, key, cancellationToken);
    public void Invalidate() => IsInvalidated = true;
    public ValueTask DisposeAsync() { Invalidate(); Disposed = true; return ValueTask.CompletedTask; }
}
internal sealed class BrowserListing<T>(Func<CancellationToken, Task<StorageResult<StoragePage<T>>>> next) : IReadOnlyListing<T>
{
    public Task<StorageResult<StoragePage<T>>> NextAsync(CancellationToken cancellationToken = default) => next(cancellationToken);
}
internal sealed class BrowserTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
}
