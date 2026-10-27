using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BucketBus.Core;
using BucketBus.Platform;
using BucketBus.S3;

namespace BucketBus.Desktop.Tests;

public sealed class DownloadRetryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static readonly byte[] Data = Encoding.UTF8.GetBytes("complete é é +% fixture");
    [Theory]
    [InlineData("connection")] [InlineData("short")] [InlineData("closure")] [InlineData("timeout")]
    public async Task Transient_failure_restarts_full_get_and_then_preserves_fifo(string fault)
    {
        await using var f = new RetryFixture(); var inspections = 0;
        f.OnDelay = async (_, _) =>
        {
            inspections++; Assert.Empty(Directory.EnumerateFileSystemEntries(f.Output, ".bucketbus-*")); Assert.False(File.Exists(f.Final));
            var row = Assert.Single(await f.Journal.ReadAsync(Token), r => r.State == TransferState.Running);
            Assert.Equal(TransferPhase.RetryDelay, row.Phase); Assert.Equal(1, row.Attempt); Assert.NotNull(row.Temporary); Assert.NotNull(row.RetryAt);
            Assert.Single(f.Factory.Requests);
        };
        f.Factory.Send = async (_, number, ct) =>
        {
            if (number == 1)
            {
                if (fault == "connection") throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture");
                if (fault == "timeout") { try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { f.Factory.Deadline = TimeSpan.FromSeconds(5); } }
                if (fault == "closure") { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ClosedResponse()) }; response.Content.Headers.ContentLength = Data.Length; return response; }
                var partial = Response([1, 2, 3], checksum: false); partial.Content.Headers.ContentLength = Data.Length + 10; return partial;
            }
            return Response(Data);
        };
        if (fault == "timeout") f.Factory.Deadline = TimeSpan.FromMilliseconds(50);
        var first = await f.Add(); await f.Add("tail.bin"); var result = await f.Start();
        Assert.True(result.Completed == 2, $"Stopped: {result.StoppedBy}; requests={f.Factory.Requests.Count}; attempts={result.LastRecord?.Attempt}; phase={result.LastRecord?.Phase}"); Assert.Null(result.StoppedBy); Assert.Equal(1, inspections);
        Assert.Equal(new[] { f.Key, f.Key, "tail.bin" }, f.Factory.Requests.Select(r => r.Key));
        Assert.All(f.Factory.Requests, r => Assert.Equal(HttpMethod.Get, r.Method)); Assert.All(f.Files.InitialPositions, p => Assert.Equal(0, p));
        Assert.Equal(Data, await File.ReadAllBytesAsync(f.Final, Token));
        var saved = (await f.Journal.ReadAsync(Token)).Single(r => r.Id == first.Id);
        Assert.Equal(2, saved.Attempt); Assert.Equal(3, saved.MaximumAttempts); Assert.Equal(TransferState.Completed, saved.State); Assert.Null(saved.RetryAt);
    }
    [Theory]
    [InlineData(429)] [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(504)]
    public async Task Retryable_http_errors_have_three_physical_attempts_then_stop_queue(int status)
    {
        await using var f = new RetryFixture(); f.Factory.Send = (_, _, _) => Task.FromResult(Error(status));
        var job = await f.Add(); var tail = await f.Add("tail.bin"); var result = await f.Start();
        Assert.Equal(TransferState.Failed, result.StoppedBy?.State); Assert.True(result.StoppedBy?.RetryLimitReached); Assert.Equal(3, result.StoppedBy?.Attempts);
        Assert.Equal(3, f.Factory.Requests.Count); Assert.Equal(2, f.Delays.Count); Assert.False(File.Exists(f.Final));
        var rows = await f.Journal.ReadAsync(Token); var failed = rows.Single(r => r.Id == job.Id);
        Assert.True(failed.RetryLimitReached); Assert.Equal(3, failed.Attempt); Assert.Equal(status == 429 ? TransferError.RateLimited : TransferError.TransientService, failed.Error);
        Assert.Equal(TransferState.Queued, rows.Single(r => r.Id == tail.Id).State);
    }
    [Theory]
    [InlineData("401", TransferError.AuthenticationFailed)] [InlineData("403", TransferError.PermissionDenied)] [InlineData("404", TransferError.ObjectMissing)]
    [InlineData("501", TransferError.RemoteRejected)] [InlineData("505", TransferError.RemoteRejected)]
    [InlineData("checksum", TransferError.IntegrityMismatch)] [InlineData("overflow", TransferError.IntegrityMismatch)]
    [InlineData("tls", TransferError.TlsFailure)] [InlineData("unknown", TransferError.NetworkFailure)]
    public async Task Non_transient_remote_failures_never_retry(string fault, TransferError expected)
    {
        await using var f = new RetryFixture();
        f.Factory.Send = (_, _, _) =>
        {
            if (fault == "tls") throw new HttpRequestException(HttpRequestError.SecureConnectionError, "fixture");
            if (fault == "unknown") throw new HttpRequestException("misleading HTTP 503 timeout words");
            if (fault == "checksum") { var response = Response(Data); response.Headers.Remove("x-amz-checksum-sha256"); response.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", Convert.ToBase64String(new byte[32])); return Task.FromResult(response); }
            if (fault == "overflow") { var response = Response(Data, checksum: false); response.Content.Headers.ContentLength = 1; return Task.FromResult(response); }
            return Task.FromResult(Error(int.Parse(fault, System.Globalization.CultureInfo.InvariantCulture)));
        };
        await f.Add(); await f.Add("tail.bin"); var result = await f.Start();
        Assert.Equal(expected, result.StoppedBy?.Error); Assert.Single(f.Factory.Requests); Assert.Empty(f.Delays);
        Assert.False(File.Exists(f.Final)); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Output, ".bucketbus-*"));
    }
    [Theory]
    [InlineData("disk", TransferError.DiskFull)] [InlineData("permission", TransferError.PermissionDenied)] [InlineData("conflict", TransferError.Conflict)]
    public async Task Local_failures_never_retry(string fault, TransferError expected)
    {
        await using var f = new RetryFixture();
        if (fault == "disk") f.Files.FailWrite = TransferError.DiskFull;
        if (fault == "permission") f.Files.FailWrite = TransferError.PermissionDenied;
        if (fault == "conflict") await File.WriteAllTextAsync(f.Final, "existing", Token);
        await f.Add(); var result = await f.Start(); Assert.Equal(expected, result.StoppedBy?.Error); Assert.Empty(f.Delays);
        Assert.Equal(fault == "conflict" ? 0 : 1, f.Factory.Requests.Count);
        if (fault == "conflict") Assert.Equal("existing", await File.ReadAllTextAsync(f.Final, Token));
    }
    [Theory]
    [InlineData("cleanup", TransferError.CleanupRequired)] [InlineData("receipt", TransferError.RecoveryUnavailable)]
    [InlineData("unexpected-temp", TransferError.RecoveryUnavailable)] [InlineData("publication", TransferError.Conflict)]
    public async Task Uncertain_cleanup_or_destination_prevents_another_attempt(string fault, TransferError expected)
    {
        await using var f = new RetryFixture(); f.Factory.Send = (_, _, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture");
        f.Files.FailCleanup = fault == "cleanup"; f.Files.HideReceipt = fault == "receipt";
        f.Files.AfterDispose = receipt =>
        {
            if (fault == "publication") File.WriteAllText(f.Final, "outside data");
            if (fault == "unexpected-temp")
            {
                var path = Path.Combine(f.Output, receipt!.Name);
                if (OperatingSystem.IsLinux()) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "unknown"), "outside data"); }
                else File.WriteAllText(path, "outside data");
            }
        };
        await f.Add(); var result = await f.Start(); Assert.Equal(TransferState.RequiresReconciliation, result.StoppedBy?.State);
        Assert.Equal(expected, result.StoppedBy?.Error); Assert.Single(f.Factory.Requests); Assert.Empty(f.Delays);
        if (fault == "cleanup") Assert.True(Assert.Single(await f.Journal.ReadAsync(Token)).CleanupRequired);
        if (fault == "publication") Assert.Equal("outside data", await File.ReadAllTextAsync(f.Final, Token));
        if (fault == "unexpected-temp") Assert.Single(Directory.EnumerateFileSystemEntries(f.Output, ".bucketbus-*"));
    }
    [Theory]
    [InlineData("parent", TransferError.DestinationChanged)] [InlineData("final", TransferError.Conflict)]
    public async Task Native_creation_rechecks_parent_and_final_after_backoff(string change, TransferError expected)
    {
        await using var f = new RetryFixture(); f.Factory.Send = (_, _, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture");
        f.OnDelay = (_, _) =>
        {
            if (change == "parent") { Directory.Move(f.Output, f.Output + ".old"); Directory.CreateDirectory(f.Output); }
            else File.WriteAllText(f.Final, "outside data");
            return Task.CompletedTask;
        };
        await f.Add(); var result = await f.Start();
        Assert.Equal(TransferState.RequiresReconciliation, result.StoppedBy?.State); Assert.Equal(expected, result.StoppedBy?.Error);
        Assert.Single(f.Factory.Requests); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Output, ".bucketbus-*"));
        if (change == "final") Assert.Equal("outside data", await File.ReadAllTextAsync(f.Final, Token));
    }
    [Fact]
    public async Task Ambiguous_local_commit_preserves_published_file_and_never_retries()
    {
        await using var f = new RetryFixture(); f.Files.FailAfterCommit = true; await f.Add(); var result = await f.Start();
        Assert.True(result.StoppedBy?.State == TransferState.RequiresReconciliation, $"Stopped: {result.StoppedBy}; requests={f.Factory.Requests.Count}; phase={result.LastRecord?.Phase}"); Assert.Single(f.Factory.Requests); Assert.Empty(f.Delays);
        Assert.Equal(Data, await File.ReadAllBytesAsync(f.Final, Token)); Assert.Equal(TransferPhase.Committing, Assert.Single(await f.Journal.ReadAsync(Token)).Phase);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancel_active_get_or_backoff_stops_without_further_attempts(bool duringDelay)
    {
        await using var f = new RetryFixture(); var ready = new TaskCompletionSource();
        if (duringDelay)
        {
            f.Factory.Send = (_, _, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture");
            f.OnDelay = async (_, ct) => { ready.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        }
        else f.Factory.Send = async (_, _, ct) => { ready.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Response(Data); };
        await f.Add(); await f.Add("tail.bin"); var run = f.Start(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); f.Queue.CancelActive();
        Assert.Equal(TransferState.Cancelled, (await run).StoppedBy?.State); Assert.Single(f.Factory.Requests);
        var rows = await f.Journal.ReadAsync(Token); Assert.Single(rows, r => r.State == TransferState.Queued); Assert.Single(rows, r => r.State == TransferState.Cancelled);
        Assert.False(File.Exists(f.Final));
    }
    [Fact]
    public async Task Pause_during_backoff_allows_current_item_to_finish_its_budget_then_leaves_tail_queued()
    {
        await using var f = new RetryFixture(); var ready = new TaskCompletionSource(); var release = new TaskCompletionSource();
        f.Factory.Send = (_, number, _) => number == 1 ? throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture") : Task.FromResult(Response(Data));
        f.OnDelay = async (_, ct) => { ready.SetResult(); await release.Task.WaitAsync(ct); };
        await f.Add(); await f.Add("tail.bin"); var run = f.Start(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        f.Queue.PauseAfterCurrent(); release.SetResult(); Assert.Equal(1, (await run).Completed);
        Assert.Equal(2, f.Factory.Requests.Count); Assert.Single(await f.Journal.ReadAsync(Token), r => r.State == TransferState.Queued);
        Assert.Equal(1, (await f.Start()).Completed); Assert.Equal("tail.bin", f.Factory.Requests.Last().Key);
    }
    [Fact]
    public async Task Changed_profile_during_backoff_stops_before_new_credentials_or_get()
    {
        await using var f = new RetryFixture(); f.Factory.Send = (_, _, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture");
        f.OnDelay = (_, _) =>
        {
            var profile = f.Profile; f.Profiles.Profiles.Snapshot = new(2, [profile with { Revision = profile.Revision + 1 }]);
            f.Profiles.Vault.Read = _ => throw new InvalidOperationException("No credential read after profile change");
            return Task.CompletedTask;
        };
        await f.Add(); var result = await f.Start(); Assert.Equal(TransferError.ProfileChanged, result.StoppedBy?.Error); Assert.Single(f.Factory.Requests);
    }
    [Theory]
    [InlineData(TransferPhase.RetryDelay)] [InlineData(TransferPhase.DestinationCreating)]
    public async Task Retry_barrier_write_failure_stops_before_another_get(TransferPhase phase)
    {
        await using var f = new RetryFixture(); f.FailRetryPhase = phase;
        f.Factory.Send = (_, _, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture");
        await f.Add(); await f.Add("tail.bin"); var result = await f.Start();
        Assert.Equal(TransferError.JournalUnavailable, result.StoppedBy?.Error); Assert.Single(f.Factory.Requests);
        var row = Assert.Single(await f.Journal.ReadAsync(Token), r => r.State == TransferState.Running);
        Assert.NotNull(row.Temporary); Assert.Equal(1, row.Attempt);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Output, ".bucketbus-*"));
    }
    [Theory]
    [InlineData("lost")] [InlineData("500")] [InlineData("timeout")] [InlineData("cancel")]
    public async Task Put_is_never_replayed_even_with_retryable_transport_status_or_cancellation(string fault)
    {
        await using var f = new RetryFixture(); var sent = new TaskCompletionSource();
        f.Factory.Send = async (request, _, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Error(404);
            Assert.Equal("*", Assert.Single(request.Headers.GetValues("If-None-Match"))); await request.Content!.CopyToAsync(Stream.Null, ct); sent.TrySetResult();
            if (fault == "500") return Error(500);
            if (fault is "timeout" or "cancel") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new IOException("synthetic lost PUT response");
        };
        if (fault == "timeout") f.Factory.Deadline = TimeSpan.FromMilliseconds(50);
        var source = Path.Combine(f.Root, "source"); await File.WriteAllBytesAsync(source, Data, Token);
        var job = await f.Queue.EnqueueAsync(f.Profile, "test-bucket", "upload.bin", source, true, Token); await f.Add("tail.bin");
        var run = f.Start(); if (fault == "cancel") { await sent.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); f.Queue.CancelActive(); }
        Assert.Equal(TransferState.OutcomeUnknown, (await run).StoppedBy?.State); Assert.Single(f.Factory.Requests, r => r.Method == HttpMethod.Put);
        Assert.Empty(f.Delays); var row = (await f.Journal.ReadAsync(Token)).Single(r => r.Id == job.Id);
        Assert.Equal(1, row.Attempt); Assert.Equal(1, row.MaximumAttempts); Assert.Equal(TransferState.OutcomeUnknown, row.State);
    }
    internal static HttpResponseMessage Response(byte[] data, bool checksum = true)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        response.Headers.TryAddWithoutValidation("ETag", "\"opaque-not-md5\"");
        if (checksum) response.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", Convert.ToBase64String(SHA256.HashData(data)));
        return response;
    }
    internal static HttpResponseMessage Error(int status) => new((HttpStatusCode)status)
    { Content = new StringContent("<Error><Code>SyntheticCode</Code><Message>synthetic fixture</Message></Error>") };
    internal sealed class RetryFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bucketbus-download-retry-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "output"); public string Key = "prefix//é +%.bin";
        public string Final => Path.Combine(Output, Key[(Key.LastIndexOf('/') + 1)..]);
        public BrowserFixture Profiles { get; } = new(); public ConnectionProfile Profile => Profiles.Profiles.Snapshot.Profiles[0];
        public SqliteTransferJournal Journal { get; } public FaultFiles Files { get; } = new(); public SdkFactory Factory { get; } = new();
        public TransferQueue Queue { get; } public TransferCoordinator Coordinator { get; }
        public List<TimeSpan> Delays { get; } = []; public TransferPhase? FailRetryPhase; public Func<TimeSpan, CancellationToken, Task>? OnDelay;
        private Task<QueueRunResult>? _running;
        public RetryFixture(string? root = null)
        {
            if (root is not null) Root = root;
            Directory.CreateDirectory(Output); Journal = new(Path.Combine(Root, "journal"));
            var retry = new DownloadRetryPolicy((delay, ct) => { Delays.Add(delay); return OnDelay?.Invoke(delay, ct) ?? Task.CompletedTask; });
            var guarded = new RetryBarrierJournal(this);
            Coordinator = new(Profiles.Manager, Files, Factory, guarded, retry); Queue = new(Profiles.Manager, Files, Coordinator, guarded);
        }
        public Task<TransferRecord> Add(string? key = null) => Queue.EnqueueAsync(Profile, "test-bucket", key ?? Key, Output, false, Token, Data.Length);
        public Task<QueueRunResult> Start() => _running = Queue.RunAsync(false, null, Token);
        public async ValueTask DisposeAsync()
        {
            Queue.CancelActive(); if (_running is not null) await _running.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Profiles.Dispose(); await Journal.DisposeAsync(); Directory.Delete(Root, true);
        }
    }
    private sealed class RetryBarrierJournal(RetryFixture fixture) : ITransferJournal
    {
        public Task<IReadOnlyList<TransferRecord>> ReadAsync(CancellationToken ct) => fixture.Journal.ReadAsync(ct);
        public Task DismissAsync(Guid id, CancellationToken ct) => fixture.Journal.DismissAsync(id, ct);
        public Task<TransferRecord> EnqueueAsync(TransferRecord record, CancellationToken ct) => fixture.Journal.EnqueueAsync(record, ct);
        public Task<TransferRecord?> ClaimQueuedAsync(Guid id, CancellationToken ct) => fixture.Journal.ClaimQueuedAsync(id, ct);
        public Task<bool> RemoveQueuedAsync(Guid id, CancellationToken ct) => fixture.Journal.RemoveQueuedAsync(id, ct);
        public Task SaveAsync(TransferRecord record, CancellationToken ct)
        {
            if (fixture.FailRetryPhase == record.Phase && (record.Phase == TransferPhase.RetryDelay || record.Attempt > 1)) throw new JournalException();
            return fixture.Journal.SaveAsync(record, ct);
        }
    }
    internal sealed class SdkFactory : IObjectTransferFactory
    {
        public ConcurrentQueue<(HttpMethod Method, string Key)> Requests { get; } = new(); public TimeSpan Deadline = TimeSpan.FromSeconds(5);
        public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Send = (_, _, _) => Task.FromResult(Response(Data));
        private int _requests;
        public IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials) => S3TransferFactory.Create(profile, credentials, new Handler(this), Deadline);
        private sealed class Handler(SdkFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Assert.False(request.Headers.Contains("Range")); var key = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath)["/test-bucket/".Length..];
                owner.Requests.Enqueue((request.Method, key)); return owner.Send(request, Interlocked.Increment(ref owner._requests), ct);
            }
        }
    }
    internal sealed class FaultFiles : ITransferFiles
    {
        private readonly TransferFiles _native = new(); public TransferError? FailWrite; public bool FailCleanup, HideReceipt, FailAfterCommit;
        public Action<TemporaryReceipt?>? AfterDispose; public Func<int, CancellationToken, Task>? AfterCreate; private int _created; public List<long> InitialPositions { get; } = [];
        public Task<ITransferSource> OpenSourceAsync(string path, CancellationToken ct) => _native.OpenSourceAsync(path, ct);
        public async Task<ITransferDestination> CreateDestinationAsync(string directory, string name, CancellationToken ct, TemporaryReceipt? previous = null)
        {
            var inner = await _native.CreateDestinationAsync(directory, name, ct, previous); InitialPositions.Add(inner.Content.Position);
            try { if (AfterCreate is not null) await AfterCreate(++_created, ct); return new Destination(inner, this); }
            catch { await inner.DisposeAsync(); throw; }
        }
        public Task ValidateRetryDestinationAsync(TransferRecord record, CancellationToken ct) => _native.ValidateRetryDestinationAsync(record, ct);
        public Task RecoverDestinationAsync(TransferRecord record, CancellationToken ct) => _native.RecoverDestinationAsync(record, ct);
        private sealed class Destination(ITransferDestination inner, FaultFiles owner) : ITransferDestination
        {
            private readonly TemporaryReceipt? _receipt = inner.Receipt;
            public Stream Content => owner.FailWrite is { } error ? new FailedOutput(inner.Content, error) : inner.Content;
            public TemporaryReceipt? Receipt => owner.HideReceipt ? null : _receipt;
            public bool CleanupRequired => owner.FailCleanup || inner.CleanupRequired;
            public async Task CommitAsync(CancellationToken ct) { await inner.CommitAsync(ct); if (owner.FailAfterCommit) throw new IOException("lost local publication acknowledgement"); }
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); owner.AfterDispose?.Invoke(_receipt); if (owner.FailCleanup) throw new IOException("injected cleanup failure"); }
        }
    }
    private sealed class FailedOutput(Stream inner, TransferError error) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => true;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => throw (error == TransferError.DiskFull ? new IOException("fixture", 28) : new UnauthorizedAccessException("fixture"));
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
    }
    private sealed class ClosedResponse : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => Data.Length; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new HttpIOException(HttpRequestError.ResponseEnded, "fixture");
        public override int Read(byte[] b, int o, int n) => throw new HttpIOException(HttpRequestError.ResponseEnded, "fixture");
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }
}
