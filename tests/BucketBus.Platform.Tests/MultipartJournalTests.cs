using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BucketBus.Core;
using BucketBus.Platform;
using Microsoft.Data.Sqlite;

namespace BucketBus.Platform.Tests;

public sealed class MultipartJournalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("upload-id")] [InlineData("part-ack")] [InlineData("verified")]
    public async Task Killing_inside_sqlite_commit_preserves_previous_multipart_evidence(string stage)
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-multipart-transaction-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(MultipartJournalTests).Assembly.Location); start.ArgumentList.Add("-method"); start.ArgumentList.Add("*MultipartJournalTests.Transaction_worker");
            start.Environment["BUCKETBUS_MULTIPART_TRANSACTION_DIR"] = root; start.Environment["BUCKETBUS_MULTIPART_TRANSACTION_STAGE"] = stage;
            using var child = Process.Start(start)!; var stdout = child.StandardOutput.ReadToEndAsync(Token); var stderr = child.StandardError.ReadToEndAsync(Token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                while (!File.Exists(Path.Combine(root, "ready"))) { Assert.False(child.HasExited, "Worker exited before transaction barrier"); await Task.Delay(20, deadline.Token); }
            }
            finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(Token); await stdout; await stderr; }
            await using var journal = new SqliteTransferJournal(root); var recovered = Assert.Single(await journal.ReadAsync(Token));
            Assert.Equal(stage == "verified" ? TransferState.OutcomeUnknown : TransferState.Interrupted, recovered.State);
            Assert.Equal(stage switch { "upload-id" => MultipartPhase.Creating, "part-ack" => MultipartPhase.SendingPart, _ => MultipartPhase.Verifying }, recovered.Multipart!.Phase);
            Assert.Equal(stage == "upload-id", recovered.Multipart.UploadId is null); Assert.True(recovered.CleanupRequired);
            Assert.False(recovered.CanResumeMultipart); Assert.False(recovered.Resolved);
            using var db = new SqliteConnection($"Data Source={root}/transfers.db;Pooling=False"); db.Open(); using var check = db.CreateCommand(); check.CommandText = "PRAGMA user_version"; Assert.Equal(2L, check.ExecuteScalar());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Durably_confirmed_abort_remains_resolved_if_terminal_commit_was_interrupted()
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-abort-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var journal = new SqliteTransferJournal(root))
            {
                var row = new TransferRecord(Guid.NewGuid(), 0, Guid.NewGuid(), 1, "target", "https://storage.example.test", "bucket", "key", root, true,
                    TransferState.Pending, Total: 1, Sha256: Convert.ToBase64String(new byte[32]));
                await journal.SaveAsync(row, Token);
                await journal.SaveAsync(row.Next(TransferState.Running) with { Multipart = new(new(MultipartPlan.MinimumPart, 1), MultipartPhase.Aborted, "owned-id") }, Token);
            }
            await using var reopened = new SqliteTransferJournal(root); var loaded = Assert.Single(await reopened.ReadAsync(Token));
            Assert.Equal(TransferState.Cancelled, loaded.State); Assert.True(loaded.Resolved); Assert.False(loaded.CanAbortMultipart);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Transaction_worker()
    {
        var root = Environment.GetEnvironmentVariable("BUCKETBUS_MULTIPART_TRANSACTION_DIR"); var stage = Environment.GetEnvironmentVariable("BUCKETBUS_MULTIPART_TRANSACTION_STAGE");
        if (root is null || stage is null) return;
        await using var journal = new SqliteTransferJournal(root);
        var hash = Convert.ToBase64String(new byte[32]);
        var row = new TransferRecord(Guid.NewGuid(), 0, Guid.NewGuid(), 1, "target", "https://storage.example.test", "bucket", "é//é", root, true,
            TransferState.Pending, Total: MultipartPlan.MinimumPart, Sha256: hash, SourceIdentity: "source");
        await journal.SaveAsync(row, Token);
        var state = new MultipartUpload(new(MultipartPlan.MinimumPart, 1), MultipartPhase.Creating);
        row = row.Next(TransferState.Running) with { Multipart = state, CleanupRequired = true }; await journal.SaveAsync(row, Token);
        if (stage != "upload-id")
        {
            state = state with { Phase = MultipartPhase.SendingPart, UploadId = "owned-id", PendingPart = 1 };
            row = row.Next(TransferState.Running) with { Multipart = state }; await journal.SaveAsync(row, Token);
        }
        var acknowledged = state with { Phase = MultipartPhase.Uploading, PendingPart = 0, UploadId = "owned-id", Parts = [new(1, MultipartPlan.MinimumPart, "opaque", hash)] };
        if (stage == "verified")
        {
            row = row.Next(TransferState.Running) with { Multipart = acknowledged with { Phase = MultipartPhase.Verifying } }; await journal.SaveAsync(row, Token);
        }
        journal.BeforeSaveCommit = _ => { File.WriteAllText(Path.Combine(root, "ready"), "ready"); Thread.Sleep(Timeout.Infinite); };
        row = row.Next(stage == "verified" ? TransferState.Completed : TransferState.Running) with
        { Multipart = stage == "upload-id" ? state with { Phase = MultipartPhase.Uploading, UploadId = "owned-id" } : acknowledged with { Phase = stage == "verified" ? MultipartPhase.Verified : MultipartPhase.Uploading }, CleanupRequired = stage != "verified" };
        await journal.SaveAsync(row, Token);
    }
    [Fact]
    public async Task Schema_two_without_multipart_payload_preserves_queued_history()
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-legacy-multipart-" + Guid.NewGuid().ToString("N"));
        try
        {
            TransferRecord row;
            await using (var journal = new SqliteTransferJournal(root))
                row = await journal.EnqueueAsync(new(Guid.NewGuid(), 0, Guid.NewGuid(), 1, "target", "https://storage.example.test", "bucket", "key", root, true,
                    TransferState.Queued, Total: 1, Sha256: Convert.ToBase64String(new byte[32])), Token);
            using (var db = new SqliteConnection($"Data Source={root}/transfers.db;Pooling=False"))
            {
                db.Open(); var json = JsonSerializer.SerializeToNode(row)!.AsObject(); json.Remove("Multipart");
                using var command = db.CreateCommand(); command.CommandText = "UPDATE transfers SET payload=$payload"; command.Parameters.AddWithValue("$payload", json.ToJsonString()); command.ExecuteNonQuery();
            }
            await using var reopened = new SqliteTransferJournal(root); var loaded = Assert.Single(await reopened.ReadAsync(Token));
            Assert.Null(loaded.Multipart); Assert.Equal(row.Id, loaded.Id); Assert.Equal(row.QueueOrder, loaded.QueueOrder); Assert.Equal(TransferState.Queued, loaded.State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
