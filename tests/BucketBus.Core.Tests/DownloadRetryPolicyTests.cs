using BucketBus.Core;
namespace BucketBus.Core.Tests;

public sealed class DownloadRetryPolicyTests
{
    [Theory]
    [InlineData(TransferError.ConnectionInterrupted)] [InlineData(TransferError.TruncatedResponse)] [InlineData(TransferError.Timeout)]
    [InlineData(TransferError.RateLimited)] [InlineData(TransferError.TransientService)]
    public void Only_explicit_transient_download_errors_are_candidates(TransferError error) => Assert.True(DownloadRetryPolicy.IsEligible(error));
    [Fact]
    public void Backoff_is_bounded_exponential_with_small_jitter_and_fixed_budget()
    {
        var policy = new DownloadRetryPolicy((_, _) => Task.CompletedTask, () => 125);
        Assert.Equal(TimeSpan.FromMilliseconds(1125), policy.DelayAfter(1)); Assert.Equal(TimeSpan.FromMilliseconds(2125), policy.DelayAfter(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayAfter(0)); Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayAfter(3));
        foreach (var error in Enum.GetValues<TransferError>().Where(e => e is not (TransferError.ConnectionInterrupted or TransferError.TruncatedResponse or TransferError.Timeout or TransferError.RateLimited or TransferError.TransientService)))
            Assert.False(DownloadRetryPolicy.IsEligible(error));
    }
    [Fact]
    public void Creation_gap_and_uploads_never_acquire_automatic_restart_rights()
    {
        var record = new TransferRecord(Guid.NewGuid(), 1, Guid.NewGuid(), 1, "target", "https://storage.example.test", "bucket", "file",
            Path.GetFullPath("destination"), false, TransferState.Interrupted, Phase: TransferPhase.DestinationCreating,
            Temporary: new("linux", "old-temp", "parent", "container", "payload"), Attempt: 2, MaximumAttempts: 3);
        Assert.False(record.CanRestart);
        Assert.Throws<JournalException>(() => (record with { Upload = true }).Validate());
    }
}
