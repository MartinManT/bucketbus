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
    public Task<TransferRecord> EnqueueAsync(TransferRecord record, CancellationToken token)
    {
        record = record with { QueueOrder = Records.Values.Select(r => r.QueueOrder ?? 0).DefaultIfEmpty().Max() + 1 };
        record.Validate(); Records[record.Id] = record; return Task.FromResult(record);
    }
    public Task<TransferRecord?> ClaimQueuedAsync(Guid id, CancellationToken token)
    {
        if (!Records.TryGetValue(id, out var record) || record.State != TransferState.Queued) return Task.FromResult<TransferRecord?>(null);
        record = record.Next(TransferState.Pending); Records[id] = record; return Task.FromResult<TransferRecord?>(record);
    }
    public Task<bool> RemoveQueuedAsync(Guid id, CancellationToken token)
    {
        if (!Records.TryGetValue(id, out var record) || record.State != TransferState.Queued) return Task.FromResult(false);
        return Task.FromResult(Records.Remove(id));
    }
    public Task DismissAsync(Guid id, CancellationToken token)
    {
        if (!Records[id].Resolved) throw new JournalException();
        Records.Remove(id); return Task.CompletedTask;
    }
}

internal static class TransferTestComposition
{
    public static BucketBus.Desktop.ViewModels.TransferViewModel Create(ProfileManager profiles, ITransferFiles files, IObjectTransferFactory factory,
        ITransferJournal journal, BucketBus.Desktop.ViewModels.LocalBrowserViewModel local, BucketBus.Desktop.ViewModels.RemoteBrowserViewModel remote,
        BucketBus.Desktop.ViewModels.DisplayOptions? display = null)
    {
        var coordinator = new TransferCoordinator(profiles, files, factory, journal);
        return new(coordinator, new TransferQueue(profiles, files, coordinator, journal), local, remote, display);
    }
}
