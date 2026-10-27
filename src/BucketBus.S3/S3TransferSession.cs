using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using BucketBus.Core;

namespace BucketBus.S3;

internal sealed partial class S3TransferSession(AmazonS3Client client,
    TransferTransport transport, IDisposable owner, TimeSpan deadline) : IObjectTransferSession
{
    private int _used;
    public async Task<TransferResult> UploadAsync(string bucket, string key, ITransferSource source, IProgress<TransferProgress>? progress, CancellationToken token, Guid? operationId = null)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) return new(TransferState.Failed, TransferError.Busy);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(deadline);
        var operation = (operationId ?? Guid.NewGuid()).ToString("N");
        var length = source.Length;
        var acknowledged = false;
        try
        {
            var ct = timeout.Token; ct.ThrowIfCancellationRequested();
            if (length > 5L * 1024 * 1024 * 1024) return new(TransferState.Failed, TransferError.RemoteRejected);
            progress?.Report(new(0, length, "Checking source"));
            await source.ValidateAsync(ct).ConfigureAwait(false);
            transport.Begin(bucket, key, HttpMethod.Put);
            var request = new PutObjectRequest
            {
                BucketName = bucket, Key = key, InputStream = source.Content, IfNoneMatch = "*",
                AutoCloseStream = false, AutoResetStreamPosition = false, UseChunkEncoding = false,
                ChecksumSHA256 = source.Sha256, ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                MD5Digest = source.Md5, ContentType = "application/octet-stream"
            };
            request.Headers.ContentLength = length;
            request.Metadata.Add("bucketbus-operation", operation);
            request.StreamTransferProgress += (_, e) => progress?.Report(new(Math.Min(e.TransferredBytes, length), length, "Uploading"));
            var response = await client.PutObjectAsync(request, ct).ConfigureAwait(false);
            acknowledged = true;
            if (response.HttpStatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(response.ETag))
                return new(TransferState.OutcomeUnknown, TransferError.RemoteRejected);
            await source.ValidateAsync(ct).ConfigureAwait(false);
            progress?.Report(new(length, length, "Verifying remote content"));
            await VerifyUpload(bucket, key, operation, source.Sha256, length, ct).ConfigureAwait(false);
            return new(TransferState.Completed, Verification: "Remote content read back and SHA-256 matched; conditional PUT succeeded.");
        }
        catch (AmazonS3Exception e) when (!acknowledged && e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        { return new(TransferState.Failed, TransferError.Conflict); }
        catch (AmazonS3Exception e) when (!acknowledged && (int)e.StatusCode is 400 or 401 or 403 or 404 or 405 or 411 or 413 or 501)
        { return new(TransferState.Failed, RejectedUpload(e)); }
        catch (Exception e)
        {
            var error = Error(e, token, timeout.Token);
            if (!transport.PutSent) return new(token.IsCancellationRequested ? TransferState.Cancelled : TransferState.Failed, error);
            // Never replay PUT. A bounded read-back can establish authorship and exact bytes.
            // Cancellation/source mutation deliberately leave manual inspection instead of hiding uncertainty.
            if (!timeout.IsCancellationRequested && error != TransferError.SourceChanged)
            {
                using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await source.ValidateAsync(reconcile.Token).ConfigureAwait(false);
                    await VerifyUpload(bucket, key, operation, source.Sha256, length, reconcile.Token).ConfigureAwait(false);
                    return new(TransferState.Completed, Verification: "Reconciled by operation identity and full SHA-256 read-back; PUT was not replayed.");
                }
                catch { /* An absent/denied read is not proof that the PUT failed. */ }
            }
            return new(TransferState.OutcomeUnknown, error);
        }
    }
    public async Task<TransferResult> ReconcileUploadAsync(TransferRecord record, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) return new(TransferState.OutcomeUnknown, TransferError.Busy);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(deadline);
        try
        {
            record.Validate();
            if (!record.CanReconcile) return new(TransferState.OutcomeUnknown, TransferError.RecoveryUnavailable);
            await VerifyUpload(record.Bucket, record.Key, record.Id.ToString("N"), record.Sha256!, record.Total!.Value, timeout.Token).ConfigureAwait(false);
            return new(TransferState.Completed, Verification: "Reconciled by saved operation identity and full SHA-256 read-back. No PUT was sent.");
        }
        catch (Exception e) { return new(TransferState.OutcomeUnknown, Error(e, token, timeout.Token)); }
    }
    private static TransferError RejectedUpload(AmazonS3Exception error)
    {
        // Classify only known rejection codes/statuses. Never display provider messages,
        // guess which header InvalidRequest refers to, or retry without a precondition.
        if (error.StatusCode == HttpStatusCode.Unauthorized
            || error.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "ExpiredToken" or "InvalidToken")
            return TransferError.AuthenticationFailed;
        if (error.StatusCode == HttpStatusCode.Forbidden) return TransferError.PermissionDenied;
        if (error.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed
            || error.ErrorCode == "NotImplemented")
            return TransferError.ConditionalWriteUnsupported;
        return TransferError.RemoteRejected;
    }
    private async Task VerifyUpload(string bucket, string key, string operation, string expectedHash, long length, CancellationToken token)
    {
        transport.Begin(bucket, key, HttpMethod.Get);
        using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED }, token).ConfigureAwait(false);
        if (response.Metadata["x-amz-meta-bucketbus-operation"] != operation || transport.Length != length)
            throw new TransferException(TransferError.IntegrityMismatch);
        var result = await CopyVerified(response, Stream.Null, null, token).ConfigureAwait(false);
        if (result.Hash != expectedHash) throw new TransferException(TransferError.IntegrityMismatch);
    }
    public async Task<TransferResult> DownloadAsync(string bucket, string key, Stream destination, IProgress<TransferProgress>? progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) return new(TransferState.Failed, TransferError.Busy);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(deadline);
        try
        {
            timeout.Token.ThrowIfCancellationRequested(); transport.Begin(bucket, key, HttpMethod.Get);
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED }, timeout.Token).ConfigureAwait(false);
            var result = await CopyVerified(response, destination, progress, timeout.Token).ConfigureAwait(false);
            return new(TransferState.Completed, Verification: result.Verification);
        }
        catch (Exception e) { return new(token.IsCancellationRequested ? TransferState.Cancelled : TransferState.Failed, DownloadError(e, token, timeout.Token)); }
    }
    private async Task<(string Hash, string Verification)> CopyVerified(GetObjectResponse response, Stream destination, IProgress<TransferProgress>? progress, CancellationToken token)
    {
        if (response.HttpStatusCode != HttpStatusCode.OK || transport.Length is not { } length || length < 0)
            throw new TransferException(TransferError.IntegrityMismatch);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[65536]; long total = 0; int count;
        progress?.Report(new(0, length, "Downloading"));
        while ((count = await ReadChunk().ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > length) throw new TransferException(TransferError.IntegrityMismatch);
            sha.AppendData(buffer, 0, count); sha1.AppendData(buffer, 0, count);
            try { await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) { throw new TransferException(TransferError.PermissionDenied); }
            catch (IOException e) { throw new TransferException((e.HResult & 0xffff) is 28 or 112 ? TransferError.DiskFull : TransferError.LocalFailure); }
            progress?.Report(new(total, length, "Downloading"));
        }
        token.ThrowIfCancellationRequested();
        if (total != length) throw new TransferException(TransferError.TruncatedResponse);
        var hash = Convert.ToBase64String(sha.GetHashAndReset());
        var verification = "Length verified; no compatible full-object checksum supplied (content equality not established).";
        if (response.ChecksumSHA256 is { Length: > 0 } expected && !expected.Contains('-'))
        { if (expected != hash) throw new TransferException(TransferError.IntegrityMismatch); verification = "Length and server full-object SHA-256 verified."; }
        if (response.ChecksumSHA1 is { Length: > 0 } expected1 && !expected1.Contains('-'))
        { if (expected1 != Convert.ToBase64String(sha1.GetHashAndReset())) throw new TransferException(TransferError.IntegrityMismatch); verification += " Server SHA-1 verified."; }
        if (response.ResponseMetadata?.ChecksumValidationStatus == Amazon.Runtime.ChecksumValidationStatus.SUCCESSFUL)
            verification += " SDK response checksum verified.";
        return (hash, verification);
        async Task<int> ReadChunk()
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            try { return await response.ResponseStream.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
            catch (Exception) when (response.ResponseMetadata?.ChecksumValidationStatus == Amazon.Runtime.ChecksumValidationStatus.INVALID)
            { throw new TransferException(TransferError.IntegrityMismatch); }
            catch (IOException e)
            {
                if (idle.IsCancellationRequested) throw new OperationCanceledException(idle.Token);
                throw new TransferException(DownloadError(e, default, default, remoteRead: true));
            }
        }
    }
    private static TransferError DownloadError(Exception error, CancellationToken user, CancellationToken deadline, bool remoteRead = false)
    {
        var chain = new List<Exception>();
        for (var current = error; current is not null && chain.Count < 16; current = current.InnerException) chain.Add(current);
        if (chain.Count == 16 && chain[^1].InnerException is not null) return TransferError.NetworkFailure;
        if (chain.OfType<TransferException>().FirstOrDefault() is { } failure) return failure.Error;
        if (chain.Any(e => e is AuthenticationException || e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } or HttpIOException { HttpRequestError: HttpRequestError.SecureConnectionError })) return TransferError.TlsFailure;
        var service = chain.OfType<AmazonS3Exception>().FirstOrDefault();
        if (service?.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "ExpiredToken" or "InvalidToken") return TransferError.AuthenticationFailed;
        var status = service is not null && (int)service.StatusCode != 0 ? service.StatusCode : chain.OfType<HttpRequestException>().FirstOrDefault()?.StatusCode;
        if (status is not null)
            return (int)status.Value switch
            {
                401 => TransferError.AuthenticationFailed, 403 => TransferError.PermissionDenied, 404 => TransferError.ObjectMissing,
                429 => TransferError.RateLimited, 500 or 502 or 503 or 504 => TransferError.TransientService, _ => TransferError.RemoteRejected
            };
        if (chain.Any(e => e is OperationCanceledException) || deadline.IsCancellationRequested)
            return user.IsCancellationRequested ? TransferError.None : TransferError.Timeout;
        foreach (var item in chain)
        {
            var kind = item switch { HttpRequestException request => request.HttpRequestError, HttpIOException io => io.HttpRequestError, _ => HttpRequestError.Unknown };
            if (kind == HttpRequestError.ResponseEnded) return TransferError.TruncatedResponse;
            if (kind is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError) return TransferError.ConnectionInterrupted;
            if (item is HttpIOException) return TransferError.NetworkFailure; // Unknown/protocol failures are not assumed transient.
            if (item is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset
                or SocketError.ConnectionRefused or SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable
                or SocketError.HostNotFound or SocketError.TryAgain or SocketError.TimedOut) return TransferError.ConnectionInterrupted;
        }
        // Only a read from the remote response stream gives a plain IOException transport meaning.
        return remoteRead && error is IOException ? TransferError.ConnectionInterrupted : TransferError.NetworkFailure;
    }
    private static TransferError Error(Exception e, CancellationToken user, CancellationToken deadline) => e switch
    {
        TransferException failure => failure.Error,
        OperationCanceledException => user.IsCancellationRequested ? TransferError.None : TransferError.Timeout,
        AmazonS3Exception s when s.StatusCode == HttpStatusCode.Forbidden => TransferError.PermissionDenied,
        AmazonS3Exception s when s.StatusCode == HttpStatusCode.NotFound => TransferError.RemoteRejected,
        _ => deadline.IsCancellationRequested ? TransferError.Timeout : TransferError.NetworkFailure
    };
    public ValueTask DisposeAsync()
    {
        try { client.Dispose(); } finally { owner.Dispose(); }
        return ValueTask.CompletedTask;
    }
}
