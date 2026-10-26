using System.Text.Json;
using BucketBus.Core;
using Microsoft.Data.Sqlite;

namespace BucketBus.Platform;

/// <summary>One owner, serialized short transactions off the UI thread. Never recreates an existing database.</summary>
public sealed class SqliteTransferJournal(string directory) : ITransferJournal, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private FileStream? _instance;
    private bool _faulted, _disposed;
    private const int ApplicationId = 0x4242544a;
    public static string DefaultDirectory => Path.Combine(JsonProfileStore.DefaultDirectory, "transfers");
    public Task<IReadOnlyList<TransferRecord>> ReadAsync(CancellationToken token) => Work<IReadOnlyList<TransferRecord>>(() => ReadAll(), token);
    public Task SaveAsync(TransferRecord record, CancellationToken token) => Work(() =>
    {
        record.Validate();
        using var transaction = _connection!.BeginTransaction();
        var old = Find(record.Id, transaction);
        if (old is null ? record.Revision != 0 || record.State != TransferState.Pending : !TransferRecord.Allows(old, record))
            throw new JournalException();
        Write(record, transaction, old is null);
        transaction.Commit(); return true;
    }, token);
    public Task DismissAsync(Guid id, CancellationToken token) => Work(() =>
    {
        using var transaction = _connection!.BeginTransaction();
        var record = Find(id, transaction);
        if (record is null || !record.Resolved) throw new JournalException();
        using var command = Command("DELETE FROM transfers WHERE id=$id", transaction);
        command.Parameters.AddWithValue("$id", id.ToString("N")); command.ExecuteNonQuery();
        transaction.Commit(); return true;
    }, token);
    private async Task<T> Work<T>(Func<T> action, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (_faulted || _disposed) throw new JournalException();
                try { Initialize(); return action(); }
                catch { _faulted = true; throw new JournalException(); }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private void Initialize()
    {
        if (_connection is not null) return;
        if (!Path.IsPathFullyQualified(directory)) throw new JournalException();
        // Reject reparse ancestors. The journal belongs on a local, per-user filesystem.
        for (var path = Path.GetFullPath(directory); path is not null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new JournalException();
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else Directory.CreateDirectory(directory); // Inherits current user's LocalApplicationData ACL.
        var lockPath = Path.Combine(directory, "owner.lock");
        RejectLink(lockPath);
        _instance = new FileStream(lockPath, Options(FileMode.OpenOrCreate));
        var database = Path.Combine(directory, "transfers.db");
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) RejectLink(database + suffix);
        var existed = File.Exists(database);
        if (!existed)
        {
            // An orphan WAL without its DB is evidence, never an invitation to create a new journal.
            if (File.Exists(database + "-wal") || File.Exists(database + "-shm") || File.Exists(database + "-journal")) throw new JournalException();
            using var created = new FileStream(database, Options(FileMode.CreateNew));
        }
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(database, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
        _connection.Open();
        if (existed && (ScalarLong("PRAGMA user_version") != 1 || ScalarLong("PRAGMA application_id") != ApplicationId)) throw new JournalException();
        if (!Equals(Scalar("PRAGMA quick_check"), "ok")) throw new JournalException();
        if (!Equals(Scalar("PRAGMA journal_mode=WAL"), "wal")) throw new JournalException();
        Execute("PRAGMA synchronous=FULL; PRAGMA trusted_schema=OFF;");
        if (ScalarLong("PRAGMA synchronous") != 2) throw new JournalException();
        if (!existed)
        {
            using var tx = _connection.BeginTransaction();
            using var create = Command($"CREATE TABLE transfers (id TEXT PRIMARY KEY NOT NULL, revision INTEGER NOT NULL, state INTEGER NOT NULL, payload TEXT NOT NULL); PRAGMA user_version=1; PRAGMA application_id={ApplicationId};", tx);
            create.ExecuteNonQuery(); tx.Commit();
        }
        // Recover all active records atomically. No credentials, filesystem cleanup or S3 access here.
        var rows = ReadAll();
        using var recovery = _connection.BeginTransaction();
        foreach (var row in rows)
        {
            TransferState? state = row.State switch
            {
                TransferState.Pending => TransferState.Interrupted,
                TransferState.Running when row.Upload => TransferState.OutcomeUnknown,
                TransferState.Running when row.Phase == TransferPhase.Committing => TransferState.RequiresReconciliation,
                TransferState.Running => TransferState.Interrupted,
                _ => null
            };
            if (state is { } value) Write(row.Next(value), recovery, false);
        }
        recovery.Commit();
    }
    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new JournalException();
    }
    private List<TransferRecord> ReadAll()
    {
        using var command = Command("SELECT id, revision, state, payload FROM transfers ORDER BY rowid DESC");
        using var reader = command.ExecuteReader(); var rows = new List<TransferRecord>();
        while (reader.Read()) rows.Add(Decode(reader));
        return rows;
    }
    private TransferRecord? Find(Guid id, SqliteTransaction transaction)
    {
        using var command = Command("SELECT id, revision, state, payload FROM transfers WHERE id=$id", transaction);
        command.Parameters.AddWithValue("$id", id.ToString("N")); using var reader = command.ExecuteReader();
        return reader.Read() ? Decode(reader) : null;
    }
    private static TransferRecord Decode(SqliteDataReader reader)
    {
        var record = JsonSerializer.Deserialize<TransferRecord>(reader.GetString(3)) ?? throw new JournalException();
        record.Validate();
        if (record.Id.ToString("N") != reader.GetString(0) || record.Revision != reader.GetInt64(1) || (int)record.State != reader.GetInt32(2)) throw new JournalException();
        return record;
    }
    private void Write(TransferRecord record, SqliteTransaction transaction, bool insert)
    {
        using var command = Command(insert ? "INSERT INTO transfers VALUES ($id,$revision,$state,$payload)"
            : "UPDATE transfers SET revision=$revision,state=$state,payload=$payload WHERE id=$id AND revision=$previous", transaction);
        command.Parameters.AddWithValue("$id", record.Id.ToString("N"));
        command.Parameters.AddWithValue("$revision", record.Revision); command.Parameters.AddWithValue("$state", (int)record.State);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record));
        if (!insert) command.Parameters.AddWithValue("$previous", record.Revision - 1);
        if (command.ExecuteNonQuery() != 1) throw new JournalException();
    }
    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    { var command = _connection!.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; return command; }
    private object? Scalar(string sql) { using var command = Command(sql); return command.ExecuteScalar(); }
    private long ScalarLong(string sql) => Convert.ToInt64(Scalar(sql), System.Globalization.CultureInfo.InvariantCulture);
    private void Execute(string sql) { using var command = Command(sql); command.ExecuteNonQuery(); }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; _connection?.Dispose(); _connection = null; _instance?.Dispose(); _instance = null; }
        finally { _gate.Release(); }
    }
}
