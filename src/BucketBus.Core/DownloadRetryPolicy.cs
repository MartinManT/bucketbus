namespace BucketBus.Core;

/// <summary>A fixed, download-only budget. SDK retries remain disabled for every physical request.</summary>
public sealed class DownloadRetryPolicy
{
    public const int TotalAttempts = 3;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Func<int> _jitter;
    public DownloadRetryPolicy() : this((delay, token) => Task.Delay(delay, token), () => Random.Shared.Next(251)) { }
    internal DownloadRetryPolicy(Func<TimeSpan, CancellationToken, Task> wait, Func<int>? jitter = null)
    { _wait = wait; _jitter = jitter ?? (() => 0); }
    public static bool IsEligible(TransferError error) => error is TransferError.ConnectionInterrupted or TransferError.TruncatedResponse
        or TransferError.Timeout or TransferError.RateLimited or TransferError.TransientService;
    public TimeSpan DelayAfter(int completedAttempt)
    {
        if (completedAttempt is < 1 or >= TotalAttempts) throw new ArgumentOutOfRangeException(nameof(completedAttempt));
        return TimeSpan.FromMilliseconds((1000 << (completedAttempt - 1)) + Math.Clamp(_jitter(), 0, 250));
    }
    internal Task WaitAsync(TimeSpan delay, CancellationToken token) => _wait(delay, token);
}
