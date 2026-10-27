using System.Text.Json;
using System.Text.Json.Nodes;
using BucketBus.Core;
using BucketBus.Platform;
using Microsoft.Data.Sqlite;

namespace BucketBus.Platform.Tests;

public sealed class RetryJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bucketbus-retry-journal-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Existing_schema_two_payload_without_retry_fields_loads_without_schema_change()
    {
        Directory.CreateDirectory(_root);
        var record = new TransferRecord(Guid.NewGuid(), 2, Guid.NewGuid(), 1, "target", "https://storage.example.test", "bucket", "exact//é é.bin",
            _root, false, TransferState.Running, Total: 100, Temporary: new("linux", "owned", "parent", "container", "file"));
        var payload = JsonSerializer.SerializeToNode(record)!.AsObject();
        foreach (var field in new[] { "Attempt", "MaximumAttempts", "RetryError", "RetryAt", "RetryLimitReached" }) payload.Remove(field);
        using (var db = new SqliteConnection($"Data Source={_root}/transfers.db;Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE transfers(id TEXT PRIMARY KEY NOT NULL,revision INTEGER NOT NULL,state INTEGER NOT NULL,payload TEXT NOT NULL,sequence INTEGER UNIQUE); PRAGMA user_version=2; PRAGMA application_id=1111643210; INSERT INTO transfers VALUES($id,2,$state,$payload,NULL);";
            command.Parameters.AddWithValue("$id", record.Id.ToString("N")); command.Parameters.AddWithValue("$state", (int)record.State); command.Parameters.AddWithValue("$payload", payload.ToJsonString()); command.ExecuteNonQuery();
        }
        await using var journal = new SqliteTransferJournal(_root); var loaded = Assert.Single(await journal.ReadAsync(Token));
        Assert.Equal(record.Id, loaded.Id); Assert.Equal(TransferState.Interrupted, loaded.State); Assert.Equal(0, loaded.Attempt); Assert.Equal(1, loaded.MaximumAttempts);
        Assert.Equal(record.Temporary, loaded.Temporary); Assert.Equal(record.Key, loaded.Key);
        using var check = new SqliteConnection($"Data Source={_root}/transfers.db;Pooling=False"); check.Open(); using var query = check.CreateCommand(); query.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, query.ExecuteScalar()); query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('transfers')"; Assert.Equal(5L, query.ExecuteScalar());
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
