using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace BucketBus.Desktop.Tests;

public sealed class TransferQueueViewTests
{
    [AvaloniaFact]
    public async Task Five_files_and_multiple_exact_remote_objects_enqueue_while_running_and_pause_then_continue()
    {
        await using var f = new TransferQueueTests.QueueFixture();
        var names = new[] { "a.txt", "b.txt", "é +%.txt", "zero", "é.txt" };
        foreach (var name in names) await File.WriteAllTextAsync(Path.Combine(f.Root, name), "fixture", TestContext.Current.CancellationToken);
        var entries = names.Select(name => new LocalEntry(name, Path.Combine(f.Root, name), LocalEntryKind.File, false, 7, null)).ToArray();
        var localFiles = new LocalFixture { Read = (_, _) => Task.FromResult(new LocalDirectoryResult(f.Root, entries)) };
        var display = f.Profiles.Browser.Display; using var local = new LocalBrowserViewModel(localFiles, display); await local.InitializeAsync();
        f.Profiles.Factory.CreateSession = (profile, _, _) => new BrowserSession(profile)
        {
            Objects = (_, _) => new BrowserListing<StorageEntry>(_ => Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new([
                new ObjectEntry("prefix/remote-one.bin", 7, null, null), new PrefixEntry("prefix/folder/"), new ObjectEntry("prefix//é remote.bin", 7, null, null)], true))))
        };
        await f.Profiles.ConnectAsync(); f.Profiles.Browser.PrefixInput = "prefix/"; await f.Profiles.Browser.GoCommand.ExecuteAsync(null);
        using var vm = new TransferViewModel(f.Coordinator, f.Queue, local, f.Profiles.Browser, display);
        await vm.InitializeAsync();
        var expectedUploads = local.Rows.Select(r => "prefix/" + r.Entry.Name).ToArray();
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Profiles.Browser, local: local, transfers: vm) };
        var release = new TaskCompletionSource(); f.Factory.Run = async (key, ct) => { if (key == "prefix/a.txt") await release.Task.WaitAsync(ct); return new(TransferState.Completed); };
        try
        {
            window.Show(); foreach (var row in local.Rows) local.SelectedRows.Add(row);
            Assert.True(vm.UploadCommand.CanExecute(null)); await vm.UploadCommand.ExecuteAsync(null);
            Assert.Empty(f.Factory.Keys); Assert.Equal(5, vm.History.Count); Assert.All(vm.History, row => Assert.Equal(TransferState.Queued, row.Record.State));
            var running = vm.StartQueueCommand.ExecuteAsync(null); await f.WaitCalls(1);
            var remoteView = window.GetVisualDescendants().OfType<RemoteBrowserView>().Single(); var list = remoteView.FindControl<ListBox>("ObjectList")!;
            list.Selection.Select(0); list.Selection.Select(2); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, f.Profiles.Browser.SelectedRows.Count); Assert.True(vm.DownloadCommand.CanExecute(null));
            var selected = f.Profiles.Browser.SelectedRows.ToArray(); display.Current = new(FileSizeDisplay.ExactBytes, RemoteNameDisplay.FullKey);
            Assert.Equal(selected, f.Profiles.Browser.SelectedRows); await vm.DownloadCommand.ExecuteAsync(null);
            var queued = (await f.Journal.ReadAsync(TestContext.Current.CancellationToken)).Where(r => r.State == TransferState.Queued).ToArray();
            Assert.Equal(6, queued.Length); Assert.All(queued.Where(r => !r.Upload), r => Assert.Equal(7, r.Total)); Assert.Equal(new[] { "prefix/remote-one.bin", "prefix//é remote.bin" }, queued.Where(r => !r.Upload).OrderBy(r => r.QueueOrder).Select(r => r.Key));
            vm.PauseQueueCommand.Execute(null); release.SetResult(); await running;
            Assert.Single(f.Factory.Keys); Assert.Equal(6, vm.History.Count(r => r.Record.State == TransferState.Queued));
            vm.SelectedHistory = vm.History.First(r => r.Record.State == TransferState.Queued); Assert.True(vm.RemoveQueuedCommand.CanExecute(null));
            var removedKey = vm.SelectedHistory.Record.Key;
            await vm.RemoveQueuedCommand.ExecuteAsync(null); Assert.Equal(5, vm.History.Count(r => r.Record.State == TransferState.Queued));
            Assert.True(vm.StartQueueCommand.CanExecute(null)); await vm.StartQueueCommand.ExecuteAsync(null);
            Assert.Equal(expectedUploads.Where(key => key != removedKey).Concat(new[] { "prefix/remote-one.bin", "prefix//é remote.bin" }), f.Factory.Keys);
            Assert.All(vm.History, row => Assert.Equal(TransferState.Completed, row.Record.State));
            Assert.Equal("fixture", await File.ReadAllTextAsync(Path.Combine(f.Root, "remote-one.bin"), TestContext.Current.CancellationToken));
            Assert.Equal("fixture", await File.ReadAllTextAsync(Path.Combine(f.Root, "é remote.bin"), TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); window.Close(); }
    }
    [AvaloniaFact]
    public async Task Mixed_directories_prefixes_and_stale_page_selection_cannot_enqueue()
    {
        await using var f = new TransferQueueTests.QueueFixture();
        using var local = new LocalBrowserViewModel(new LocalFixture()); await local.InitializeAsync();
        f.Profiles.Factory.CreateSession = (profile, _, _) => new BrowserSession(profile)
        { Objects = (_, _) => new BrowserListing<StorageEntry>(_ => Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new([
            new PrefixEntry("prefix/"), new ObjectEntry("object", 7, null, null)], true)))) };
        await f.Profiles.ConnectAsync(); using var vm = new TransferViewModel(f.Coordinator, f.Queue, local, f.Profiles.Browser);
        local.SelectedRows.Add(local.Rows.First(r => r.Entry.Kind == LocalEntryKind.Directory)); Assert.False(vm.UploadCommand.CanExecute(null));
        foreach (var row in f.Profiles.Browser.Rows) f.Profiles.Browser.SelectedRows.Add(row); Assert.False(vm.DownloadCommand.CanExecute(null));
        f.Profiles.Browser.SelectedRows.Clear(); f.Profiles.Browser.SelectedRows.Add(new("unselected-other-page", BrowserRowKind.Object, 7));
        Assert.False(vm.DownloadCommand.CanExecute(null)); Assert.Empty(await f.Journal.ReadAsync(TestContext.Current.CancellationToken));
    }
}
