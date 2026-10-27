using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
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
    [AvaloniaTheory]
    [InlineData(850, 680)] [InlineData(1180, 760)]
    public async Task Recovery_actions_are_visible_without_scrolling_at_supported_window_sizes(int width, int height)
    {
        await using var f = new MultipartWorkflowTests.Fixture(); f.Remote.Stop = "after-ack";
        await f.Add(); await f.Queue.RunAsync(false, null, TestContext.Current.CancellationToken);
        using var local = new LocalBrowserViewModel(new LocalFixture());
        await using var vm = new TransferViewModel(f.Coordinator, f.Queue, local, f.Profiles.Browser);
        await vm.InitializeAsync(); vm.SelectedHistory = Assert.Single(vm.History);
        var shell = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Profiles.Browser, local: local, transfers: vm);
        shell.CompleteInitialization();
        var window = new MainWindow { Width = width, Height = height, DataContext = shell };
        window.Show();
        try
        {
            window.FindControl<Expander>("TransferHistoryPanel")!.IsExpanded = true;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            foreach (var name in new[] { "Resume multipart upload", "Abort incomplete upload" })
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, name));
                Assert.True(button.IsEffectivelyVisible); Assert.True(button.Bounds.Height > 0);
                var center = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
                Assert.True(new Rect(window.Bounds.Size).Contains(new Rect(button.TranslatePoint(default, window)!.Value, button.Bounds.Size)), name + $" is outside the window: center={button.TranslatePoint(center, window)}, window={window.Bounds}");
                foreach (var viewport in button.GetVisualAncestors().OfType<ScrollViewer>())
                    Assert.True(new Rect(viewport.Bounds.Size).Contains(button.TranslatePoint(center, viewport)!.Value), name + " is clipped below the history viewport");
            }
            var footer = window.FindControl<TextBlock>("ShellStatus")!;
            Assert.True(new Rect(window.Bounds.Size).Contains(new Rect(footer.TranslatePoint(default, window)!.Value, footer.Bounds.Size)), "Recovery controls push the status bar outside the window");
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Unacknowledged_part_explains_abort_then_allows_fresh_enqueue_after_confirmed_cleanup()
    {
        await using var f = new MultipartWorkflowTests.Fixture(); f.Remote.Stop = "after-ack";
        await f.Add("a-file"); await f.Queue.RunAsync(false, null, TestContext.Current.CancellationToken);
        var interrupted = Assert.Single(await f.Journal.ReadAsync(TestContext.Current.CancellationToken));
        var record = interrupted.Next(TransferState.RequiresReconciliation); await f.Journal.SaveAsync(record, TestContext.Current.CancellationToken);
        record = record.Next(TransferState.Running) with { Multipart = record.Multipart! with { Phase = MultipartPhase.SendingPart, PendingPart = 2 } };
        await f.Journal.SaveAsync(record, TestContext.Current.CancellationToken);
        await f.Journal.SaveAsync(record.Next(TransferState.Interrupted), TestContext.Current.CancellationToken);
        await f.Profiles.ConnectAsync();
        using var local = new LocalBrowserViewModel(new LocalFixture()); await local.InitializeAsync();
        local.SelectedRows.Add(local.Rows.Single(r => r.Entry.Name == "a-file"));
        await using var vm = new TransferViewModel(f.Coordinator, f.Queue, local, f.Profiles.Browser);
        await vm.InitializeAsync(); vm.SelectedHistory = Assert.Single(vm.History);
        Assert.Contains("unacknowledged", vm.RecoveryGuidance); Assert.True(vm.ShowMultipartConfirmation); Assert.False(vm.ShowDownloadConfirmation);
        Assert.False(vm.ResumeMultipartCommand.CanExecute(null)); Assert.False(vm.AbortMultipartCommand.CanExecute(null));
        await vm.UploadCommand.ExecuteAsync(null);
        Assert.True(vm.IsHistoryExpanded); Assert.Contains("unresolved transfer", vm.Status); Assert.Contains("history record", vm.Status);
        Assert.Single(await f.Journal.ReadAsync(TestContext.Current.CancellationToken)); Assert.Equal(0, f.Remote.AbortCalls);
        vm.ConfirmMultipart = true; Assert.False(vm.ResumeMultipartCommand.CanExecute(null)); Assert.True(vm.AbortMultipartCommand.CanExecute(null));
        await vm.AbortMultipartCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(vm.History).Record.Resolved); Assert.Contains("add a new transfer", vm.RecoveryGuidance);
        await vm.UploadCommand.ExecuteAsync(null);
        Assert.Single(vm.History, r => r.Record.State == TransferState.Queued); Assert.Single(vm.History, r => r.Record.State == TransferState.Cancelled);
        Assert.Equal(1, f.Remote.AbortCalls); Assert.Equal(1, f.Remote.MultipartCalls); // No automatic restart or extra remote upload.
    }
    [AvaloniaFact]
    public async Task Uncertain_completion_guidance_never_offers_abort_or_restart()
    {
        await using var f = new MultipartWorkflowTests.Fixture(); f.Remote.Stop = "after-ack";
        await f.Add(); await f.Queue.RunAsync(false, null, TestContext.Current.CancellationToken);
        using var local = new LocalBrowserViewModel(new LocalFixture());
        await using var vm = new TransferViewModel(f.Coordinator, f.Queue, local, f.Profiles.Browser);
        await vm.InitializeAsync();
        var record = Assert.Single(vm.History).Record;
        vm.SelectedHistory = new(record with { State = TransferState.OutcomeUnknown, Multipart = record.Multipart! with { Phase = MultipartPhase.Completing,
            Parts = Enumerable.Range(1, record.Multipart.Plan.PartCount).Select(n => new UploadedPart(n, record.Multipart.Plan.Length(n, record.Total!.Value), "fixture", record.Sha256!)).ToArray() } });
        vm.SelectedHistory.Record.Validate();
        vm.ConfirmMultipart = true;
        Assert.Contains("Reconcile upload", vm.RecoveryGuidance); Assert.False(vm.ShowMultipartConfirmation);
        Assert.True(vm.ReconcileCommand.CanExecute(null)); Assert.False(vm.ResumeMultipartCommand.CanExecute(null)); Assert.False(vm.AbortMultipartCommand.CanExecute(null));
        Assert.False(vm.RestartCommand.CanExecute(null)); Assert.Equal(0, f.Remote.AbortCalls);
    }
    private sealed class BlockingSource : ITransferFiles
    {
        public TaskCompletionSource Entered { get; } = new();
        public async Task<ITransferSource> OpenSourceAsync(string path, CancellationToken token)
        { Entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
        public Task<ITransferDestination> CreateDestinationAsync(string dir, string name, CancellationToken token, TemporaryReceipt? previous = null) => throw new NotSupportedException();
    }
}
