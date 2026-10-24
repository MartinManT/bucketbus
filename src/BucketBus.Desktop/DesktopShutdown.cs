namespace BucketBus.Desktop;

/// <summary>Cancel on the UI thread, then await disposal without blocking its message loop.</summary>
public static class DesktopShutdown
{
    public static async Task<bool> StopAsync(Action cancel, Func<ValueTask> dispose, TimeSpan timeout)
    {
        var cancellationSucceeded = true;
        try { cancel(); }
        catch (Exception) { cancellationSucceeded = false; }
        var cleanup = DisposeSafelyAsync(dispose);
        try { return await cleanup.WaitAsync(timeout) && cancellationSucceeded; }
        catch (TimeoutException) { return false; }
        // DisposeSafelyAsync continues observing cleanup after a timeout; no false completion claim.
    }

    private static async Task<bool> DisposeSafelyAsync(Func<ValueTask> dispose)
    {
        try { await dispose(); return true; }
        catch (Exception) { return false; } // Never surface native/provider details during exit.
    }
}
