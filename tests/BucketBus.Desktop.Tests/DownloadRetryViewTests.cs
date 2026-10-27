using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;
using static BucketBus.Desktop.Tests.DownloadRetryTests;

namespace BucketBus.Desktop.Tests;

public sealed class DownloadRetryViewTests
{
    [AvaloniaFact]
    public async Task Backoff_and_final_attempt_are_visible_while_queue_stays_busy()
    {
        await using var f = new RetryFixture(); var ready = new TaskCompletionSource(); var release = new TaskCompletionSource();
        f.Factory.Send = (_, number, _) => number == 1 ? throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture") : Task.FromResult(Response(Data));
        f.OnDelay = async (_, ct) => { ready.SetResult(); await release.Task.WaitAsync(ct); };
        await f.Add();
        using var local = new LocalBrowserViewModel(new LocalFixture());
        using var vm = new TransferViewModel(f.Coordinator, f.Queue, local, f.Profiles.Browser);
        await vm.InitializeAsync(); var run = vm.StartQueueCommand.ExecuteAsync(null);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsBusy); Assert.Contains("retrying in", vm.Status); Assert.False(vm.StartQueueCommand.CanExecute(null)); Assert.True(vm.PauseQueueCommand.CanExecute(null));
            Assert.Contains("1/3", Assert.Single(vm.History).Details);
            release.SetResult(); await run;
            Assert.False(vm.IsBusy); Assert.Contains("completed", vm.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2/3", Assert.Single(vm.History).Details); Assert.Equal(100, vm.Percent);
        }
        finally { release.TrySetResult(); }
    }
    [AvaloniaFact]
    public async Task Closing_cancels_retry_delay_and_leaves_no_running_worker()
    {
        await using var f = new RetryFixture(); var ready = new TaskCompletionSource();
        f.Factory.Send = (_, _, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture");
        f.OnDelay = async (_, ct) => { ready.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
        await f.Add(); using var local = new LocalBrowserViewModel(new LocalFixture());
        await using var vm = new TransferViewModel(f.Coordinator, f.Queue, local, f.Profiles.Browser);
        await vm.InitializeAsync(); var run = vm.StartQueueCommand.ExecuteAsync(null);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await vm.DisposeAsync(); await run;
        Assert.False(f.Queue.IsRunning); Assert.Single(f.Factory.Requests);
        Assert.Equal(TransferState.Cancelled, Assert.Single(await f.Journal.ReadAsync(TestContext.Current.CancellationToken)).State);
    }
    [Fact]
    public void Exhausted_failure_message_keeps_reason_and_budget()
    {
        var message = TransferViewModel.Describe(new(TransferState.Failed, TransferError.TransientService, Attempts: 3, RetryLimitReached: true));
        Assert.Contains("retry limit reached", message); Assert.Contains("3 attempts", message); Assert.Contains("500/502/503/504", message);
    }
}
