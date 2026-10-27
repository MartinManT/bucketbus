using System.Net;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using BucketBus.Core;

namespace BucketBus.S3.Tests;

public sealed class MultipartTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("key")] [InlineData("/prefix//é é +%&漢字.bin")]
    public async Task Parts_are_ordered_bounded_and_conditionally_completed_with_full_readback(string key)
    {
        using var f = new Fixture(key); var stages = new List<TransferProgress>();
        var result = await f.Run(new InlineProgress(stages.Add));
        Assert.True(result.State == TransferState.Completed, $"{result}; calls={string.Join(",",f.Calls)}; phase={f.Record.Multipart!.Phase}; fixture={f.FixtureError}"); Assert.Equal(MultipartPhase.Verified, f.Record.Multipart!.Phase);
        Assert.Equal(new[] { "create", "part1", "part2", "part3", "complete", "verify" }, f.Calls);
        Assert.Equal(f.Source.Length, f.Record.Multipart.AcknowledgedBytes);
        Assert.Contains(stages, p => p.Stage.Contains("Completing")); Assert.Contains(stages, p => p.Stage.Contains("Verifying"));
        Assert.True(f.Source.Stream.MaxRead <= 131072, $"Read buffer exceeded bound: {f.Source.Stream.MaxRead}");
        Assert.Equal(17, f.Record.Multipart.Confirmed[^1].Size);
    }
    [Theory]
    [InlineData(412, "PreconditionFailed", TransferError.Conflict)] [InlineData(409, "ConditionalRequestConflict", TransferError.Conflict)]
    [InlineData(501, "NotImplemented", TransferError.ConditionalWriteUnsupported)] [InlineData(403, "AccessDenied", TransferError.PermissionDenied)]
    public async Task Definite_completion_rejection_never_falls_back_or_restarts(int status, string code, TransferError error)
    {
        using var f = new Fixture(); f.Fault = (stage, _) => stage == "complete" ? Task.FromResult<HttpResponseMessage?>(SdkFixture.Xml(SdkFixture.Error(code), (HttpStatusCode)status)) : Task.FromResult<HttpResponseMessage?>(null);
        var result = await f.Run(); Assert.Equal(TransferState.Failed, result.State); Assert.Equal(error, result.Error);
        Assert.True(result.CleanupRequired); Assert.Equal(MultipartPhase.CompletionRejected, f.Record.Multipart!.Phase);
        Assert.Single(f.Calls, c => c == "complete"); Assert.DoesNotContain("verify", f.Calls); Assert.DoesNotContain("abort", f.Calls);
    }
    [Theory]
    [InlineData("lost")] [InlineData("embedded200")] [InlineData("malformed200")] [InlineData("wrong-identity")]
    public async Task Ambiguous_completion_is_never_replayed_or_aborted(string fault)
    {
        using var f = new Fixture(); f.Fault = (stage, _) =>
        {
            if (stage != "complete") return Task.FromResult<HttpResponseMessage?>(null);
            if (fault == "lost") throw new HttpRequestException("synthetic lost response");
            return Task.FromResult<HttpResponseMessage?>(SdkFixture.Xml(fault switch {
                "embedded200" => SdkFixture.Error("InternalError"), "wrong-identity" => "<CompleteMultipartUploadResult><Bucket>wrong</Bucket><Key>key</Key><ETag>opaque</ETag></CompleteMultipartUploadResult>", _ => "<broken>" }));
        };
        var result = await f.Run(); Assert.Equal(TransferState.OutcomeUnknown, result.State); Assert.True(result.CleanupRequired);
        Assert.Single(f.Calls, c => c == "complete"); Assert.False(f.Record.Multipart!.CanAbort); Assert.False(f.Record.Multipart.CanResume);
    }
    [Theory]
    [InlineData("create")] [InlineData("part2")]
    public async Task Lost_initiation_or_part_acknowledgement_is_retained_without_replay(string stage)
    {
        using var f = new Fixture(); f.Fault = (at, _) => at == stage ? throw new HttpRequestException("lost") : Task.FromResult<HttpResponseMessage?>(null);
        var result = await f.Run(); Assert.Equal(TransferState.Interrupted, result.State); Assert.True(result.CleanupRequired);
        Assert.Single(f.Calls, c => c == stage); Assert.DoesNotContain("complete", f.Calls); Assert.False(f.Record.Multipart!.CanResume);
        Assert.Equal(stage != "create", f.Record.Multipart.CanAbort);
    }
    [Theory]
    [InlineData("part2")] [InlineData("complete")]
    public async Task Cancellation_preserves_ownership_without_automatic_abort(string at)
    {
        using var f = new Fixture(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        f.Fault = async (stage, ct) => { if (stage == at) { cancel.Cancel(); await Task.Delay(1, ct); } return null; };
        var result = await f.Run(token: cancel.Token);
        Assert.Equal(at == "complete" ? TransferState.OutcomeUnknown : TransferState.Interrupted, result.State);
        Assert.True(result.CleanupRequired); Assert.DoesNotContain("abort", f.Calls);
    }
    [Theory]
    [InlineData("hash")] [InlineData("metadata")]
    public async Task Incorrect_full_content_or_authorship_never_reports_completed(string fault)
    {
        using var f = new Fixture(); f.BadReadback = fault;
        var result = await f.Run(); Assert.Equal(TransferState.OutcomeUnknown, result.State);
        Assert.Equal(TransferError.IntegrityMismatch, result.Error); Assert.False(f.Record.Multipart!.CanAbort);
    }
    [Fact]
    public async Task Source_mutation_before_completion_never_sends_commit()
    {
        using var f = new Fixture();
        f.Fault = (stage, _) => { if (stage == "part2") f.Source.Changed = true; return Task.FromResult<HttpResponseMessage?>(null); };
        var result = await f.Run(); Assert.Equal(TransferState.Interrupted, result.State);
        Assert.Equal(TransferError.SourceChanged, result.Error); Assert.DoesNotContain("complete", f.Calls);
    }
    [Fact]
    public async Task Short_part_stream_is_rejected_before_part_send()
    {
        using var f = new Fixture(); f.Source.Stream.StopAt = 50;
        var result = await f.Run(); Assert.Equal(TransferError.SourceChanged, result.Error); Assert.Equal(new[] { "create" }, f.Calls);
    }
    [Theory]
    [InlineData("Uploading", 0)] [InlineData("Uploading", 1)] [InlineData("SendingPart", 2)] [InlineData("Completing", 3)] [InlineData("Verifying", 3)] [InlineData("Verified", 3)]
    public async Task Journal_barrier_failure_stops_next_effect(string phase, int count)
    {
        using var f = new Fixture();
        f.PersistFailure = state => state.Phase.ToString() == phase && state.Confirmed.Length == count;
        var result = await f.Run(); Assert.Equal(TransferError.JournalUnavailable, result.Error);
        Assert.Equal(TransferState.RequiresReconciliation, result.State);
        if (phase == "Completing") Assert.DoesNotContain("complete", f.Calls);
        if (phase == "Verifying") Assert.DoesNotContain("verify", f.Calls);
        Assert.Single(f.Calls, c => c == "create");
    }
    [Theory]
    [InlineData("agree")] [InlineData("missing")] [InlineData("etag")] [InlineData("extra")] [InlineData("checksum")]
    public async Task Explicit_resume_requires_exact_remote_part_agreement(string evidence)
    {
        using var f = new Fixture(); await f.SeedOnePart(); f.ListEvidence = evidence;
        var result = await f.Run(resume: true);
        if (evidence == "agree") { Assert.Equal(TransferState.Completed, result.State); Assert.DoesNotContain("part1", f.Calls); }
        else { Assert.Equal(TransferState.Interrupted, result.State); Assert.DoesNotContain("part2", f.Calls); Assert.DoesNotContain("complete", f.Calls); }
        Assert.DoesNotContain("create", f.Calls);
    }
    [Theory]
    [InlineData("absent")] [InlineData("already-absent")] [InlineData("retained")] [InlineData("lost")]
    public async Task Abort_is_exact_owned_id_and_requires_confirmed_absence(string evidence)
    {
        using var f = new Fixture(); await f.SeedOnePart(); f.AbortEvidence = evidence;
        await using var session = f.Session(); var result = await session.AbortMultipartAsync(f.Record, f.Save, Token);
        Assert.Equal(evidence is "absent" or "already-absent" ? TransferState.Cancelled : TransferState.Interrupted, result.State);
        Assert.Equal(evidence is not ("absent" or "already-absent"), result.CleanupRequired); Assert.Equal("abort", f.Calls[0]);
        Assert.DoesNotContain("complete", f.Calls);
    }
    [Theory]
    [InlineData("empty", true)] [InlineData("other-upload", true)] [InlineData("paginated", true)]
    [InlineData("retained", false)] [InlineData("retained-page2", false)] [InlineData("denied", false)]
    [InlineData("missing-bucket", false)] [InlineData("malformed", false)] [InlineData("repeated-marker", false)]
    [InlineData("wrong-prefix", false)] [InlineData("wrong-bucket", false)] [InlineData("grouped", false)]
    [InlineData("encoded", false)] [InlineData("missing-upload-id", false)] [InlineData("outside-prefix", false)]
    [InlineData("bad-marker", false)] [InlineData("page-limit", false)] [InlineData("different-key-same-id", true)]
    public async Task Ceph_missing_key_after_abort_requires_complete_scoped_upload_listing(string evidence, bool resolved)
    {
        using var f = new Fixture(); await f.SeedOnePart(); f.AbortEvidence = "ceph"; f.CleanupEvidence = evidence;
        await using var session = f.Session(); var result = await session.AbortMultipartAsync(f.Record, f.Save, Token);
        Assert.Equal(resolved ? TransferState.Cancelled : TransferState.Interrupted, result.State);
        Assert.Equal(!resolved, result.CleanupRequired);
        Assert.Equal(resolved ? MultipartPhase.Aborted : MultipartPhase.Aborting, f.Record.Multipart!.Phase);
        Assert.Equal(new[] { "abort", "list" }, f.Calls.Take(2));
        Assert.Contains("cleanup-list", f.Calls);
        Assert.All(f.Calls.Skip(2), call => Assert.Equal("cleanup-list", call));
        Assert.Single(f.Calls, call => call == "abort"); Assert.DoesNotContain("complete", f.Calls);
        Assert.Null(f.FixtureError);
        if (evidence == "page-limit") Assert.Equal(16, f.Calls.Count(c => c == "cleanup-list"));
    }
    [Fact]
    public async Task Ceph_cleanup_listing_preserves_unusual_exact_keys()
    {
        using var f = new Fixture("/prefix//é é +%&漢字.bin"); await f.SeedOnePart(); f.AbortEvidence = "ceph"; f.CleanupEvidence = "empty";
        await using var session = f.Session(); var result = await session.AbortMultipartAsync(f.Record, f.Save, Token);
        Assert.Equal(TransferState.Cancelled, result.State); Assert.False(result.CleanupRequired); Assert.Null(f.FixtureError);
    }
    [Theory]
    [InlineData("bucket")] [InlineData("prefix")] [InlineData("query")] [InlineData("method")]
    public async Task Multipart_cleanup_listing_guard_rejects_scope_changes_before_transport(string fault)
    {
        var calls = 0; var profile = TransferTests.Profile();
        using var transport = new TransferTransport(profile, new ScriptTransport((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }); }));
        transport.BeginMultipartListing("test-bucket", "key", null, null);
        using var client = new HttpClient(transport);
        var uri = profile.Settings.Endpoint + (fault == "bucket" ? "/other-bucket" : "/test-bucket")
            + "?uploads&max-uploads=1000&prefix=" + (fault == "prefix" ? "other-key" : "key") + (fault == "query" ? "&delimiter=/" : "");
        using var request = new HttpRequestMessage(fault == "method" ? HttpMethod.Delete : HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", "AWS4-HMAC-SHA256 Credential=SYNTHETIC/20261027/us-east-1/s3/aws4_request");
        var error = await Assert.ThrowsAsync<TransferException>(() => client.SendAsync(request, Token));
        Assert.Equal(TransferError.RemoteRejected, error.Error); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task Unknown_completion_cannot_be_aborted_and_can_only_be_read_back()
    {
        using var f = new Fixture(); await f.SeedOnePart();
        // Build a complete valid durable ledger without sending writes.
        var parts = new List<UploadedPart>();
        for (var n = 1; n <= f.Record.Multipart!.Plan.PartCount; n++)
        {
            using var part = new PartStream(f.Source.Content, f.Record.Multipart.Plan.Offset(n), f.Record.Multipart.Plan.Length(n, f.Source.Length));
            parts.Add(new(n, part.Length, "\"opaque-part\"", Convert.ToBase64String(await SHA256.HashDataAsync(part, Token))));
        }
        f.Record = f.Record with { State = TransferState.OutcomeUnknown, Multipart = f.Record.Multipart! with { Phase = MultipartPhase.Completing, Parts = parts.ToArray() } };
        await using (var session = f.Session()) Assert.Equal(TransferError.RecoveryUnavailable, (await session.AbortMultipartAsync(f.Record, f.Save, Token)).Error);
        Assert.Empty(f.Calls);
        await using (var session = f.Session()) Assert.Equal(TransferState.Completed, (await session.ReconcileUploadAsync(f.Record, Token)).State);
        Assert.Equal(new[] { "verify" }, f.Calls);
    }
    [Fact]
    public async Task Part_view_preserves_offsets_above_five_gib_without_allocating_the_file()
    {
        const long boundary = 5L * 1024 * 1024 * 1024;
        using var source = new LogicalStream(boundary + 17);
        using var part = new PartStream(source, boundary - 6, 23);
        var bytes = new byte[32]; Assert.Equal(23, await part.ReadAsync(bytes, Token)); Assert.Equal(0, await part.ReadAsync(bytes, Token));
        for (var n = 0; n < 23; n++) Assert.Equal(LogicalStream.Byte(boundary - 6 + n), bytes[n]);
        Assert.Equal(23, part.Length); part.Seek(-1, SeekOrigin.End); Assert.Equal(LogicalStream.Byte(boundary + 16), part.ReadByte());
        Assert.Throws<IOException>(() => part.Seek(24, SeekOrigin.Begin)); Assert.Throws<IOException>(() => part.Seek(-1, SeekOrigin.Begin));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task List_parts_pagination_is_bounded_and_requires_monotonic_agreement(bool repeat)
    {
        using var f = new Fixture(); await f.SeedOnePart();
        using var part = new PartStream(f.Source.Content, MultipartPlan.MinimumPart, MultipartPlan.MinimumPart);
        var second = new UploadedPart(2, MultipartPlan.MinimumPart, "\"opaque-part\"", Convert.ToBase64String(await SHA256.HashDataAsync(part, Token)));
        f.Record = f.Record with { Multipart = f.Record.Multipart! with { Parts = [..f.Record.Multipart.Confirmed, second] } };
        f.ListEvidence = repeat ? "repeat" : "paginate";
        var result = await f.Run(resume: true);
        Assert.Equal(repeat ? TransferState.Interrupted : TransferState.Completed, result.State);
        Assert.Equal(2, f.Calls.Count(c => c == "list")); Assert.DoesNotContain("part1", f.Calls); Assert.DoesNotContain("part2", f.Calls);
    }
    [Theory]
    [InlineData("missing-id")] [InlineData("wrong-key")]
    public async Task Invalid_creation_evidence_never_grants_ownership_or_sends_parts(string fault)
    {
        using var f = new Fixture();
        f.Fault = (stage, _) => Task.FromResult<HttpResponseMessage?>(stage == "create"
            ? SdkFixture.Xml("<InitiateMultipartUploadResult><Bucket>test-bucket</Bucket><Key>" + (fault == "wrong-key" ? "other" : "key")
                + "</Key>" + (fault == "missing-id" ? "" : "<UploadId>wrong-target-id</UploadId>") + "</InitiateMultipartUploadResult>") : null);
        var result = await f.Run(); Assert.Equal(TransferState.Interrupted, result.State); Assert.True(result.CleanupRequired);
        Assert.Null(f.Record.Multipart!.UploadId); Assert.False(f.Record.Multipart.CanAbort); Assert.Equal(new[] { "create" }, f.Calls);
    }
    [Fact]
    public async Task Cancellation_before_completion_leaves_acknowledged_upload_resumable_without_commit()
    {
        using var f = new Fixture(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var result = await f.Run(new InlineProgress(p => { if (p.Stage == "Checking source before completion") cancel.Cancel(); }), token: cancel.Token);
        Assert.Equal(TransferState.Interrupted, result.State); Assert.True(f.Record.Multipart!.CanResume); Assert.True(f.Record.Multipart.CanAbort);
        Assert.DoesNotContain("complete", f.Calls); Assert.DoesNotContain("abort", f.Calls);
    }
    internal sealed class InlineProgress(Action<TransferProgress> action) : IProgress<TransferProgress> { public void Report(TransferProgress value) => action(value); }
    internal sealed class Fixture : IDisposable
    {
        public LogicalSource Source { get; } = new(2 * MultipartPlan.MinimumPart + 17);
        public TransferRecord Record;
        public List<string> Calls { get; } = [];
        public Func<string, CancellationToken, Task<HttpResponseMessage?>>? Fault;
        public Func<MultipartUpload, bool>? PersistFailure;
        public Exception? FixtureError;
        public string? BadReadback, AbortEvidence, CleanupEvidence;
        public string ListEvidence = "agree";
        public Fixture(string key = "key")
        {
            var profile = TransferTests.Profile();
            Record = new(Guid.NewGuid(), 1, profile.Id, profile.Revision, profile.Settings.TargetFingerprint(), profile.Settings.Endpoint,
                "test-bucket", key, Path.GetFullPath("synthetic.bin"), true, TransferState.Running, Total: Source.Length, Sha256: Source.Sha256,
                SourceIdentity: Source.SourceIdentity, Multipart: new(new(MultipartPlan.MinimumPart, 3), MultipartPhase.Creating));
        }
        public Task Save(MultipartUpload state)
        {
            state.Validate(Source.Length); if (PersistFailure?.Invoke(state) == true) throw new JournalException();
            Record = Record with { Multipart = state }; return Task.CompletedTask;
        }
        public IObjectTransferSession Session() => S3TransferFactory.Create(TransferTests.Profile(), new("SYNTHETICKEY", "synthetic-secret"), new ScriptTransport(async (r, ct) => { try { return await Send(r, ct); } catch (Exception e) { FixtureError = e; throw; } }), TimeSpan.FromSeconds(20));
        public async Task<TransferResult> Run(IProgress<TransferProgress>? progress = null, bool resume = false, CancellationToken? token = null)
        { await using var session = Session(); return await session.UploadMultipartAsync(Record, Source, Save, progress, token ?? Token, resume); }
        public async Task SeedOnePart()
        {
            using var part = new PartStream(Source.Content, 0, MultipartPlan.MinimumPart);
            var hash = Convert.ToBase64String(await SHA256.HashDataAsync(part, Token));
            Record = Record with { State = TransferState.Interrupted, Multipart = Record.Multipart! with { Phase = MultipartPhase.Uploading, UploadId = "owned+/id=", Parts = [new(1, MultipartPlan.MinimumPart, "\"opaque-part\"", hash)] } };
        }
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            var query = request.RequestUri!.Query;
            var cleanupList = request.Method == HttpMethod.Get && query.Contains("uploads", StringComparison.Ordinal);
            if (cleanupList) Assert.Equal("/test-bucket", Uri.UnescapeDataString(request.RequestUri.AbsolutePath).TrimEnd('/'));
            else Assert.Equal("/test-bucket/" + Record.Key, Uri.UnescapeDataString(request.RequestUri.AbsolutePath));
            var stage = cleanupList ? "cleanup-list" : request.Method == HttpMethod.Delete ? "abort" : query.Contains("partNumber=") ? "part" + query.Split("partNumber=")[1].Split('&')[0]
                : query.Contains("uploads") ? "create" : query.Contains("uploadId") ? request.Method == HttpMethod.Post ? "complete" : "list" : "verify";
            Calls.Add(stage);
            if (stage is not ("create" or "verify" or "cleanup-list")) Assert.Contains("uploadId=owned+/id=", Uri.UnescapeDataString(query));
            if (Fault is not null && await Fault(stage, token) is { } fault) return fault;
            if (cleanupList)
            {
                Assert.Contains("prefix=" + Record.Key, Uri.UnescapeDataString(query));
                Assert.Contains("max-uploads=1000", query);
                if (CleanupEvidence == "denied") return SdkFixture.Xml(SdkFixture.Error("AccessDenied"), HttpStatusCode.Forbidden);
                if (CleanupEvidence == "missing-bucket") return SdkFixture.Xml(SdkFixture.Error("NoSuchBucket"), HttpStatusCode.NotFound);
                var parameters = query.TrimStart('?').Split('&').Select(s => s.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "");
                var second = parameters.ContainsKey("key-marker");
                var page = Calls.Count(c => c == "cleanup-list");
                var truncated = CleanupEvidence is "paginated" or "retained-page2" or "repeated-marker" or "bad-marker" && !second
                    || CleanupEvidence is "repeated-marker" or "page-limit";
                var retained = CleanupEvidence == "retained" || CleanupEvidence == "retained-page2" && second;
                var otherId = CleanupEvidence == "page-limit" ? "other-id-" + page : "other-id";
                string Upload(string key, string? id) => "<Upload><Key>" + SecurityElement.Escape(key) + "</Key>"
                    + (id is null ? "" : "<UploadId>" + SecurityElement.Escape(id) + "</UploadId>") + "</Upload>";
                var uploads = CleanupEvidence switch
                {
                    "different-key-same-id" => Upload(Record.Key + "/child", "owned+/id="),
                    "missing-upload-id" => Upload(Record.Key, null),
                    "outside-prefix" => Upload("other-key", "other-id"),
                    "paginated" when second => "",
                    _ when retained => Upload(Record.Key, "owned+/id="),
                    "other-upload" or "paginated" or "retained-page2" or "repeated-marker" or "bad-marker" or "page-limit" => Upload(Record.Key, otherId),
                    _ => ""
                };
                return SdkFixture.Xml("<ListMultipartUploadsResult><Bucket>" + (CleanupEvidence == "wrong-bucket" ? "other-bucket" : "test-bucket")
                    + "</Bucket><Prefix>" + SecurityElement.Escape(CleanupEvidence == "wrong-prefix" ? "other-prefix" : Record.Key) + "</Prefix>"
                    + (second ? "<KeyMarker>" + SecurityElement.Escape(Record.Key) + "</KeyMarker><UploadIdMarker>" + SecurityElement.Escape(parameters["upload-id-marker"]) + "</UploadIdMarker>" : "")
                    + (CleanupEvidence == "malformed" ? "" : "<IsTruncated>" + truncated.ToString().ToLowerInvariant() + "</IsTruncated>")
                    + (truncated ? "<NextKeyMarker>" + SecurityElement.Escape(Record.Key) + "</NextKeyMarker><NextUploadIdMarker>" + (CleanupEvidence == "bad-marker" ? "not-last-upload" : otherId) + "</NextUploadIdMarker>" : "")
                    + (CleanupEvidence == "encoded" ? "<EncodingType>url</EncodingType>" : "")
                    + (CleanupEvidence == "grouped" ? "<CommonPrefixes><Prefix>group/</Prefix></CommonPrefixes>" : "")
                    + uploads + "</ListMultipartUploadsResult>");
            }
            var identity = "<Bucket>test-bucket</Bucket><Key>" + SecurityElement.Escape(Record.Key) + "</Key>";
            if (stage == "create")
            {
                Assert.Equal(Record.Id.ToString("N"), Assert.Single(request.Headers.GetValues("x-amz-meta-bucketbus-operation")));
                return SdkFixture.Xml("<InitiateMultipartUploadResult>" + identity + "<UploadId>owned+/id=</UploadId></InitiateMultipartUploadResult>");
            }
            if (stage.StartsWith("part", StringComparison.Ordinal))
            {
                var number = int.Parse(stage[4..], System.Globalization.CultureInfo.InvariantCulture);
                var expectedLength = Record.Multipart!.Plan.Length(number, Source.Length);
                await using var body = await request.Content!.ReadAsStreamAsync(token);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long count = 0; int read;
                while ((read = await body.ReadAsync(buffer, token)) > 0)
                {
                    for (var i = 0; i < read; i++) if (LogicalStream.Byte(Record.Multipart.Plan.Offset(number) + count + i) != buffer[i]) Assert.Fail("Part byte/offset mismatch");
                    count += read; sha.AppendData(buffer, 0, read);
                }
                Assert.Equal(expectedLength, count); Assert.Equal(Convert.ToBase64String(sha.GetHashAndReset()), Assert.Single(request.Headers.GetValues("x-amz-checksum-sha256")));
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                response.Headers.TryAddWithoutValidation("ETag", "\"opaque-part\""); return response;
            }
            if (stage == "complete")
            {
                Assert.Equal("*", Assert.Single(request.Headers.GetValues("If-None-Match")));
                var body = await request.Content!.ReadAsStringAsync(token); Assert.Contains("<PartNumber>3</PartNumber>", body);
                return SdkFixture.Xml("<CompleteMultipartUploadResult>" + identity + "<ETag>opaque-3</ETag></CompleteMultipartUploadResult>");
            }
            if (stage == "abort")
            {
                if (AbortEvidence == "lost") throw new HttpRequestException("lost");
                if (AbortEvidence == "already-absent") return SdkFixture.Xml(SdkFixture.Error("NoSuchUpload"), HttpStatusCode.NotFound);
                return new(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) };
            }
            if (stage == "list")
            {
                if (AbortEvidence == "ceph") return SdkFixture.Xml(SdkFixture.Error("NoSuchKey"), HttpStatusCode.NotFound);
                if (AbortEvidence is "absent" or "already-absent") return SdkFixture.Xml(SdkFixture.Error("NoSuchUpload"), HttpStatusCode.NotFound);
                var page = Calls.Count(c => c == "list");
                var part = Record.Multipart!.Confirmed[ListEvidence == "paginate" ? page - 1 : 0];
                var parts = ListEvidence == "missing" ? "" : $"<Part><PartNumber>{part.Number}</PartNumber><Size>{part.Size}</Size><ETag>{SecurityElement.Escape(ListEvidence == "etag" ? "wrong" : part.ETag)}</ETag>"
                    + (ListEvidence == "checksum" ? "<ChecksumSHA256>wrong</ChecksumSHA256>" : "") + "</Part>";
                if (ListEvidence == "extra") parts += "<Part><PartNumber>2</PartNumber><Size>5</Size><ETag>extra</ETag></Part>";
                return SdkFixture.Xml("<ListPartsResult>" + identity + "<UploadId>owned+/id=</UploadId>" + (ListEvidence is "paginate" or "repeat" && page == 1 ? "<IsTruncated>true</IsTruncated><NextPartNumberMarker>1</NextPartNumberMarker>" : "<IsTruncated>false</IsTruncated>") + parts + "</ListPartsResult>");
            }
            var content = new LogicalStream(Source.Length) { Corrupt = BadReadback == "hash" };
            var readback = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) };
            readback.Content.Headers.ContentLength = Source.Length; readback.Headers.TryAddWithoutValidation("ETag", "\"opaque-not-md5\"");
            readback.Headers.TryAddWithoutValidation("x-amz-meta-bucketbus-operation", BadReadback == "metadata" ? "other" : Record.Id.ToString("N")); return readback;
        }
        public void Dispose() => Source.Dispose();
    }
    internal sealed class LogicalSource : ITransferSource, IDisposable
    {
        public LogicalStream Stream { get; }
        public bool Changed;
        public LogicalSource(long size) { Stream = new(size); Sha256 = Convert.ToBase64String(SHA256.HashData(Stream)); Stream.Position = 0; }
        public Stream Content => Stream; public long Length => Stream.Length; public string Sha256 { get; }
        public string Md5 => ""; public string SourceIdentity => "synthetic-stable-source";
        public Task ValidateIdentityAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task ValidateAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); if (Changed) throw new TransferException(TransferError.SourceChanged); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; } public void Dispose() => Stream.Dispose();
    }
    internal sealed class LogicalStream(long length) : Stream
    {
        public int MaxRead; public long? StopAt; public bool Corrupt;
        public static byte Byte(long offset) => (byte)(offset % 251);
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get; set; }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            MaxRead = Math.Max(MaxRead, buffer.Length); var count = (int)Math.Max(0, Math.Min(buffer.Length, Math.Min(length, StopAt ?? length) - Position));
            for (var i = 0; i < count; i++) buffer[i] = Corrupt ? (byte)0 : Byte(Position + i);
            Position += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
