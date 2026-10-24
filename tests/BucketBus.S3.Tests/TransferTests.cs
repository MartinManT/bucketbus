using System.Net;
using System.Security.Cryptography;
using System.Text;
using BucketBus.Core;

namespace BucketBus.S3.Tests;

public sealed class TransferTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("exact é é 漢字 % + content");
    internal static ConnectionProfile Profile()
    {
        var s = new ProfileSettings("Fixture", "https://storage.example.test:9443", "test-region-1", AddressingStyle.Path);
        var id = Guid.NewGuid(); return new(id, 1, s, new(id, Guid.NewGuid(), CredentialStorage.Session, s.TargetFingerprint()));
    }
    private static IObjectTransferSession Session(ScriptTransport transport, bool qualified = true, TimeSpan? deadline = null) =>
        S3TransferFactory.Create(Profile(), new("SYNTHETICKEY", "synthetic-secret"), new Qualification(qualified), transport, deadline ?? TimeSpan.FromSeconds(5));
    private static HttpResponseMessage Object(byte[] bytes, string? operation = null, bool checksum = true)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Headers.TryAddWithoutValidation("ETag", "\"opaque-not-md5\"");
        if (checksum) response.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", Convert.ToBase64String(SHA256.HashData(bytes)));
        if (operation is not null) response.Headers.TryAddWithoutValidation("x-amz-meta-bucketbus-operation", operation);
        return response;
    }
    [Theory]
    [InlineData("", "é %+.txt")] [InlineData("hello", "é %+.txt")] [InlineData("leading", "/prefix//é %+.txt")]
    public async Task Upload_uses_conditional_put_explicit_checksums_then_exact_content_readback(string content, string key)
    {
        var data = Encoding.UTF8.GetBytes(content); string? operation = null;
        using var source = new Source(data);
        var transport = new ScriptTransport(async (request, token) =>
        {
            Assert.Equal("/test-bucket/" + key, Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("*", Assert.Single(request.Headers.GetValues("If-None-Match")));
                Assert.Equal(source.Sha256, Assert.Single(request.Headers.GetValues("x-amz-checksum-sha256")));
                operation = Assert.Single(request.Headers.GetValues("x-amz-meta-bucketbus-operation"));
                var sent = await request.Content!.ReadAsByteArrayAsync(token);
                Assert.Equal(data, sent);
                var response = Object([]); return response;
            }
            Assert.Equal(HttpMethod.Get, request.Method); return Object(data, operation);
        });
        await using var session = Session(transport);
        var result = await session.UploadAsync("test-bucket", key, source, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Completed, result.State); Assert.Contains("SHA-256", result.Verification); Assert.Equal(2, transport.Attempts);
    }
    [Theory]
    [InlineData(412)] [InlineData(409)] [InlineData(403)]
    public async Task Confirmed_rejection_does_not_retry_or_reconcile(int status)
    {
        var transport = new ScriptTransport((_, _) => Task.FromResult(SdkFixture.Xml(SdkFixture.Error("Rejected"), (HttpStatusCode)status)));
        await using var session = Session(transport); using var source = new Source(Data);
        var result = await session.UploadAsync("test-bucket", "key", source, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Failed, result.State); Assert.Equal(1, transport.Attempts);
    }
    [Fact]
    public async Task Lost_put_response_is_unknown_never_replayed_and_size_alone_cannot_reconcile()
    {
        var puts = 0;
        var transport = new ScriptTransport((r, _) =>
        {
            if (r.Method == HttpMethod.Put) { puts++; throw new HttpRequestException("synthetic-private-detail"); }
            return Task.FromResult(Object(Data));
        });
        await using var session = Session(transport); using var source = new Source(Data);
        var result = await session.UploadAsync("test-bucket", "key", source, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.OutcomeUnknown, result.State); Assert.Equal(1, puts); Assert.Equal(2, transport.Attempts);
        Assert.DoesNotContain("synthetic", result.ToString());
    }
    [Fact]
    public async Task Lost_response_can_be_reconciled_only_with_operation_identity_and_full_hash()
    {
        string? operation = null;
        var transport = new ScriptTransport((r, _) =>
        {
            if (r.Method == HttpMethod.Put) { operation = Assert.Single(r.Headers.GetValues("x-amz-meta-bucketbus-operation")); throw new HttpRequestException(); }
            return Task.FromResult(Object(Data, operation));
        });
        await using var session = Session(transport); using var source = new Source(Data);
        var result = await session.UploadAsync("test-bucket", "key", source, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Completed, result.State); Assert.Contains("Reconciled", result.Verification); Assert.Equal(2, transport.Attempts);
    }
    [Fact]
    public async Task Cancellation_after_send_remains_unknown_and_does_not_replay()
    {
        using var cancel = new CancellationTokenSource();
        var transport = new ScriptTransport((r, _) => { cancel.Cancel(); throw new OperationCanceledException(); });
        await using var session = Session(transport); using var source = new Source(Data);
        var result = await session.UploadAsync("test-bucket", "key", source, null, cancel.Token);
        Assert.Equal(TransferState.OutcomeUnknown, result.State); Assert.Equal(1, transport.Attempts);
    }
    [Fact]
    public async Task Unqualified_upload_and_precancelled_download_do_not_send()
    {
        var transport = new ScriptTransport((_, _) => throw new Exception("Must not send"));
        await using var session = Session(transport, false); using var source = new Source(Data);
        Assert.Equal(TransferError.UploadNotQualified, (await session.UploadAsync("test-bucket", "key", source, null, TestContext.Current.CancellationToken)).Error);
        var transport2 = new ScriptTransport((_, _) => throw new Exception("Must not send"));
        await using var session2 = Session(transport2); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Equal(TransferState.Cancelled, (await session2.DownloadAsync("test-bucket", "key", Stream.Null, null, cancel.Token)).State);
        Assert.Equal(0, transport.Attempts + transport2.Attempts);
    }
    [Theory]
    [InlineData("/leading//é%+", true)] [InlineData("normal", false)]
    public async Task Downloads_preserve_exact_keys_and_report_actual_integrity_level(string key, bool checksum)
    {
        var transport = new ScriptTransport((r, _) =>
        { Assert.Equal("/test-bucket/" + key, Uri.UnescapeDataString(r.RequestUri!.AbsolutePath)); return Task.FromResult(Object(Data, checksum: checksum)); });
        await using var session = Session(transport); using var output = new MemoryStream();
        var result = await session.DownloadAsync("test-bucket", key, output, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Completed, result.State); Assert.Equal(Data, output.ToArray());
        Assert.Contains(checksum ? "SHA-256 verified" : "equality not established", result.Verification);
    }
    [Fact]
    public async Task Short_read_is_not_completed()
    {
        var transport = new ScriptTransport((_, _) => { var r = Object(Data, checksum: false); r.Content.Headers.ContentLength = Data.Length + 1; return Task.FromResult(r); });
        await using var session = Session(transport);
        var result = await session.DownloadAsync("test-bucket", "key", Stream.Null, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Failed, result.State); Assert.Equal(TransferError.IntegrityMismatch, result.Error);
    }
    [Fact]
    public async Task Source_mutation_after_put_cannot_be_reported_as_completed()
    {
        using var source = new Source(Data);
        var transport = new ScriptTransport((_, _) => { source.Changed = true; return Task.FromResult(Object([])); });
        await using var session = Session(transport);
        var result = await session.UploadAsync("test-bucket", "key", source, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.OutcomeUnknown, result.State); Assert.Equal(TransferError.SourceChanged, result.Error);
    }
    [Theory]
    [InlineData(-1, TransferError.PermissionDenied)]
    [InlineData(28, TransferError.DiskFull)] [InlineData(112, TransferError.DiskFull)] [InlineData(5, TransferError.LocalFailure)]
    public async Task Destination_write_failure_does_not_complete(int code, TransferError expected)
    {
        var transport = new ScriptTransport((_, _) => Task.FromResult(Object(Data)));
        await using var session = Session(transport); using var stream = new FailingOutput(code);
        var result = await session.DownloadAsync("test-bucket", "key", stream, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Failed, result.State); Assert.Equal(expected, result.Error);
    }
    [Fact]
    public async Task Wrong_response_checksum_is_an_integrity_failure()
    {
        var transport = new ScriptTransport((_, _) =>
        {
            var r = Object(Data); r.Headers.Remove("x-amz-checksum-sha256");
            r.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", Convert.ToBase64String(new byte[32]));
            return Task.FromResult(r);
        });
        await using var session = Session(transport);
        var result = await session.DownloadAsync("test-bucket", "key", Stream.Null, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Failed, result.State); Assert.Equal(TransferError.IntegrityMismatch, result.Error);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Large_response_is_streamed_in_bounded_chunks_and_connection_loss_fails(bool disconnect)
    {
        using var input = new GeneratedInput(32 * 1024 * 1024, disconnect);
        using var output = new CountingOutput(input);
        var transport = new ScriptTransport((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) };
            response.Content.Headers.ContentLength = input.Length; return Task.FromResult(response);
        });
        await using var session = Session(transport);
        var result = await session.DownloadAsync("test-bucket", "stream", output, null, TestContext.Current.CancellationToken);
        Assert.Equal(disconnect ? TransferState.Failed : TransferState.Completed, result.State);
        Assert.True(input.MaximumRead <= 65536); Assert.True(output.WroteBeforeEnd);
        if (!disconnect) Assert.Equal(input.Length, output.Bytes);
    }
    [Fact]
    public async Task Cancellation_during_download_and_deadline_do_not_retry()
    {
        using var cancel = new CancellationTokenSource();
        var transport = new ScriptTransport(async (_, ct) => { cancel.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Object(Data); });
        await using var session = Session(transport);
        Assert.Equal(TransferState.Cancelled, (await session.DownloadAsync("test-bucket", "key", Stream.Null, null, cancel.Token)).State);
        Assert.Equal(1, transport.Attempts);
        var stalled = new ScriptTransport(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Object(Data); });
        await using var timed = Session(stalled, deadline: TimeSpan.FromMilliseconds(50));
        Assert.Equal(TransferError.Timeout, (await timed.DownloadAsync("test-bucket", "key", Stream.Null, null, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(1, stalled.Attempts);
    }
    [Fact]
    public async Task Get_failure_after_acknowledged_put_is_unknown_not_a_confirmed_failed_write()
    {
        var transport = new ScriptTransport((r, _) => Task.FromResult(r.Method == HttpMethod.Put ? Object([]) : SdkFixture.Xml(SdkFixture.Error("AccessDenied"), HttpStatusCode.Forbidden)));
        await using var session = Session(transport); using var source = new Source(Data);
        var result = await session.UploadAsync("test-bucket", "key", source, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.OutcomeUnknown, result.State);
    }
    [Fact]
    public async Task Pre_cancelled_upload_sends_nothing_and_progress_never_implies_completion()
    {
        var transport = new ScriptTransport((_, _) => throw new Exception("No I/O"));
        await using var session = Session(transport); using var source = new Source(Data);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await session.UploadAsync("test-bucket", "key", source, null, cancel.Token);
        Assert.Equal(TransferState.Cancelled, result.State); Assert.Equal(0, transport.Attempts);
    }
    private sealed class GeneratedInput(long length, bool disconnect) : Stream
    {
        private long _position;
        public int MaximumRead;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); MaximumRead = Math.Max(MaximumRead, buffer.Length);
            if (disconnect && _position >= 65536) throw new IOException("synthetic connection loss");
            var count = (int)Math.Min(buffer.Length, length - _position); buffer.Span[..count].Clear(); _position += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).Result;
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class CountingOutput(GeneratedInput input) : MemoryStream
    {
        public long Bytes; public bool WroteBeforeEnd;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Bytes += buffer.Length; WroteBeforeEnd |= input.Position < input.Length; return ValueTask.CompletedTask; }
    }
    private sealed class FailingOutput(int code) : MemoryStream
    { public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => throw (code == -1 ? new UnauthorizedAccessException("fixture") : new IOException("fixture", code)); }
    private sealed class Qualification(bool allowed) : IUploadQualification { public bool Allows(ProfileSettings s, string b) => allowed; }
    private sealed class Source(byte[] bytes) : ITransferSource, IDisposable
    {
        public bool Changed { get; set; }
        public Stream Content { get; } = new MemoryStream(bytes);
        public long Length => bytes.Length;
        public string Sha256 => Convert.ToBase64String(SHA256.HashData(bytes));
        public string Md5 => Convert.ToBase64String(MD5.HashData(bytes));
        public Task ValidateAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); if (Changed) throw new TransferException(TransferError.SourceChanged); Content.Position = 0; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose() => Content.Dispose();
    }
}
