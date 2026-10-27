using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BucketBus.Core;
using BucketBus.Platform;
using Microsoft.Data.Sqlite;

namespace BucketBus.Platform.Tests;

public sealed class QueueJournalMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bucketbus-queue-migration-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Database => Path.Combine(_root, "transfers.db");
    [Fact]
    public async Task Legacy_history_and_uncertainty_migrate_without_requeueing_or_losing_safety_fields()
    {
        var rows = new[]
        {
            Record(TransferState.Completed), Record(TransferState.Failed), Record(TransferState.Cancelled),
            Record(TransferState.Pending), Record(TransferState.Running) with { Upload = true },
            Record(TransferState.Running), Record(TransferState.Running) with { Phase = TransferPhase.Committing },
            Record(TransferState.OutcomeUnknown) with { Upload = true }, Record(TransferState.RequiresReconciliation)
        };
        CreateLegacy(rows); await using var journal = new SqliteTransferJournal(_root);
        var migrated = await journal.ReadAsync(Token); Assert.Equal(rows.Length, migrated.Count);
        foreach (var old in rows)
        {
            var now = migrated.Single(r => r.Id == old.Id);
            Assert.Equal(old.ProfileId, now.ProfileId); Assert.Equal(old.ProfileRevision, now.ProfileRevision);
            Assert.Equal(old.Target, now.Target); Assert.Equal(old.Endpoint, now.Endpoint); Assert.Equal(old.Key, now.Key);
            Assert.Equal(old.LocalPath, now.LocalPath); Assert.Equal(old.Sha256, now.Sha256); Assert.Equal(old.Total, now.Total);
            Assert.Equal(old.Temporary, now.Temporary); Assert.Equal(old.CleanupRequired, now.CleanupRequired); Assert.Null(now.QueueOrder);
            Assert.Equal(old.State switch
            {
                TransferState.Pending => TransferState.Interrupted,
                TransferState.Running when old.Upload => TransferState.OutcomeUnknown,
                TransferState.Running when old.Phase == TransferPhase.Committing => TransferState.RequiresReconciliation,
                TransferState.Running => TransferState.Interrupted, _ => old.State
            }, now.State);
        }
        Assert.DoesNotContain(migrated, r => r.State == TransferState.Queued); Assert.Equal(2L, Scalar("PRAGMA user_version"));
    }
    [Fact]
    public async Task Migration_failure_rolls_back_schema_and_preserves_legacy_rows()
    {
        var original = Record(TransferState.OutcomeUnknown) with { Upload = true }; CreateLegacy([original]);
        await using (var failed = new SqliteTransferJournal(_root) { BeforeMigrationCommit = () => throw new IOException("injected before migration commit") })
            await Assert.ThrowsAsync<JournalException>(() => failed.ReadAsync(Token));
        Assert.Equal(1L, Scalar("PRAGMA user_version")); Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM transfers"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM pragma_table_info('transfers') WHERE name='sequence'"));
        await using var retry = new SqliteTransferJournal(_root);
        Assert.Equal(original.Id, Assert.Single(await retry.ReadAsync(Token)).Id); Assert.Equal(2L, Scalar("PRAGMA user_version"));
    }
    [Fact]
    public async Task Queued_survives_restart_but_claimed_pending_is_interrupted_and_not_removable()
    {
        TransferRecord first, second;
        await using (var journal = new SqliteTransferJournal(_root))
        {
            first = await journal.EnqueueAsync(Record(TransferState.Queued) with { Revision = 0, Temporary = null, Bytes = 0, CleanupRequired = false, Sha256 = null, Total = null }, Token);
            second = await journal.EnqueueAsync(Record(TransferState.Queued) with { Revision = 0, Temporary = null, Bytes = 0, CleanupRequired = false, Sha256 = null, Total = null }, Token);
            Assert.Equal(1, first.QueueOrder); Assert.Equal(2, second.QueueOrder);
            Assert.NotNull(await journal.ClaimQueuedAsync(first.Id, Token));
        }
        await using var restarted = new SqliteTransferJournal(_root);
        var rows = await restarted.ReadAsync(Token);
        Assert.Equal(TransferState.Interrupted, rows.Single(r => r.Id == first.Id).State);
        Assert.Equal(TransferState.Queued, rows.Single(r => r.Id == second.Id).State);
        Assert.False(await restarted.RemoveQueuedAsync(first.Id, Token)); Assert.Null(await restarted.ClaimQueuedAsync(first.Id, Token));
        Assert.True(await restarted.RemoveQueuedAsync(second.Id, Token)); Assert.Single(await restarted.ReadAsync(Token));
    }
    [Fact]
    public async Task Concurrent_owner_cannot_claim_or_enqueue_and_first_owner_remains_valid()
    {
        await using var first = new SqliteTransferJournal(_root);
        var record = await first.EnqueueAsync(Record(TransferState.Queued) with { Revision = 0, Temporary = null, Bytes = 0, CleanupRequired = false, Sha256 = null, Total = null }, Token);
        await using var second = new SqliteTransferJournal(_root);
        await Assert.ThrowsAsync<JournalException>(() => second.ClaimQueuedAsync(record.Id, Token));
        Assert.NotNull(await first.ClaimQueuedAsync(record.Id, Token));
    }
    [Fact]
    public async Task Process_kill_in_migration_preserves_schema_one_and_can_migrate_again()
    {
        var original = Record(TransferState.OutcomeUnknown) with { Upload = true }; CreateLegacy([original]);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(QueueJournalMigrationTests).Assembly.Location); start.ArgumentList.Add("-method"); start.ArgumentList.Add("*QueueJournalMigrationTests.Migration_crash_worker");
        start.Environment["BUCKETBUS_MIGRATION_CRASH_DIR"] = _root;
        using var child = Process.Start(start)!; var output = child.StandardOutput.ReadToEndAsync(Token); var error = child.StandardError.ReadToEndAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(_root, "ready"))) { Assert.False(child.HasExited, "Migration worker exited before DDL barrier"); await Task.Delay(20, deadline.Token); }
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(Token); await output; await error; }
        Assert.Equal(1L, Scalar("PRAGMA user_version"));
        await using var journal = new SqliteTransferJournal(_root);
        Assert.Equal(original.Id, Assert.Single(await journal.ReadAsync(Token)).Id); Assert.Equal(2L, Scalar("PRAGMA user_version"));
    }
    [Fact]
    public async Task Migration_crash_worker()
    {
        var root = Environment.GetEnvironmentVariable("BUCKETBUS_MIGRATION_CRASH_DIR"); if (root is null) return;
        await using var journal = new SqliteTransferJournal(root)
        {
            BeforeMigrationCommit = () => { File.WriteAllText(Path.Combine(root, "ready"), "ready"); Thread.Sleep(Timeout.Infinite); }
        };
        await journal.ReadAsync(Token);
    }
    private TransferRecord Record(TransferState state) => new(Guid.NewGuid(), 5, Guid.NewGuid(), 4, "synthetic-target", "https://storage.example.test",
        "test-bucket", "exact//é é %+.bin", _root, false, state, Bytes: 7, Total: 7, Sha256: Convert.ToBase64String(new byte[32]),
        Temporary: new("linux", ".bucketbus-" + Guid.NewGuid().ToString("N") + ".tmp", "parent", "container", "payload"), CleanupRequired: true,
        Created: DateTimeOffset.UnixEpoch, Updated: DateTimeOffset.UnixEpoch);
    private void CreateLegacy(IEnumerable<TransferRecord> records)
    {
        Directory.CreateDirectory(_root); using var db = new SqliteConnection($"Data Source={Database};Pooling=False"); db.Open();
        using var create = db.CreateCommand(); create.CommandText = "CREATE TABLE transfers(id TEXT PRIMARY KEY NOT NULL, revision INTEGER NOT NULL, state INTEGER NOT NULL, payload TEXT NOT NULL); PRAGMA user_version=1; PRAGMA application_id=1111643210;";
        create.ExecuteNonQuery();
        foreach (var record in records)
        {
            var payload = JsonSerializer.SerializeToNode(record)!.AsObject(); payload.Remove("QueueOrder"); payload.Remove("SourceIdentity");
            using var insert = db.CreateCommand(); insert.CommandText = "INSERT INTO transfers VALUES($id,$revision,$state,$payload)";
            insert.Parameters.AddWithValue("$id", record.Id.ToString("N")); insert.Parameters.AddWithValue("$revision", record.Revision);
            insert.Parameters.AddWithValue("$state", (int)record.State); insert.Parameters.AddWithValue("$payload", payload.ToJsonString()); insert.ExecuteNonQuery();
        }
    }
    private long Scalar(string sql)
    {
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
