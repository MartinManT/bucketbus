using Avalonia.Headless.XUnit;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;

namespace BucketBus.Desktop.Tests;

public sealed class MultipartViewTests
{
    [AvaloniaFact]
    public async Task Recovery_actions_require_confirmation_and_reset_when_selection_changes()
    {
        await using var f = new MultipartWorkflowTests.Fixture(); f.Remote.Stop = "after-ack";
        await f.Add(); await f.Queue.RunAsync(false, null, TestContext.Current.CancellationToken);
        using var local = new LocalBrowserViewModel(new LocalFixture());
        await using var vm = new TransferViewModel(f.Coordinator, f.Queue, local, f.Profiles.Browser);
        await vm.InitializeAsync(); vm.SelectedHistory = Assert.Single(vm.History);
        Assert.Contains("Multipart: Uploading", vm.RecoveryDetails);
        Assert.False(vm.ResumeMultipartCommand.CanExecute(null)); Assert.False(vm.AbortMultipartCommand.CanExecute(null));
        vm.ConfirmMultipart = true; Assert.True(vm.ResumeMultipartCommand.CanExecute(null)); Assert.True(vm.AbortMultipartCommand.CanExecute(null));
        vm.SelectedHistory = null; Assert.False(vm.ConfirmMultipart); Assert.False(vm.AbortMultipartCommand.CanExecute(null));
        vm.SelectedHistory = Assert.Single(vm.History); vm.ConfirmMultipart = true;
        await vm.AbortMultipartCommand.ExecuteAsync(null);
        Assert.Equal(TransferState.Cancelled, Assert.Single(vm.History).Record.State); Assert.False(vm.ConfirmMultipart); Assert.False(vm.IsBusy);
    }
    [AvaloniaFact]
    public async Task Closing_cancels_large_source_preparation_without_recording_a_partial_job()
    {
        using var profiles = new BrowserFixture(); await profiles.ConnectAsync();
        using var local = new LocalBrowserViewModel(new LocalFixture()); await local.InitializeAsync();
        local.SelectedRows.Add(local.Rows.First(r => r.Entry.Kind == LocalEntryKind.File));
        var files = new BlockingSource(); var journal = new MemoryJournal();
        await using var vm = TransferTestComposition.Create(profiles.Manager, files, new MultipartWorkflowTests.Remote(), journal, local, profiles.Browser);
        var enqueue = vm.UploadCommand.ExecuteAsync(null); await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await vm.DisposeAsync(); await enqueue;
        Assert.False(vm.IsEnqueuing); Assert.Empty(journal.Records);
    }
    private sealed class BlockingSource : ITransferFiles
    {
        public TaskCompletionSource Entered { get; } = new();
        public async Task<ITransferSource> OpenSourceAsync(string path, CancellationToken token)
        { Entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
        public Task<ITransferDestination> CreateDestinationAsync(string dir, string name, CancellationToken token, TemporaryReceipt? previous = null) => throw new NotSupportedException();
    }
}
