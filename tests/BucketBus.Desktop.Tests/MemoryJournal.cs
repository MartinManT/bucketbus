using BucketBus.Core;
namespace BucketBus.Desktop.Tests;

internal sealed class MemoryJournal : ITransferJournal
{
    public Dictionary<Guid, TransferRecord> Records { get; } = [];
    public int Saves;
    public int FailAt = int.MaxValue;
    public Task<IReadOnlyList<TransferRecord>> ReadAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<TransferRecord>>(Records.Values.ToArray());
    public Task SaveAsync(TransferRecord record, CancellationToken token)
    {
        if (++Saves == FailAt) throw new JournalException();
        record.Validate();
        if (Records.TryGetValue(record.Id, out var old) && !TransferRecord.Allows(old, record)) throw new JournalException();
        Records[record.Id] = record; return Task.CompletedTask;
    }
    public Task DismissAsync(Guid id, CancellationToken token)
    {
        if (!Records[id].Resolved) throw new JournalException();
        Records.Remove(id); return Task.CompletedTask;
    }
}
