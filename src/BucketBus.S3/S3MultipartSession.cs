using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using BucketBus.Core;
using MultipartUpload = BucketBus.Core.MultipartUpload;

namespace BucketBus.S3;

internal sealed partial class S3TransferSession
{
    public async Task<TransferResult> UploadMultipartAsync(TransferRecord record, ITransferSource source,
        Func<MultipartUpload, Task> persist, IProgress<TransferProgress>? progress, CancellationToken token, bool resume = false)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) return new(TransferState.Failed, TransferError.Busy);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(deadline);
        var ct = timeout.Token; var state = record.Multipart; var completionSent = false;
        try
        {
            record.Validate();
            if (state is null || record.Total != source.Length || record.Sha256 != source.Sha256
                || record.SourceIdentity != source.SourceIdentity || !source.Content.CanSeek) throw new TransferException(TransferError.SourceChanged);
            await source.ValidateIdentityAsync(ct).ConfigureAwait(false);
            if (resume)
            {
                if (!state.CanResume) throw new TransferException(TransferError.RecoveryUnavailable);
                await CheckParts(record, state, ct).ConfigureAwait(false);
            }
            else
            {
                if (state.Phase != MultipartPhase.Creating || state.UploadId is not null) throw new TransferException(TransferError.RecoveryUnavailable);
                // Creating was durably saved before this request. A lost ID is never recreated automatically.
                transport.BeginMultipart(record.Bucket, record.Key, HttpMethod.Post);
                var create = new InitiateMultipartUploadRequest { BucketName = record.Bucket, Key = record.Key,
                    ContentType = "application/octet-stream", ChecksumAlgorithm = ChecksumAlgorithm.SHA256 };
                create.Metadata.Add("bucketbus-operation", record.Id.ToString("N"));
                var initiated = await client.InitiateMultipartUploadAsync(create, ct).ConfigureAwait(false);
                if (initiated.HttpStatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(initiated.UploadId)
                    || initiated.BucketName != record.Bucket || initiated.Key != record.Key) throw new TransferException(TransferError.RemoteRejected);
                state = state with { UploadId = initiated.UploadId, Phase = MultipartPhase.Uploading };
                state.Validate(source.Length); await persist(state).ConfigureAwait(false);
            }
            for (var number = state.Confirmed.Length + 1; number <= state.Plan.PartCount; number++)
            {
                ct.ThrowIfCancellationRequested(); await source.ValidateIdentityAsync(ct).ConfigureAwait(false);
                var size = state.Plan.Length(number, source.Length);
                using var part = new PartStream(source.Content, state.Plan.Offset(number), size);
                var hash = Convert.ToBase64String(await SHA256.HashDataAsync(part, ct).ConfigureAwait(false));
                if (part.Position != size) throw new TransferException(TransferError.SourceChanged);
                part.Position = 0;
                state = state with { Phase = MultipartPhase.SendingPart, PendingPart = number };
                await persist(state).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
                transport.BeginMultipart(record.Bucket, record.Key, HttpMethod.Put, state.UploadId, number);
                var acknowledged = state.AcknowledgedBytes;
                progress?.Report(new(acknowledged, source.Length, $"Uploading part {number}/{state.Plan.PartCount} — bytes shown are acknowledged"));
                var uploaded = await client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = record.Bucket, Key = record.Key, UploadId = state.UploadId, PartNumber = number,
                    PartSize = size, InputStream = part, IsLastPart = number == state.Plan.PartCount,
                    UseChunkEncoding = false, ChecksumSHA256 = hash, ChecksumAlgorithm = ChecksumAlgorithm.SHA256
                }, ct).ConfigureAwait(false);
                if (uploaded.HttpStatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(uploaded.ETag)
                    || uploaded.ChecksumSHA256 is { Length: > 0 } checksum && checksum != hash) throw new TransferException(TransferError.IntegrityMismatch);
                await source.ValidateIdentityAsync(ct).ConfigureAwait(false);
                state = state with { Phase = MultipartPhase.Uploading, PendingPart = 0,
                    Parts = [..state.Confirmed, new(number, size, uploaded.ETag, hash)] };
                state.Validate(source.Length); await persist(state).ConfigureAwait(false);
                progress?.Report(new(state.AcknowledgedBytes, source.Length, $"Acknowledged part {number}/{state.Plan.PartCount}; completion pending"));
            }
            progress?.Report(new(source.Length, source.Length, "Checking source before completion"));
            await source.ValidateAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            // Persist completion intent before the remote commit point; no automatic replay, even on HTTP 200 errors.
            state = state with { Phase = MultipartPhase.Completing }; await persist(state).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(source.Length, source.Length, "Completing multipart upload"));
            transport.BeginMultipart(record.Bucket, record.Key, HttpMethod.Post, state.UploadId, completing: true);
            completionSent = true;
            var complete = await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = record.Bucket, Key = record.Key, UploadId = state.UploadId, IfNoneMatch = "*",
                PartETags = state.Confirmed.Select(p => new PartETag(p.Number, p.ETag) { ChecksumSHA256 = p.Sha256 }).ToList()
            }, ct).ConfigureAwait(false);
            if (complete.HttpStatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(complete.ETag)
                || complete.BucketName != record.Bucket || complete.Key != record.Key) throw new TransferException(TransferError.RemoteRejected);
            state = state with { Phase = MultipartPhase.Verifying }; await persist(state).ConfigureAwait(false);
            progress?.Report(new(source.Length, source.Length, "Verifying full remote content"));
            await VerifyUpload(record.Bucket, record.Key, record.Id.ToString("N"), record.Sha256!, source.Length, ct).ConfigureAwait(false);
            state = state with { Phase = MultipartPhase.Verified }; await persist(state).ConfigureAwait(false);
            return new(TransferState.Completed, Verification: "Conditional multipart completion acknowledged; operation identity and full remote SHA-256 verified.");
        }
        catch (JournalException) { return new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable, CleanupRequired: true); }
        catch (AmazonS3Exception e) when (completionSent && state?.Phase == MultipartPhase.Completing
            && ((int)e.StatusCode is 400 or 401 or 403 or 404 or 405 or 409 or 412 or 501))
        {
            // A definite service rejection does not authorize replay. Preserve owned upload for explicit abort.
            try { await persist(state with { Phase = MultipartPhase.CompletionRejected }).ConfigureAwait(false); }
            catch { return new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable, CleanupRequired: true); }
            return new(TransferState.Failed, e.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
                ? TransferError.Conflict : RejectedUpload(e), CleanupRequired: true);
        }
        catch (Exception e)
        {
            return new(state?.CompletionUncertain == true ? TransferState.OutcomeUnknown : TransferState.Interrupted,
                Error(e, token, timeout.Token), CleanupRequired: true);
        }
    }

    // Bounded pagination. Only exact agreement allows continuation. An unacknowledged part is not reused.
    private async Task CheckParts(TransferRecord record, MultipartUpload state, CancellationToken token)
    {
        var marker = 0; var seen = 0;
        do
        {
            transport.BeginMultipart(record.Bucket, record.Key, HttpMethod.Get, state.UploadId, marker: marker);
            var response = await client.ListPartsAsync(new ListPartsRequest { BucketName = record.Bucket, Key = record.Key,
                UploadId = state.UploadId, PartNumberMarker = marker.ToString(System.Globalization.CultureInfo.InvariantCulture), MaxParts = 1000 }, token).ConfigureAwait(false);
            if (response.HttpStatusCode != HttpStatusCode.OK || response.UploadId != state.UploadId
                || response.BucketName != record.Bucket || response.Key != record.Key || response.IsTruncated is null)
                throw new TransferException(TransferError.RecoveryUnavailable);
            var parts = response.Parts ?? [];
            if (parts.Count > 1000) throw new TransferException(TransferError.RecoveryUnavailable);
            foreach (var remote in parts)
            {
                if (seen >= state.Confirmed.Length) throw new TransferException(TransferError.RecoveryUnavailable);
                var local = state.Confirmed[seen++];
                if (remote.PartNumber != local.Number || remote.Size != local.Size || remote.ETag != local.ETag
                    || remote.ChecksumSHA256 is { Length: > 0 } sha && sha != local.Sha256)
                    throw new TransferException(TransferError.IntegrityMismatch);
            }
            if (response.IsTruncated != true) break;
            if (parts.Count == 0 || response.NextPartNumberMarker is not { } next || next <= marker || next != seen || next >= 10000)
                throw new TransferException(TransferError.RecoveryUnavailable);
            marker = next;
        } while (true);
        if (seen != state.Confirmed.Length) throw new TransferException(TransferError.RecoveryUnavailable);
    }

    public async Task<TransferResult> AbortMultipartAsync(TransferRecord record, Func<MultipartUpload, Task> persist, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) return new(TransferState.Interrupted, TransferError.Busy, CleanupRequired: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(deadline);
        var verifyingAbsence = false;
        var verifyingListing = false;
        try
        {
            record.Validate();
            if (record.Multipart is not { CanAbort: true } state) throw new TransferException(TransferError.RecoveryUnavailable);
            state = state with { Phase = MultipartPhase.Aborting }; await persist(state).ConfigureAwait(false);
            transport.BeginMultipart(record.Bucket, record.Key, HttpMethod.Delete, state.UploadId);
            try
            {
                var response = await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    { BucketName = record.Bucket, Key = record.Key, UploadId = state.UploadId }, timeout.Token).ConfigureAwait(false);
                if (response.HttpStatusCode != HttpStatusCode.NoContent) throw new TransferException(TransferError.CleanupRequired);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound && e.ErrorCode == "NoSuchUpload")
            { /* Prior explicit abort or provider expiry: still require the read-only absence check below. */ }
            // Acknowledgement alone may leave in-flight parts. Verify absence; never delete a final object.
            verifyingAbsence = true;
            transport.BeginMultipart(record.Bucket, record.Key, HttpMethod.Get, state.UploadId, marker: 0);
            try
            {
                await client.ListPartsAsync(new ListPartsRequest { BucketName = record.Bucket, Key = record.Key,
                    UploadId = state.UploadId, PartNumberMarker = "0", MaxParts = 1000 }, timeout.Token).ConfigureAwait(false);
                return new(TransferState.Interrupted, TransferError.CleanupRequired, CleanupRequired: true);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound && e.ErrorCode == "NoSuchUpload") { }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound && e.ErrorCode == "NoSuchKey")
            {
                // Squid can report missing multipart metadata as NoSuchKey. It is not
                // absence proof by itself: check a complete, exact-prefix upload listing.
                verifyingListing = true;
                if (!await MultipartUploadAbsentAsync(record, timeout.Token).ConfigureAwait(false))
                    return new(TransferState.Interrupted, TransferError.CleanupRequired,
                        Verification: " Recorded upload is still listed; cleanup remains unresolved.", CleanupRequired: true);
            }
            await persist(state with { Phase = MultipartPhase.Aborted }).ConfigureAwait(false);
            return new(TransferState.Cancelled, Verification: "Owned multipart upload aborted and absence confirmed. No object deletion requested.");
        }
        catch (JournalException) { return new(TransferState.RequiresReconciliation, TransferError.JournalUnavailable, CleanupRequired: true); }
        catch (Exception e)
        {
            var detail = transport.FailureReason;
            if (detail is null && e is AmazonS3Exception service)
            {
                var code = service.ErrorCode switch
                {
                    "NoSuchBucket" => "NoSuchBucket", "NoSuchKey" => "NoSuchKey", "NoSuchUpload" => "NoSuchUpload",
                    "AccessDenied" => "AccessDenied", "InvalidRequest" => "InvalidRequest",
                    "InvalidAccessKeyId" => "InvalidAccessKeyId", "SignatureDoesNotMatch" => "SignatureDoesNotMatch",
                    "ExpiredToken" => "ExpiredToken", _ => "unclassified provider error"
                };
                detail = $"Provider returned HTTP {(int)service.StatusCode} ({code}).";
                if (verifyingListing && service.StatusCode == HttpStatusCode.Forbidden)
                    detail += " Multipart cleanup verification requires s3:ListBucketMultipartUploads permission.";
            }
            var diagnostic = detail is null ? "" : " " + (verifyingListing ? "Cleanup verification (ListMultipartUploads): " : verifyingAbsence ? "Cleanup verification: " : "Abort request: ") + detail;
            return new(TransferState.Interrupted, Error(e, token, timeout.Token), Verification: diagnostic, CleanupRequired: true);
        }
    }
    private async Task<bool> MultipartUploadAbsentAsync(TransferRecord record, CancellationToken token)
    {
        using var verification = CancellationTokenSource.CreateLinkedTokenSource(token);
        verification.CancelAfter(TimeSpan.FromSeconds(30));
        string? keyMarker = null, uploadMarker = null;
        var seen = new HashSet<(string, string)>();
        var markers = new HashSet<(string, string)>();
        for (var page = 0; page < 16; page++)
        {
            transport.BeginMultipartListing(record.Bucket, record.Key, keyMarker, uploadMarker);
            var response = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
            {
                BucketName = record.Bucket, Prefix = record.Key, MaxUploads = 1000,
                KeyMarker = keyMarker, UploadIdMarker = uploadMarker
            }, verification.Token).ConfigureAwait(false);
            var uploads = response.MultipartUploads ?? [];
            if (response.HttpStatusCode != HttpStatusCode.OK || response.BucketName != record.Bucket
                || response.Prefix != record.Key || response.IsTruncated is null || response.EncodingType is not null
                || !string.IsNullOrEmpty(response.Delimiter) || response.CommonPrefixes?.Count > 0
                || (response.KeyMarker ?? "") != (keyMarker ?? "") || (response.UploadIdMarker ?? "") != (uploadMarker ?? "")
                || uploads.Count > 1000) throw new TransferException(TransferError.RecoveryUnavailable);
            foreach (var upload in uploads)
            {
                if (upload.Key is null || !upload.Key.StartsWith(record.Key, StringComparison.Ordinal)
                    || new System.Text.UTF8Encoding(false, true).GetByteCount(upload.Key) > 1024
                    || string.IsNullOrEmpty(upload.UploadId) || upload.UploadId.Length > 4096 || upload.UploadId.Any(char.IsControl)
                    || !seen.Add((upload.Key, upload.UploadId))) throw new TransferException(TransferError.RecoveryUnavailable);
                if (upload.Key == record.Key && upload.UploadId == record.Multipart!.UploadId) return false;
            }
            if (response.IsTruncated == false) return true;
            if (uploads.Count == 0 || response.NextKeyMarker != uploads[^1].Key || response.NextUploadIdMarker != uploads[^1].UploadId
                || !markers.Add((response.NextKeyMarker, response.NextUploadIdMarker)))
                throw new TransferException(TransferError.RecoveryUnavailable);
            keyMarker = response.NextKeyMarker; uploadMarker = response.NextUploadIdMarker;
        }
        throw new TransferException(TransferError.RecoveryUnavailable);
    }

}
