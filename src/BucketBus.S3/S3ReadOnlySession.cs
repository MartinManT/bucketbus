using System.Globalization;
using System.Collections.ObjectModel;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BucketBus.Core;

namespace BucketBus.S3;

internal sealed class S3ReadOnlySession(ConnectionProfile profile, AmazonS3Client client, IDisposable transportOwner,
    ReadTransport transport, Func<CancellationToken, Task<bool>> isCurrent, TimeSpan deadline, int pageLimit) : IReadOnlyStorageSession
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _disposalGate = new();
    private readonly int _pageLimit = pageLimit;
    private Task? _disposal;
    private int _invalidated;
    public ConnectionProfile Profile { get; } = profile;
    public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;

    public IReadOnlyListing<BucketEntry> ListBuckets() => new Listing<BucketEntry>(this, null, "ListAllMyBucketsResult", async (token, ct) =>
    {
        var response = await client.ListBucketsAsync(new ListBucketsRequest { MaxBuckets = 1000, ContinuationToken = token }, ct).ConfigureAwait(false);
        var buckets = response.Buckets ?? [];
        if (buckets.Count > 1000 || buckets.Any(b => b is null || string.IsNullOrWhiteSpace(b.BucketName))
            || buckets.Select(b => b.BucketName).Distinct(StringComparer.Ordinal).Count() != buckets.Count)
            throw new ReadFailure(StorageError.ProtocolError);
        return (buckets.Select(b => new BucketEntry(b.BucketName, Date(b.CreationDate))).ToArray(), response.ContinuationToken);
    });

    public IReadOnlyListing<StorageEntry> ListObjects(string bucket, string prefix = "") =>
        new Listing<StorageEntry>(this, bucket, "ListBucketResult", async (token, ct) =>
        {
            ValidateBucket(bucket); ValidateKey(prefix, allowEmpty: true);
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket, Prefix = prefix, Delimiter = "/", MaxKeys = 1000,
                Encoding = EncodingType.Url, ContinuationToken = token
            }, ct).ConfigureAwait(false);
            if (response.Name != bucket || response.IsTruncated is null
                || response.Encoding is not null && response.Encoding != EncodingType.Url
                || response.ContinuationToken is not null && response.ContinuationToken != token
                || response.Delimiter is not null && Decode(response.Delimiter, response.Encoding) != "/"
                || response.Prefix is not null && Decode(response.Prefix, response.Encoding) != prefix)
                throw new ReadFailure(StorageError.ProtocolError);
            var objects = response.S3Objects ?? [];
            var prefixes = response.CommonPrefixes ?? [];
            if (objects.Count + prefixes.Count > 1000 || response.KeyCount is < 0 or > 1000)
                throw new ReadFailure(StorageError.ProtocolError);
            var entries = new List<StorageEntry>(objects.Count + prefixes.Count);
            var objectKeys = new HashSet<string>(StringComparer.Ordinal);
            var prefixKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in objects)
            {
                if (item is null || item.Size < 0) throw new ReadFailure(StorageError.ProtocolError);
                var key = Decode(item.Key, response.Encoding);
                ValidateReturnedKey(key, prefix);
                if (!objectKeys.Add(key)) throw new ReadFailure(StorageError.ProtocolError);
                entries.Add(new ObjectEntry(key, item.Size, Date(item.LastModified), item.ETag));
            }
            foreach (var item in prefixes)
            {
                var key = Decode(item, response.Encoding);
                ValidateReturnedKey(key, prefix);
                if (!key.EndsWith('/') || !prefixKeys.Add(key)) throw new ReadFailure(StorageError.ProtocolError);
                entries.Add(new PrefixEntry(key));
            }
            var next = response.NextContinuationToken;
            if (response.IsTruncated == true && string.IsNullOrEmpty(next)
                || response.IsTruncated == false && !string.IsNullOrEmpty(next))
                throw new ReadFailure(StorageError.ProtocolError);
            return (entries.ToArray(), next);
        });

    public Task<StorageResult<ObjectMetadata>> GetMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default) =>
        RunAsync(bucket, null, async ct =>
        {
            ValidateBucket(bucket); ValidateKey(key, false);
            var response = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, ct).ConfigureAwait(false);
            var metadata = response.Metadata.Keys.ToDictionary(k => k, k => response.Metadata[k], StringComparer.OrdinalIgnoreCase);
            if (metadata.Count > 128 || metadata.Sum(pair => (long)pair.Key.Length + pair.Value.Length) > 65536)
                throw new ReadFailure(StorageError.ProtocolError);
            return new ObjectMetadata(key, transport.HeadLength, Date(response.LastModified), response.ETag,
                response.ContentType, response.VersionId, new ReadOnlyDictionary<string, string>(metadata));
        }, cancellationToken, expectedKey: key);

    private async Task<StorageResult<T>> RunAsync<T>(string? bucket, string? xmlRoot, Func<CancellationToken, Task<T>> action,
        CancellationToken token, Action<T>? commit = null, string? expectedKey = null)
    {
        if (IsInvalidated) return StorageResult<T>.Failure(StorageError.SessionExpired);
        // The deadline covers queueing, authority verification, SDK attempts/backoff and body parsing.
        using var timeout = new CancellationTokenSource(deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token, _lifetime.Token);
        var entered = false;
        try
        {
            await _operation.WaitAsync(linked.Token).ConfigureAwait(false); entered = true;
            if (IsInvalidated || !await isCurrent(linked.Token).ConfigureAwait(false))
            { Invalidate(); return StorageResult<T>.Failure(StorageError.SessionExpired); }
            transport.Begin(bucket, xmlRoot, expectedKey);
            var value = await action(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (!await isCurrent(linked.Token).ConfigureAwait(false))
            { Invalidate(); return StorageResult<T>.Failure(StorageError.SessionExpired); }
            linked.Token.ThrowIfCancellationRequested();
            commit?.Invoke(value);
            return StorageResult<T>.Success(value);
        }
        catch (Exception error)
        {
            var category = IsInvalidated ? StorageError.SessionExpired : token.IsCancellationRequested ? StorageError.Cancelled
                : timeout.IsCancellationRequested ? StorageError.Timeout : Classify(error);
            return StorageResult<T>.Failure(category);
        }
        finally { if (entered) _operation.Release(); }
    }
    private static StorageError Classify(Exception error)
    {
        var transportFailure = false;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is ReadFailure failure) return failure.Error;
            if (current is AuthenticationException || current is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return StorageError.TlsFailure;
            if (current is XmlException or FormatException or OverflowException or DecoderFallbackException) return StorageError.ProtocolError;
            transportFailure |= current is HttpRequestException or IOException or System.Net.Sockets.SocketException;
        }
        if (transportFailure) return StorageError.TransportFailure;
        if (error is AmazonS3Exception s3)
        {
            if (s3.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "ExpiredToken" or "InvalidToken" or "TokenRefreshRequired" or "AuthorizationHeaderMalformed")
                return StorageError.AuthenticationFailed;
            return s3.StatusCode switch
            {
                HttpStatusCode.Forbidden => StorageError.AccessDenied,
                HttpStatusCode.Unauthorized => StorageError.AuthenticationFailed,
                HttpStatusCode.NotFound => StorageError.NotFound,
                HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => StorageError.Timeout,
                _ => StorageError.ServiceFailure
            };
        }
        return error switch
        {
            ArgumentException => StorageError.InvalidRequest,
            OperationCanceledException => StorageError.Cancelled,
            HttpRequestException or IOException => StorageError.TransportFailure,
            AmazonClientException => StorageError.ProtocolError,
            _ => StorageError.ServiceFailure
        };
    }
    private void ValidateBucket(string bucket)
    {
        if (string.IsNullOrEmpty(bucket) || bucket.Length > 255
            || bucket.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_')))
            throw new ReadFailure(StorageError.InvalidRequest); // Excludes ARNs/access points and authority injection.
        if (Profile.Settings.Addressing == AddressingStyle.VirtualHosted
            && (!Regex.IsMatch(bucket, "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)
                || new Uri(Profile.Settings.Endpoint).HostNameType != UriHostNameType.Dns))
            throw new ReadFailure(StorageError.InvalidRequest); // Do not silently switch dotted/IP buckets to path style.
    }
    private static void ValidateKey(string key, bool allowEmpty)
    {
        if (key is null || !allowEmpty && key.Length == 0 || new UTF8Encoding(false, true).GetByteCount(key) > 1024)
            throw new ReadFailure(StorageError.InvalidRequest);
    }
    private static void ValidateReturnedKey(string key, string prefix)
    {
        try { ValidateKey(key, false); }
        catch (Exception) { throw new ReadFailure(StorageError.ProtocolError); }
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new ReadFailure(StorageError.ProtocolError);
    }
    private static DateTimeOffset? Date(DateTime? value) => value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    private static string Decode(string? value, EncodingType? encoding)
    {
        if (value is null) throw new ReadFailure(StorageError.ProtocolError);
        if (encoding != EncodingType.Url) return value;
        var utf8 = new UTF8Encoding(false, true);
        var raw = utf8.GetBytes(value);
        using var decoded = new MemoryStream(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '%') { decoded.WriteByte(raw[i]); continue; }
            if (i + 2 >= raw.Length || !byte.TryParse(Encoding.ASCII.GetString(raw, i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                throw new ReadFailure(StorageError.ProtocolError);
            decoded.WriteByte(b); i += 2;
        }
        return utf8.GetString(decoded.ToArray()); // Exactly once; '+' is not form-urlencoded space.
    }
    public void Invalidate()
    {
        if (Interlocked.Exchange(ref _invalidated, 1) == 0) _lifetime.Cancel();
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposalGate) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Invalidate();
        await _operation.WaitAsync().ConfigureAwait(false);
        try { client.Dispose(); transportOwner.Dispose(); }
        finally { _operation.Release(); }
        // Keep the cancellation source usable for a concurrent caller that observed the
        // old state just before invalidation. It owns no timer or native wait handle.
    }

    private sealed class Listing<T>(S3ReadOnlySession session, string? bucket, string xmlRoot,
        Func<string?, CancellationToken, Task<(T[] Entries, string? Next)>> fetch) : IReadOnlyListing<T>
    {
        private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
        private string? _next;
        private bool _complete;
        private int _pages;
        private StorageError? _terminalError;
        public Task<StorageResult<StoragePage<T>>> NextAsync(CancellationToken cancellationToken = default)
        {
            string? nextToken = null, tokenHash = null;
            var advance = false;
            return session.RunAsync(bucket, xmlRoot, async ct =>
            {
                if (_terminalError is { } error) throw new ReadFailure(error);
                if (_complete) return new StoragePage<T>(Array.Empty<T>(), true);
                if (_pages >= session._pageLimit) { _terminalError = StorageError.PaginationLimit; throw new ReadFailure(_terminalError.Value); }
                var result = await fetch(_next, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(result.Next))
                {
                    var next = result.Next;
                    if (next.Length > 16384 || string.IsNullOrWhiteSpace(next) || next.Any(char.IsControl))
                        throw new ReadFailure(StorageError.ProtocolError);
                    tokenHash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(next)));
                    if (_tokens.Contains(tokenHash)) { _terminalError = StorageError.ProtocolError; throw new ReadFailure(_terminalError.Value); }
                }
                nextToken = string.IsNullOrEmpty(result.Next) ? null : result.Next;
                advance = true;
                return new StoragePage<T>(Array.AsReadOnly(result.Entries), nextToken is null);
            }, cancellationToken, _ =>
            {
                if (!advance) return;
                if (tokenHash is not null) _tokens.Add(tokenHash);
                _next = nextToken; _complete = _next is null; _pages++;
            });
        }
    }
}
