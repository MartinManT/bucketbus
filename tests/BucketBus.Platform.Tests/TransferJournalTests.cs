using System.Diagnostics;
using System.Text.Json;
using BucketBus.Core;
using BucketBus.Platform;
using Microsoft.Data.Sqlite;

namespace BucketBus.Platform.Tests;

public sealed class TransferJournalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static TransferRecord Record(string root, bool upload = false) => new(Guid.NewGuid(), 0, Guid.NewGuid(), 1,
        "synthetic-target", "https://storage.example.test", "fixture", "exact//é.bin", root, upload, TransferState.Pending,
        Created: DateTimeOffset.UtcNow, Updated: DateTimeOffset.UtcNow);

    [Fact]
    public async Task Transactions_roundtrip_exact_names_progress_and_recover_without_credentials()
    {
        using var fixture = new JournalDirectory(); var record = Record(fixture.Path, true);
        await using (var journal = new SqliteTransferJournal(fixture.Path))
        {
            await journal.SaveAsync(record, Token);
            record = record.Next(TransferState.Running) with { Bytes = 12, Total = 99, Phase = TransferPhase.Transferring, Sha256 = Convert.ToBase64String(new byte[32]) };
            await journal.SaveAsync(record, Token);
            Assert.Equal(record, Assert.Single(await journal.ReadAsync(Token)));
        }
        await using var reopened = new SqliteTransferJournal(fixture.Path);
        var recovered = Assert.Single(await reopened.ReadAsync(Token));
        Assert.Equal(TransferState.OutcomeUnknown, recovered.State); Assert.Equal(record.Id, recovered.Id);
        Assert.Equal("exact//é.bin", recovered.Key); Assert.Equal(12, recovered.Bytes);
        Assert.DoesNotContain("credential", JsonSerializer.Serialize(recovered), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(recovered), StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<JournalException>(() => reopened.DismissAsync(record.Id, Token));
    }
    [Fact]
    public async Task Stale_transaction_preserves_previous_state_and_blocks_owner_until_restart()
    {
        using var fixture = new JournalDirectory(); var record = Record(fixture.Path);
        await using (var journal = new SqliteTransferJournal(fixture.Path))
        {
            await journal.SaveAsync(record, Token);
            await journal.SaveAsync(record.Next(TransferState.Running), Token);
            await Assert.ThrowsAsync<JournalException>(() => journal.SaveAsync(record.Next(TransferState.Completed), Token));
        }
        await using var reopened = new SqliteTransferJournal(fixture.Path);
        Assert.Equal(TransferState.Interrupted, Assert.Single(await reopened.ReadAsync(Token)).State);
    }
    [Fact]
    public async Task Resolved_history_dismissal_is_local_only()
    {
        using var fixture = new JournalDirectory(); await using var journal = new SqliteTransferJournal(fixture.Path);
        var record = Record(fixture.Path); await journal.SaveAsync(record, Token);
        record = record.Next(TransferState.Failed); await journal.SaveAsync(record, Token);
        await journal.DismissAsync(record.Id, Token); Assert.Empty(await journal.ReadAsync(Token));
    }
    [Fact]
    public async Task Concurrent_owner_is_denied_and_first_owner_is_unchanged()
    {
        using var fixture = new JournalDirectory(); await using var first = new SqliteTransferJournal(fixture.Path);
        await first.SaveAsync(Record(fixture.Path), Token);
        await using var second = new SqliteTransferJournal(fixture.Path);
        await Assert.ThrowsAsync<JournalException>(() => second.ReadAsync(Token));
        Assert.Single(await first.ReadAsync(Token));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Corrupt_or_unknown_schema_is_preserved(bool schema)
    {
        using var fixture = new JournalDirectory(); var path = System.IO.Path.Combine(fixture.Path, "transfers.db");
        if (schema)
        {
            await using (var journal = new SqliteTransferJournal(fixture.Path)) await journal.SaveAsync(Record(fixture.Path), Token);
            using var db = new SqliteConnection($"Data Source={path};Pooling=False"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version=99"; cmd.ExecuteNonQuery();
        }
        else await File.WriteAllTextAsync(path, "synthetic corrupt database", Token);
        var before = await File.ReadAllBytesAsync(path, Token);
        await using var reopen = new SqliteTransferJournal(fixture.Path);
        await Assert.ThrowsAsync<JournalException>(() => reopen.ReadAsync(Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
    }
    [Fact]
    public async Task Unwritable_location_fails_closed()
    {
        using var fixture = new JournalDirectory(); var path = System.IO.Path.Combine(fixture.Path, "not-directory");
        await File.WriteAllTextAsync(path, "fixture", Token);
        await using var journal = new SqliteTransferJournal(path);
        await Assert.ThrowsAsync<JournalException>(() => journal.SaveAsync(Record(fixture.Path), Token));
        Assert.Equal("fixture", await File.ReadAllTextAsync(path, Token));
    }
    [Fact]
    public async Task Native_sqlite_uses_wal_full_and_private_files()
    {
        using var fixture = new JournalDirectory(); await using var journal = new SqliteTransferJournal(fixture.Path);
        await journal.SaveAsync(Record(fixture.Path), Token);
        using var db = new SqliteConnection($"Data Source={fixture.Path}/transfers.db;Pooling=False"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode"; Assert.Equal("wal", cmd.ExecuteScalar());
        cmd.CommandText = "SELECT sqlite_version()"; Assert.NotEmpty((string)cmd.ExecuteScalar()!);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fixture.Path));
            foreach (var file in Directory.EnumerateFiles(fixture.Path))
                Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(file) & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite));
        }
    }
    [Theory]
    [InlineData("transaction")] [InlineData("download")] [InlineData("commit")]
    public async Task Process_kill_preserves_committed_intent_and_never_restarts(string mode)
    {
        using var fixture = new JournalDirectory();
        await KillWorker(fixture.Path, mode);
        await using var journal = new SqliteTransferJournal(fixture.Path);
        var recovered = Assert.Single(await journal.ReadAsync(Token));
        Assert.Equal(mode == "commit" ? TransferState.RequiresReconciliation : TransferState.Interrupted, recovered.State);
        Assert.Equal(0, recovered.Bytes); // Uncommitted transaction must not leak its progress.
        if (mode != "transaction")
        {
            Assert.NotNull(recovered.Temporary);
            var files = new TransferFiles();
            if (mode == "commit")
            {
                var error = await Assert.ThrowsAsync<TransferException>(() => files.RecoverDestinationAsync(recovered, Token));
                Assert.Equal(TransferError.Conflict, error.Error);
                Assert.Equal("fixture", await File.ReadAllTextAsync(System.IO.Path.Combine(fixture.Path, "é.bin"), Token));
            }
            else
            {
                Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "é.bin")));
                await files.RecoverDestinationAsync(recovered, Token); // Only explicit recovery removes verified owned temp.
                await files.RecoverDestinationAsync(recovered, Token); // Missing already-cleaned temp is safe.
                Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path, ".bucketbus-*"));
            }
        }
    }
    [Fact]
    public async Task Recovery_rejects_changed_parent_or_substituted_temp_and_preserves_unknown_files()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new JournalDirectory(); await KillWorker(fixture.Path, "download");
        await using var journal = new SqliteTransferJournal(fixture.Path);
        var record = Assert.Single(await journal.ReadAsync(Token)); var files = new TransferFiles();
        await Assert.ThrowsAsync<TransferException>(() => files.RecoverDestinationAsync(record with { Temporary = record.Temporary! with { ParentIdentity = "changed" } }, Token));
        var payload = System.IO.Path.Combine(fixture.Path, record.Temporary!.Name, "payload.part");
        File.Move(payload, payload + ".original"); await File.WriteAllTextAsync(payload, "not owned", Token);
        await Assert.ThrowsAsync<TransferException>(() => files.RecoverDestinationAsync(record, Token));
        Assert.Equal("not owned", await File.ReadAllTextAsync(payload, Token));
    }
    [Fact]
    public async Task Crash_worker()
    {
        var root = Environment.GetEnvironmentVariable("BUCKETBUS_JOURNAL_CRASH_DIR");
        var mode = Environment.GetEnvironmentVariable("BUCKETBUS_JOURNAL_CRASH_MODE");
        if (root is null || mode is null) return;
        await using var journal = new SqliteTransferJournal(root);
        var record = Record(root); await journal.SaveAsync(record, Token);
        record = record.Next(TransferState.Running); await journal.SaveAsync(record, Token);
        if (mode == "transaction")
        {
            using var db = new SqliteConnection($"Data Source={root}/transfers.db;Pooling=False"); db.Open();
            using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "UPDATE transfers SET payload=$payload,revision=$revision";
            var pending = record.Next(TransferState.Running) with { Bytes = 999 };
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(pending)); cmd.Parameters.AddWithValue("$revision", pending.Revision);
            cmd.ExecuteNonQuery(); await WaitForKill(root); tx.Commit();
        }
        else
        {
            await using var destination = await new TransferFiles().CreateDestinationAsync(root, "é.bin", Token);
            record = record.Next(TransferState.Running) with { Temporary = destination.Receipt, Phase = TransferPhase.Transferring };
            await journal.SaveAsync(record, Token);
            await destination.Content.WriteAsync("fixture"u8.ToArray(), Token);
            if (mode == "commit")
            {
                record = record.Next(TransferState.Running) with { Phase = TransferPhase.Committing };
                await journal.SaveAsync(record, Token); await destination.CommitAsync(Token);
            }
            await WaitForKill(root);
        }
    }
    private static async Task WaitForKill(string root)
    { await File.WriteAllTextAsync(System.IO.Path.Combine(root, "ready"), "ready"); await Task.Delay(Timeout.InfiniteTimeSpan, Token); }
    private static async Task KillWorker(string root, string mode)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(TransferJournalTests).Assembly.Location); start.ArgumentList.Add("-method");
        start.ArgumentList.Add("*TransferJournalTests.Crash_worker");
        start.Environment["BUCKETBUS_JOURNAL_CRASH_DIR"] = root; start.Environment["BUCKETBUS_JOURNAL_CRASH_MODE"] = mode;
        using var child = Process.Start(start)!; var output = child.StandardOutput.ReadToEndAsync(Token); var error = child.StandardError.ReadToEndAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (!File.Exists(System.IO.Path.Combine(root, "ready")))
            { Assert.False(child.HasExited, "Crash worker exited before its persistence barrier."); await Task.Delay(20, deadline.Token); }
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(Token); await output; await error; }
    }
    private sealed class JournalDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bucketbus-journal-" + Guid.NewGuid().ToString("N"));
        public JournalDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
