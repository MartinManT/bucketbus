using System.Diagnostics;
using BucketBus.Core;
using BucketBus.Platform;
using static BucketBus.Desktop.Tests.DownloadRetryTests;

namespace BucketBus.Desktop.Tests;

public sealed class DownloadRetryCrashTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("delay")] [InlineData("second-get")] [InlineData("creation-gap")]
    public async Task Process_termination_keeps_retry_identity_and_never_resumes_or_cleans_at_startup(string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-retry-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(DownloadRetryCrashTests).Assembly.Location); start.ArgumentList.Add("-method"); start.ArgumentList.Add("*DownloadRetryCrashTests.Retry_crash_worker");
            start.Environment["BUCKETBUS_RETRY_CRASH_DIR"] = root; start.Environment["BUCKETBUS_RETRY_CRASH_BOUNDARY"] = boundary;
            using var child = Process.Start(start)!; var output = child.StandardOutput.ReadToEndAsync(Token); var error = child.StandardError.ReadToEndAsync(Token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                while (!File.Exists(Path.Combine(root, "ready"))) { Assert.False(child.HasExited, "Retry worker exited before the durable barrier"); await Task.Delay(20, deadline.Token); }
            }
            finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(Token); await output; await error; }
            var operation = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(root, "ready"), Token));
            var before = Directory.EnumerateFileSystemEntries(Path.Combine(root, "output"), ".bucketbus-*").Order().ToArray();
            await using var journal = new SqliteTransferJournal(Path.Combine(root, "journal"));
            using var profiles = new BrowserFixture(); profiles.Vault.Read = _ => throw new InvalidOperationException("Offline history must not resolve credentials");
            var factory = new SdkFactory(); var files = new TransferFiles(); var coordinator = new TransferCoordinator(profiles.Manager, files, factory, journal);
            var queue = new TransferQueue(profiles.Manager, files, coordinator, journal);
            var rows = await coordinator.HistoryAsync(Token); var recovered = rows.Single(r => r.Id == operation);
            Assert.Equal(TransferState.Interrupted, recovered.State); Assert.Equal(boundary == "delay" ? 1 : 2, recovered.Attempt);
            Assert.Equal(3, recovered.MaximumAttempts); Assert.NotNull(recovered.Temporary);
            Assert.Single(rows, r => r.State == TransferState.Queued); Assert.False(queue.IsRunning); Assert.Empty(factory.Requests);
            Assert.Equal(before, Directory.EnumerateFileSystemEntries(Path.Combine(root, "output"), ".bucketbus-*").Order().ToArray());
            if (boundary == "delay") { Assert.Equal(TransferPhase.RetryDelay, recovered.Phase); Assert.Empty(before); Assert.NotNull(recovered.RetryAt); }
            else Assert.Single(before);
            if (boundary == "creation-gap") { Assert.Equal(TransferPhase.DestinationCreating, recovered.Phase); Assert.False(recovered.CanRestart); }
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Retry_crash_worker()
    {
        var root = Environment.GetEnvironmentVariable("BUCKETBUS_RETRY_CRASH_DIR"); var boundary = Environment.GetEnvironmentVariable("BUCKETBUS_RETRY_CRASH_BOUNDARY");
        if (root is null || boundary is null) return;
        await using var f = new RetryFixture(root); var operation = await f.Add(); await f.Add("tail.bin");
        async Task Stop(CancellationToken ct)
        { await File.WriteAllTextAsync(Path.Combine(root, "ready"), operation.Id.ToString("N"), ct); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        f.Factory.Send = async (_, number, ct) =>
        {
            if (number == 1) throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture");
            if (boundary == "second-get") await Stop(ct);
            return Response(Data);
        };
        f.OnDelay = (_, ct) => boundary == "delay" ? Stop(ct) : Task.CompletedTask;
        if (boundary == "creation-gap") f.Files.AfterCreate = (number, ct) => number == 2 ? Stop(ct) : Task.CompletedTask;
        await f.Start();
    }
}
