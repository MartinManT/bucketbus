using BucketBus.Core;

namespace BucketBus.Core.Tests;

public sealed class TransferRecordTests
{
    private static TransferRecord Record(bool upload = true) => new(Guid.NewGuid(), 1, Guid.NewGuid(), 1, "target",
        "https://storage.example.test", "bucket", "key", Path.GetFullPath("source"), upload, TransferState.OutcomeUnknown,
        Total: 1, Sha256: upload ? Convert.ToBase64String(new byte[32]) : null);
    [Theory]
    [InlineData(TransferState.Running)] [InlineData(TransferState.Pending)]
    [InlineData(TransferState.Completed)] [InlineData(TransferState.Failed)] [InlineData(TransferState.Cancelled)]
    public void Unknown_write_cannot_be_restarted_or_silently_resolved(TransferState next)
    {
        var record = Record(); Assert.False(TransferRecord.Allows(record, record.Next(next)));
        Assert.True(TransferRecord.Allows(record, record.Next(TransferState.RequiresReconciliation)));
        Assert.False(record.Resolved); Assert.False(record.CanRestart); Assert.True(record.CanReconcile);
    }
    [Fact]
    public void Operation_identity_target_and_verification_evidence_are_immutable()
    {
        var record = Record(); var next = record.Next(TransferState.RequiresReconciliation);
        foreach (var changed in new[] { next with { Id = Guid.NewGuid() }, next with { Target = "different" }, next with { ProfileRevision = 2 },
            next with { Bucket = "different" }, next with { Key = "different" }, next with { Sha256 = null }, next with { LocalPath = Path.GetFullPath("different") } })
            Assert.False(TransferRecord.Allows(record, changed));
    }
    [Fact]
    public void Missing_temporary_identity_disables_download_restart_and_cleanup_obligations_cannot_be_dismissed()
    {
        var record = Record(false) with { State = TransferState.Interrupted };
        Assert.False(record.CanRestart);
        Assert.False((record with { State = TransferState.Completed, CleanupRequired = true }).Resolved);
    }
    [Theory]
    [InlineData("https://user:secret@storage.example.test")]
    [InlineData("https://storage.example.test?X-Amz-Signature=synthetic")]
    [InlineData("http://storage.example.test")]
    public void Journal_rejects_credential_bearing_or_unsafe_endpoint_forms(string endpoint)
    { Assert.Throws<JournalException>(() => (Record() with { Endpoint = endpoint }).Validate()); }
}
