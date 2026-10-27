using System.Diagnostics;
using BucketBus.Core;
using BucketBus.Platform;

namespace BucketBus.Desktop.Tests;

public sealed class MultipartWorkflowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(0, false)] [InlineData(MultipartPlan.Threshold - 1, false)] [InlineData(MultipartPlan.Threshold, true)]
    [InlineData(5L * 1024 * 1024 * 1024, true)] [InlineData(5L * 1024 * 1024 * 1024 + 1, true)]
    public async Task Queue_selects_multipart_by_actual_source_length_with_same_operation(long length, bool multipart)
    {
        await using var f = new Fixture(length); var job = await f.Add();
        var result = await f.Queue.RunAsync(false, null, Token); Assert.Equal(1, result.Completed);
        Assert.Equal(multipart ? 1 : 0, f.Remote.MultipartCalls); Assert.Equal(multipart ? 0 : 1, f.Remote.SingleCalls);
        var saved = Assert.Single(await f.Journal.ReadAsync(Token)); Assert.Equal(job.Id, saved.Id); Assert.Equal(TransferState.Completed, saved.State);
        Assert.Equal(multipart, saved.Multipart is not null);
    }
    [Fact]
    public async Task Unsupported_size_sends_nothing()
    {
        await using var f = new Fixture(MultipartPlan.MaximumObject + 1); await f.Add();
        var result = await f.Queue.RunAsync(false, null, Token);
        Assert.Equal(TransferError.RemoteRejected, result.StoppedBy?.Error); Assert.Equal(0, f.Remote.MultipartCalls); Assert.Equal(0, f.Remote.SingleCalls);
    }
    [Theory]
    [InlineData("source")] [InlineData("profile")] [InlineData("credentials")] [InlineData("confirmation")]
    public async Task Explicit_resume_checks_binding_contents_credentials_and_confirmation_before_remote(string fault)
    {
        await using var f = new Fixture(); f.Remote.Stop = "after-ack"; await f.Add(); await f.Queue.RunAsync(false, null, Token);
        var record = Assert.Single(await f.Journal.ReadAsync(Token)); Assert.True(record.CanResumeMultipart);
        if (fault == "source") f.Files.Identity = "replaced";
        if (fault == "profile") f.Profiles.Profiles.Snapshot = f.Profiles.Profiles.Snapshot with { Profiles = [] };
        if (fault == "credentials") f.Profiles.Vault.Read = _ => Task.FromResult(new CredentialResult(CredentialStatus.Missing));
        f.Remote.MultipartCalls = 0;
        var result = await f.Coordinator.RecoverMultipartAsync(record.Id, false, fault != "confirmation", false, null, Token);
        Assert.NotEqual(TransferState.Completed, result.State); Assert.Equal(0, f.Remote.MultipartCalls); Assert.Equal(0, f.Remote.AbortCalls);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Explicit_resume_or_abort_uses_same_owned_record_and_keeps_tail_queued(bool abort)
    {
        await using var f = new Fixture(); f.Remote.Stop = "after-ack"; var job = await f.Add(); await f.Add("tail");
        await f.Queue.RunAsync(false, null, Token); var record = (await f.Journal.ReadAsync(Token)).Single(r => r.Id == job.Id);
        Assert.True(record.CanResumeMultipart); Assert.True(record.CanAbortMultipart);
        f.Remote.Stop = null; var result = await f.Coordinator.RecoverMultipartAsync(job.Id, abort, true, false, null, Token);
        Assert.Equal(abort ? TransferState.Cancelled : TransferState.Completed, result.State);
        record = (await f.Journal.ReadAsync(Token)).Single(r => r.Id == job.Id);
        Assert.True(record.Resolved); Assert.Equal("owned-id", record.Multipart!.UploadId);
        Assert.Single(await f.Journal.ReadAsync(Token), r => r.State == TransferState.Queued);
    }
    [Theory]
    [InlineData("before-create")] [InlineData("id-gap")] [InlineData("after-ack")] [InlineData("part-persist-gap")]
    [InlineData("before-complete")] [InlineData("complete-sent")] [InlineData("complete-ack")]
    public async Task Process_crashes_retain_lifecycle_without_credentials_or_automatic_actions(string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-multipart-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(MultipartWorkflowTests).Assembly.Location); start.ArgumentList.Add("-method"); start.ArgumentList.Add("*MultipartWorkflowTests.Multipart_crash_worker");
            start.Environment["BUCKETBUS_MULTIPART_CRASH_DIR"] = root; start.Environment["BUCKETBUS_MULTIPART_CRASH_BOUNDARY"] = boundary;
            using var child = Process.Start(start)!; var stdout = child.StandardOutput.ReadToEndAsync(Token); var stderr = child.StandardError.ReadToEndAsync(Token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                while (!File.Exists(Path.Combine(root, "ready"))) { Assert.False(child.HasExited, "Multipart worker exited before crash boundary"); await Task.Delay(20, deadline.Token); }
            }
            finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(Token); await stdout; await stderr; }
            var id = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(root, "ready"), Token));
            await using var journal = new SqliteTransferJournal(root); using var profiles = new BrowserFixture();
            profiles.Vault.Read = _ => throw new InvalidOperationException("History cannot read credentials");
            var remote = new Remote(); var coordinator = new TransferCoordinator(profiles.Manager, new Files(MultipartPlan.Threshold), remote, journal);
            var rows = await coordinator.HistoryAsync(Token); var row = rows.Single(r => r.Id == id);
            Assert.Equal(boundary is "before-complete" or "complete-sent" or "complete-ack" ? TransferState.OutcomeUnknown : TransferState.Interrupted, row.State);
            Assert.Equal(boundary is not ("before-create" or "id-gap"), row.Multipart!.UploadId is not null);
            Assert.True(row.CleanupRequired); Assert.Equal(boundary == "after-ack", row.CanResumeMultipart);
            Assert.Equal(boundary is "after-ack" or "part-persist-gap", row.CanAbortMultipart);
            Assert.Equal(0, remote.MultipartCalls + remote.SingleCalls + remote.AbortCalls); Assert.Single(rows, r => r.State == TransferState.Queued);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Multipart_crash_worker()
    {
        var root = Environment.GetEnvironmentVariable("BUCKETBUS_MULTIPART_CRASH_DIR"); var boundary = Environment.GetEnvironmentVariable("BUCKETBUS_MULTIPART_CRASH_BOUNDARY");
        if (root is null || boundary is null) return;
        await using var f = new Fixture(root: root); var job = await f.Add(); await f.Add("tail");
        f.Remote.Boundary = async stage =>
        {
            if (stage != boundary) return;
            await File.WriteAllTextAsync(Path.Combine(root, "ready"), job.Id.ToString("N"), Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, Token);
        };
        await f.Queue.RunAsync(false, null, Token);
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public BrowserFixture Profiles = new(); public Files Files; public Remote Remote = new();
        public SqliteTransferJournal Journal; public TransferCoordinator Coordinator; public TransferQueue Queue;
        public string Root;
        public Fixture(long length = MultipartPlan.Threshold, string? root = null)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), "bucketbus-multipart-" + Guid.NewGuid().ToString("N"));
            Files = new(length); Journal = new(Root); Coordinator = new(Profiles.Manager, Files, Remote, Journal); Queue = new(Profiles.Manager, Files, Coordinator, Journal);
        }
        public Task<TransferRecord> Add(string key = "key") => Queue.EnqueueAsync(Profiles.Profiles.Snapshot.Profiles[0], "test-bucket", key, Path.Combine(Root, "logical-source"), true, Token);
        public async ValueTask DisposeAsync() { await Journal.DisposeAsync(); Profiles.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    internal sealed class Files(long length) : ITransferFiles
    {
        public string Identity = "same-source";
        public Task<ITransferSource> OpenSourceAsync(string path, CancellationToken token) => Task.FromResult<ITransferSource>(new Source(length, Identity));
        public Task<ITransferDestination> CreateDestinationAsync(string dir, string name, CancellationToken token, TemporaryReceipt? previous = null) => throw new NotSupportedException();
    }
    private sealed class Source(long length, string identity) : ITransferSource
    {
        public Stream Content => Stream.Null; public long Length => length; public string Sha256 => Convert.ToBase64String(new byte[32]); public string Md5 => ""; public string SourceIdentity => identity;
        public Task ValidateAsync(CancellationToken token) => Task.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class Remote : IObjectTransferFactory, IObjectTransferSession
    {
        public int SingleCalls, MultipartCalls, AbortCalls; public string? Stop; public Func<string, Task>? Boundary;
        private Task At(string stage) => Boundary?.Invoke(stage) ?? Task.CompletedTask;
        public IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials) => this;
        public Task<TransferResult> UploadAsync(string bucket, string key, ITransferSource source, IProgress<TransferProgress>? progress, CancellationToken token, Guid? operationId = null)
        { SingleCalls++; return Task.FromResult(new TransferResult(TransferState.Completed)); }
        public async Task<TransferResult> UploadMultipartAsync(TransferRecord record, ITransferSource source, Func<MultipartUpload, Task> persist, IProgress<TransferProgress>? progress, CancellationToken token, bool resume = false)
        {
            MultipartCalls++; var state = record.Multipart!;
            if (!resume)
            {
                await At("before-create"); await At("id-gap");
                state = state with { UploadId = "owned-id", Phase = MultipartPhase.Uploading }; await persist(state);
            }
            for (var n = state.Confirmed.Length + 1; n <= state.Plan.PartCount; n++)
            {
                state = state with { Phase = MultipartPhase.SendingPart, PendingPart = n }; await persist(state);
                await At("part-persist-gap");
                state = state with { Phase = MultipartPhase.Uploading, PendingPart = 0, Parts = [..state.Confirmed, new(n, state.Plan.Length(n, source.Length), "opaque", source.Sha256)] };
                await persist(state); await At("after-ack");
                if (Stop == "after-ack") return new(TransferState.Interrupted, CleanupRequired: true);
            }
            state = state with { Phase = MultipartPhase.Completing }; await persist(state); await At("before-complete"); await At("complete-sent"); await At("complete-ack");
            state = state with { Phase = MultipartPhase.Verified }; await persist(state); return new(TransferState.Completed);
        }
        public async Task<TransferResult> AbortMultipartAsync(TransferRecord record, Func<MultipartUpload, Task> persist, CancellationToken token)
        {
            AbortCalls++; var state = record.Multipart! with { Phase = MultipartPhase.Aborting }; await persist(state);
            await persist(state with { Phase = MultipartPhase.Aborted }); return new(TransferState.Cancelled);
        }
        public Task<TransferResult> DownloadAsync(string bucket, string key, Stream destination, IProgress<TransferProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
