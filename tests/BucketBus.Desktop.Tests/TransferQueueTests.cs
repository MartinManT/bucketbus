using System.Diagnostics;
using System.Net;
using BucketBus.Core;
using BucketBus.Platform;
using BucketBus.S3;

namespace BucketBus.Desktop.Tests;

public sealed class TransferQueueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Fifo_is_single_worker_and_accepts_more_jobs_while_active()
    {
        await using var f = new QueueFixture(); var release = new TaskCompletionSource();
        f.Factory.Run = async (key, ct) => { if (key == "1") await release.Task.WaitAsync(ct); return new(TransferState.Completed); };
        var records = new List<TransferRecord>();
        for (var i = 1; i <= 3; i++) records.Add(await f.Add(i.ToString()));
        var running = f.Start(); await f.WaitCalls(1);
        Assert.Equal(TransferError.Busy, (await f.Queue.RunAsync(false, null, Token)).StoppedBy?.Error);
        records.Add(await f.Add("4")); records.Add(await f.Add("5"));
        Assert.Equal(4, (await f.Journal.ReadAsync(Token)).Count(r => r.State == TransferState.Queued));
        release.SetResult(); Assert.Equal(5, (await running).Completed);
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, f.Factory.Keys);
        Assert.Equal(records.Select(r => (Guid?)r.Id), f.Factory.Ids); Assert.Equal(1, f.Factory.MaximumActive);
        Assert.All(await f.Journal.ReadAsync(Token), r => Assert.Equal(TransferState.Completed, r.State));
    }
    [Fact]
    public async Task Pause_finishes_current_without_cancellation_and_requires_explicit_continue()
    {
        await using var f = new QueueFixture(); var release = new TaskCompletionSource(); CancellationToken active = default;
        f.Factory.Run = async (_, ct) => { active = ct; await release.Task.WaitAsync(ct); return new(TransferState.Completed); };
        await f.Add("1"); var second = await f.Add("2"); var run = f.Start(); await f.WaitCalls(1);
        f.Queue.PauseAfterCurrent(); Assert.False(active.IsCancellationRequested); release.SetResult();
        Assert.Equal(1, (await run).Completed); Assert.Single(f.Factory.Keys);
        Assert.Equal(TransferState.Queued, (await f.Journal.ReadAsync(Token)).Single(r => r.Id == second.Id).State);
        Assert.Equal(1, (await f.Start()).Completed); Assert.Equal(new[] { "1", "2" }, f.Factory.Keys);
    }
    [Fact]
    public async Task Cancel_keeps_unknown_put_and_remaining_queue_without_replay()
    {
        await using var f = new QueueFixture();
        f.Factory.Run = async (_, ct) => { try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { } return new(TransferState.OutcomeUnknown); };
        var first = await f.Add("1"); await f.Add("2"); var running = f.Start(); await f.WaitCalls(1); f.Queue.CancelActive();
        Assert.Equal(TransferState.OutcomeUnknown, (await running).StoppedBy?.State);
        var rows = await f.Journal.ReadAsync(Token);
        Assert.Equal(TransferState.OutcomeUnknown, rows.Single(r => r.Id == first.Id).State); Assert.Single(rows, r => r.State == TransferState.Queued);
        var refused = await Assert.ThrowsAsync<TransferException>(() => f.Add("1")); Assert.Equal(TransferError.RecoveryUnavailable, refused.Error);
        f.Factory.Run = (_, _) => Task.FromResult(new TransferResult(TransferState.Completed));
        Assert.Equal(1, (await f.Start()).Completed); Assert.Equal(new[] { "1", "2" }, f.Factory.Keys);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancel_before_credentials_are_available_is_cancelled_without_a_put(bool returnCancelled)
    {
        await using var f = new QueueFixture(); await f.Add("1"); await f.Add("2");
        var waiting = new TaskCompletionSource();
        f.Profiles.Vault.Read = async ct =>
        {
            waiting.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) when (returnCancelled) { }
            return new(CredentialStatus.Cancelled);
        };
        var run = f.Start(); await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); f.Queue.CancelActive();
        Assert.Equal(TransferState.Cancelled, (await run).StoppedBy?.State); Assert.Empty(f.Factory.Keys);
        var rows = await f.Journal.ReadAsync(Token); Assert.Single(rows, r => r.State == TransferState.Cancelled); Assert.Single(rows, r => r.State == TransferState.Queued);
    }
    [Theory]
    [InlineData(TransferState.Failed, TransferError.Conflict)]
    [InlineData(TransferState.Failed, TransferError.PermissionDenied)]
    [InlineData(TransferState.Failed, TransferError.NetworkFailure)]
    [InlineData(TransferState.OutcomeUnknown, TransferError.NetworkFailure)]
    public async Task Middle_error_stops_and_explicit_continue_never_retries_failed_job(TransferState state, TransferError error)
    {
        await using var f = new QueueFixture();
        f.Factory.Run = (key, _) => Task.FromResult(key == "2" ? new TransferResult(state, error) : new(TransferState.Completed));
        await f.Add("1"); var second = await f.Add("2"); var third = await f.Add("3");
        var stopped = await f.Start(); Assert.Equal(1, stopped.Completed); Assert.Equal(error, stopped.StoppedBy?.Error);
        var rows = await f.Journal.ReadAsync(Token);
        Assert.Equal(state, rows.Single(r => r.Id == second.Id).State); Assert.Equal(TransferState.Queued, rows.Single(r => r.Id == third.Id).State);
        Assert.Equal(1, (await f.Start()).Completed); Assert.Equal(new[] { "1", "2", "3" }, f.Factory.Keys);
    }
    [Theory]
    [InlineData("content")] [InlineData("missing")] [InlineData("replacement")]
    public async Task Changed_upload_source_fails_before_upload(string change)
    {
        await using var f = new QueueFixture(); var record = await f.Add("1");
        if (change == "missing") File.Delete(record.LocalPath);
        else if (change == "replacement") { File.Move(record.LocalPath, record.LocalPath + ".old"); await File.WriteAllTextAsync(record.LocalPath, "fixture", Token); }
        else await File.WriteAllTextAsync(record.LocalPath, "changed fixture", Token);
        var result = await f.Start(); Assert.Equal(TransferState.Failed, result.StoppedBy?.State); Assert.Empty(f.Factory.Keys);
        if (change != "missing") Assert.Equal(TransferError.SourceChanged, result.StoppedBy?.Error);
        Assert.Equal(TransferState.Failed, Assert.Single(await f.Journal.ReadAsync(Token)).State);
    }
    [Fact]
    public async Task Destination_collision_stops_before_get_and_preserves_both_data_and_waiting_jobs()
    {
        await using var f = new QueueFixture(); var output = Path.Combine(f.Root, "output"); Directory.CreateDirectory(output);
        await f.Queue.EnqueueAsync(f.Profile, "test-bucket", "prefix/target", output, false, Token);
        await f.Add("later"); await File.WriteAllTextAsync(Path.Combine(output, "target"), "existing", Token);
        Assert.Equal(TransferError.Conflict, (await f.Start()).StoppedBy?.Error);
        Assert.Empty(f.Factory.Keys); Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(output, "target"), Token));
        Assert.Single(await f.Journal.ReadAsync(Token), r => r.State == TransferState.Queued);
    }
    [Theory]
    [InlineData("revision")] [InlineData("endpoint")] [InlineData("bucket")]
    public async Task Profile_binding_changes_fail_before_credentials_or_sdk(string change)
    {
        await using var f = new QueueFixture(); await f.Add("1"); var profile = f.Profile;
        f.Profiles.Profiles.Snapshot = new(2, [profile with { Revision = 2, Settings = change switch
        { "endpoint" => profile.Settings with { Endpoint = "https://different.example.test" }, "bucket" => profile.Settings with { Bucket = "other" }, _ => profile.Settings } }]);
        f.Profiles.Vault.Read = _ => throw new InvalidOperationException("Credentials must not be read for a changed profile");
        Assert.Equal(TransferError.ProfileChanged, (await f.Start()).StoppedBy?.Error); Assert.Empty(f.Factory.Keys);
        Assert.Equal(TransferState.Failed, Assert.Single(await f.Journal.ReadAsync(Token)).State);
    }
    [Fact]
    public async Task Never_started_removal_and_atomic_claim_cannot_delete_started_work()
    {
        await using var f = new QueueFixture(); var first = await f.Add("1"); var second = await f.Add("2");
        Assert.True(await f.Queue.RemoveAsync(first.Id, Token));
        var claimed = await f.Journal.ClaimQueuedAsync(second.Id, Token); Assert.Equal(TransferState.Pending, claimed?.State);
        Assert.False(await f.Queue.RemoveAsync(second.Id, Token)); Assert.Null(await f.Journal.ClaimQueuedAsync(second.Id, Token));
        Assert.Equal(second.Id, Assert.Single(await f.Journal.ReadAsync(Token)).Id); // Failed removal did not poison the owner.
        Assert.NotNull(await f.Add("3"));
    }
    [Fact]
    public async Task Unknown_target_is_protected_across_profiles_and_signing_region_changes()
    {
        await using var f = new QueueFixture(); f.Factory.Run = (_, _) => Task.FromResult(new TransferResult(TransferState.OutcomeUnknown));
        await f.Add("1"); await f.Start(); var old = f.Profile;
        var settings = old.Settings with { Region = "different-region" }; var id = Guid.NewGuid();
        var other = new ConnectionProfile(id, 1, settings, new(id, Guid.NewGuid(), CredentialStorage.Secure, settings.TargetFingerprint()));
        f.Profiles.Profiles.Snapshot = new(2, [old, other]);
        var error = await Assert.ThrowsAsync<TransferException>(() => f.Queue.EnqueueAsync(other, "test-bucket", "1", Path.Combine(f.Root, "1"), true, Token));
        Assert.Equal(TransferError.RecoveryUnavailable, error.Error); Assert.Single(f.Factory.Keys);
    }
    [Fact]
    public async Task Process_kill_retains_queued_jobs_separately_from_unknown_put_and_never_auto_runs()
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-queue-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(TransferQueueTests).Assembly.Location); start.ArgumentList.Add("-method"); start.ArgumentList.Add("*TransferQueueTests.Queue_crash_worker");
            start.Environment["BUCKETBUS_QUEUE_CRASH_DIR"] = root;
            using var child = Process.Start(start)!; var stdout = child.StandardOutput.ReadToEndAsync(Token); var stderr = child.StandardError.ReadToEndAsync(Token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                while (!File.Exists(Path.Combine(root, "ready"))) { Assert.False(child.HasExited, "Worker exited before PUT barrier"); await Task.Delay(20, deadline.Token); }
            }
            finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(Token); await stdout; await stderr; }
            await using var journal = new SqliteTransferJournal(Path.Combine(root, "journal"));
            var records = await journal.ReadAsync(Token); Assert.Equal(3, records.Count);
            var first = Assert.Single(records, r => r.State == TransferState.OutcomeUnknown);
            Assert.Equal(first.Id.ToString("N"), await File.ReadAllTextAsync(Path.Combine(root, "ready"), Token));
            Assert.Equal(new long?[] { 2, 3 }, records.Where(r => r.State == TransferState.Queued).OrderBy(r => r.QueueOrder).Select(r => r.QueueOrder));
            using var profiles = new BrowserFixture(); profiles.Vault.Read = _ => throw new InvalidOperationException("Offline startup must not resolve credentials");
            var factory = new QueueFactory(); var coordinator = new TransferCoordinator(profiles.Manager, new TransferFiles(), factory, journal);
            var queue = new TransferQueue(profiles.Manager, new TransferFiles(), coordinator, journal);
            Assert.False(queue.IsRunning); Assert.Equal(3, (await coordinator.HistoryAsync(Token)).Count); Assert.Empty(factory.Keys);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Queue_crash_worker()
    {
        var root = Environment.GetEnvironmentVariable("BUCKETBUS_QUEUE_CRASH_DIR"); if (root is null) return;
        using var profiles = new BrowserFixture(); await using var journal = new SqliteTransferJournal(Path.Combine(root, "journal"));
        var files = new TransferFiles(); var coordinator = new TransferCoordinator(profiles.Manager, files, new CrashFactory(root), journal);
        var queue = new TransferQueue(profiles.Manager, files, coordinator, journal); var profile = profiles.Profiles.Snapshot.Profiles[0];
        for (var i = 1; i <= 3; i++) { var source = Path.Combine(root, i.ToString()); await File.WriteAllTextAsync(source, "fixture", Token); await queue.EnqueueAsync(profile, "test-bucket", i.ToString(), source, true, Token); }
        await queue.RunAsync(false, null, Token);
    }
    private sealed class CrashFactory(string root) : IObjectTransferFactory
    {
        public IObjectTransferSession Create(ConnectionProfile p, Credentials c) => S3TransferFactory.Create(p, c, new Transport(root), TimeSpan.FromMinutes(1));
        private sealed class Transport(string root) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Assert.Equal(HttpMethod.Put, request.Method); await request.Content!.CopyToAsync(Stream.Null, ct);
                await File.WriteAllTextAsync(Path.Combine(root, "ready"), request.Headers.GetValues("x-amz-meta-bucketbus-operation").Single(), ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(HttpStatusCode.OK);
            }
        }
    }
    internal sealed class QueueFactory : IObjectTransferFactory
    {
        public List<string> Keys { get; } = []; public List<Guid?> Ids { get; } = [];
        private int _active; public int MaximumActive;
        public Func<string, CancellationToken, Task<TransferResult>> Run = (_, _) => Task.FromResult(new TransferResult(TransferState.Completed));
        public IObjectTransferSession Create(ConnectionProfile p, Credentials c) => new Session(this);
        private sealed class Session(QueueFactory owner) : IObjectTransferSession
        {
            public async Task<TransferResult> UploadAsync(string b, string k, ITransferSource source, IProgress<TransferProgress>? p, CancellationToken ct, Guid? operationId = null)
            {
                lock (owner.Keys) { owner.Keys.Add(k); owner.Ids.Add(operationId); owner.MaximumActive = Math.Max(owner.MaximumActive, ++owner._active); }
                try { p?.Report(new(source.Length, source.Length, "Uploading")); return await owner.Run(k, ct); }
                finally { lock (owner.Keys) owner._active--; }
            }
            public async Task<TransferResult> DownloadAsync(string b, string k, Stream output, IProgress<TransferProgress>? p, CancellationToken ct)
            {
                lock (owner.Keys) owner.Keys.Add(k); await output.WriteAsync("fixture"u8.ToArray(), ct); return await owner.Run(k, ct);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    internal sealed class QueueFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bucketbus-queue-" + Guid.NewGuid().ToString("N"));
        public BrowserFixture Profiles { get; } = new();
        public ConnectionProfile Profile => Profiles.Profiles.Snapshot.Profiles[0];
        public SqliteTransferJournal Journal { get; }
        public QueueFactory Factory { get; } = new();
        public TransferCoordinator Coordinator { get; }
        public TransferQueue Queue { get; }
        private Task<QueueRunResult>? _running;
        public QueueFixture()
        {
            Directory.CreateDirectory(Root); Journal = new(Path.Combine(Root, "journal")); var files = new TransferFiles();
            Coordinator = new(Profiles.Manager, files, Factory, Journal); Queue = new(Profiles.Manager, files, Coordinator, Journal);
        }
        public async Task<TransferRecord> Add(string key)
        {
            var path = Path.Combine(Root, key); if (!File.Exists(path)) await File.WriteAllTextAsync(path, "fixture", Token);
            return await Queue.EnqueueAsync(Profile, "test-bucket", key, path, true, Token);
        }
        public Task<QueueRunResult> Start() => _running = Queue.RunAsync(false, null, Token);
        public async Task WaitCalls(int count)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true) { lock (Factory.Keys) if (Factory.Keys.Count >= count) return; await Task.Delay(5, deadline.Token); }
        }
        public async ValueTask DisposeAsync()
        {
            Queue.CancelActive(); if (_running is not null) await _running.WaitAsync(TimeSpan.FromSeconds(5));
            Profiles.Dispose(); await Journal.DisposeAsync(); Directory.Delete(Root, true);
        }
    }
}
