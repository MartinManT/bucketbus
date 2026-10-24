using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace BucketBus.Desktop.Tests;

public sealed class LocalBrowserTests
{
    [AvaloniaFact]
    public async Task Navigation_parent_refresh_sort_and_exact_selection_work_without_reading_user_files()
    {
        var files = new LocalFixture(); using var vm = new LocalBrowserViewModel(files);
        await vm.InitializeAsync(); Assert.Equal(files.HomeDirectory, vm.DirectoryPath); Assert.Single(vm.Roots);
        var home = vm.DirectoryPath;
        var folder = Assert.Single(vm.Rows, row => row.Entry.CanEnter); vm.SelectedRows.Add(folder);
        await vm.OpenCommand.ExecuteAsync(null); Assert.Equal(folder.Entry.FullPath, vm.DirectoryPath);
        await vm.UpCommand.ExecuteAsync(null); Assert.Equal(home, vm.DirectoryPath);
        Assert.Equal(3, files.Reads);
        vm.SelectedRows.Add(vm.Rows.Single(row => row.Entry.Name == "z-file"));
        await vm.RefreshCommand.ExecuteAsync(null); Assert.Equal("z-file", Assert.Single(vm.SelectedRows).Entry.Name);
        vm.SortCommand.Execute(LocalSortField.Size);
        Assert.Equal(new[] { "folder", "z-file", "a-file" }, vm.Rows.Select(r => r.Entry.Name));
        vm.SortCommand.Execute(LocalSortField.Size);
        Assert.Equal(new[] { "folder", "a-file", "z-file" }, vm.Rows.Select(r => r.Entry.Name));
        vm.SortCommand.Execute(LocalSortField.Modified);
        Assert.Equal("z-file", vm.Rows[1].Entry.Name);
        vm.SortCommand.Execute(LocalSortField.Type); Assert.Contains("Type", vm.SortSummary);
        vm.SortCommand.Execute(LocalSortField.Name); Assert.Equal("a-file", vm.Rows[1].Entry.Name);
        Assert.Equal("z-file", Assert.Single(vm.SelectedRows).Entry.Name);
        files.Read = (p, _) => Task.FromResult(new LocalDirectoryResult(p, []));
        await vm.RefreshCommand.ExecuteAsync(null); Assert.Empty(vm.SelectedRows); Assert.Contains("no longer", vm.Status);
    }
    [AvaloniaTheory]
    [InlineData(LocalBrowseError.None, LocalBrowserState.Empty, "Empty")]
    [InlineData(LocalBrowseError.AccessDenied, LocalBrowserState.Error, "denied")]
    [InlineData(LocalBrowseError.NotFound, LocalBrowserState.Error, "no longer")]
    [InlineData(LocalBrowseError.Unavailable, LocalBrowserState.Error, "unavailable")]
    [InlineData(LocalBrowseError.InvalidPath, LocalBrowserState.Error, "absolute")]
    public async Task Empty_and_filesystem_failures_have_distinct_visible_states(LocalBrowseError error, LocalBrowserState state, string message)
    {
        var files = new LocalFixture { Read = (path, _) => Task.FromResult(new LocalDirectoryResult(path, [], error)) };
        using var vm = new LocalBrowserViewModel(files);
        var view = new LocalBrowserView { DataContext = vm }; var window = new Window { Content = view, Width = 620, Height = 600 };
        try
        {
            window.Show(); await vm.NavigateAsync(files.HomeDirectory); Dispatcher.UIThread.RunJobs();
            Assert.Equal(state, vm.State); Assert.Contains(message, view.FindControl<TextBlock>("LocalStatus")!.Text);
            Assert.False(view.FindControl<ProgressBar>("LocalProgress")!.IsVisible);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Late_root_discovery_cannot_reset_explicit_user_navigation()
    {
        var files = new LocalFixture(); using var vm = new LocalBrowserViewModel(files);
        var roots = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        files.GetRoots = _ => roots.Task;
        var initialization = vm.InitializeAsync();
        var chosen = Path.Combine(files.HomeDirectory, "chosen"); await vm.NavigateAsync(chosen);
        roots.SetResult([Path.GetPathRoot(chosen)!]); await initialization;
        Assert.Equal(chosen, vm.DirectoryPath); Assert.Equal(1, files.Reads);
    }
    [AvaloniaFact]
    public async Task Superseded_and_cancelled_results_cannot_replace_the_current_directory()
    {
        var files = new LocalFixture(); using var vm = new LocalBrowserViewModel(files);
        var delayed = new TaskCompletionSource<LocalDirectoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        files.Read = (path, ct) => { observed = ct; return delayed.Task; };
        var old = vm.NavigateAsync(files.HomeDirectory);
        Assert.True(vm.IsBusy); Assert.Contains("Loading", vm.ListingSummary);
        var current = Path.Combine(files.HomeDirectory, "new");
        files.Read = (path, _) => Task.FromResult(new LocalDirectoryResult(path, []));
        await vm.NavigateAsync(current); Assert.True(observed.IsCancellationRequested);
        delayed.SetResult(new(files.HomeDirectory, files.Entries)); await old;
        Assert.Equal(current, vm.DirectoryPath); Assert.Empty(vm.Rows);
        var cancelled = new TaskCompletionSource<LocalDirectoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        files.Read = (_, ct) => { observed = ct; return cancelled.Task; };
        var pending = vm.RefreshCommand.ExecuteAsync(null); vm.CancelCommand.Execute(null);
        cancelled.SetResult(new(current, files.Entries)); await pending;
        Assert.True(observed.IsCancellationRequested); Assert.Empty(vm.Rows); Assert.Equal(LocalBrowserState.Cancelled, vm.State);
    }
    [AvaloniaFact]
    public async Task Partial_listing_is_bounded_virtualized_and_never_presented_as_a_complete_sort()
    {
        var files = new LocalFixture(); using var vm = new LocalBrowserViewModel(files);
        files.Read = (path, _) => Task.FromResult(new LocalDirectoryResult(path,
            Enumerable.Range(0, LocalBrowsingLimits.MaximumEntries + 1).Select(i => new LocalEntry("entry" + i,
                Path.Combine(path, "entry" + i), LocalEntryKind.File, false, i, null)).ToArray()));
        await vm.NavigateAsync(files.HomeDirectory);
        Assert.Equal(LocalBrowsingLimits.MaximumEntries, vm.Rows.Count);
        vm.SortCommand.Execute(LocalSortField.Size); Assert.Contains("Partial", vm.ListingSummary); Assert.Contains("only", vm.ListingSummary);
        var view = new LocalBrowserView { DataContext = vm }; var window = new Window { Content = view, Width = 620, Height = 600 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); var list = view.FindControl<ListBox>("LocalList")!;
            Assert.Equal(LocalBrowsingLimits.MaximumEntries, list.ItemCount);
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 60);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Real_window_supports_multi_selection_keyboard_navigation_and_independent_panes()
    {
        using var remote = new BrowserFixture(); await remote.ConnectAsync();
        var remoteRows = remote.Browser.Rows;
        var files = new LocalFixture(); using var vm = new LocalBrowserViewModel(files); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: remote.Browser, local: vm) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var localView = window.GetVisualDescendants().OfType<LocalBrowserView>().Single();
            var list = localView.FindControl<ListBox>("LocalList")!; list.Focus();
            Assert.Same(list, window.FocusManager!.GetFocusedElement());
            Press(window, PhysicalKey.A, RawInputModifiers.Control);
            Assert.Equal(3, vm.SelectedRows.Count); Assert.False(vm.OpenCommand.CanExecute(null));
            list.SelectedItems!.Clear(); list.SelectedIndex = 0;
            Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            Assert.Equal(2, vm.SelectedRows.Count);
            Press(window, PhysicalKey.R, RawInputModifiers.Control);
            Assert.Equal(2, vm.SelectedRows.Count); Assert.True(list.IsKeyboardFocusWithin);
            list.SelectedItems.Clear(); list.SelectedIndex = 0;
            Press(window, PhysicalKey.Enter);
            await BrowserFixture.UntilAsync(() => vm.DirectoryPath.EndsWith("folder", StringComparison.Ordinal) && !vm.IsBusy);
            Assert.True(list.IsKeyboardFocusWithin);
            Press(window, PhysicalKey.Backspace);
            await BrowserFixture.UntilAsync(() => vm.DirectoryPath == files.HomeDirectory && !vm.IsBusy);
            Assert.True(remote.Browser.IsConnected); Assert.Same(remoteRows, remote.Browser.Rows);
            vm.SelectedRows.Add(vm.Rows[1]); var before = vm.Rows;
            await remote.Browser.RefreshCommand.ExecuteAsync(null);
            Assert.Same(before, vm.Rows); Assert.Single(vm.SelectedRows); Assert.Equal(files.HomeDirectory, vm.DirectoryPath);
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            var remoteView = window.GetVisualDescendants().OfType<RemoteBrowserView>().Single();
            Assert.Same(remoteView.FindControl<ListBox>("ObjectList"), window.FocusManager.GetFocusedElement());
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control); Assert.Same(list, window.FocusManager.GetFocusedElement());
            Press(window, PhysicalKey.L, RawInputModifiers.Control);
            var location = window.FindControl<TextBox>("LocalLocation")!; Assert.Same(location, window.FocusManager.GetFocusedElement());
            var prior = vm.DirectoryPath; location.Text = files.HomeDirectory + "x"; location.CaretIndex = location.Text.Length;
            Press(window, PhysicalKey.Backspace); Assert.Equal(prior, vm.DirectoryPath); Assert.Equal(files.HomeDirectory, location.Text);
            location.Text = Path.Combine(files.HomeDirectory, "typed"); Press(window, PhysicalKey.Enter);
            await BrowserFixture.UntilAsync(() => vm.DirectoryPath.EndsWith("typed", StringComparison.Ordinal));
            Assert.True(list.IsKeyboardFocusWithin);
            var readCount = files.Reads; Press(window, PhysicalKey.R, RawInputModifiers.Control);
            await BrowserFixture.UntilAsync(() => files.Reads > readCount && !vm.IsBusy);
            Assert.True(remote.Browser.IsConnected);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Double_click_opens_directory_and_closing_cancels_pending_enumeration()
    {
        var files = new LocalFixture(); using var vm = new LocalBrowserViewModel(files); await vm.InitializeAsync();
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, local: vm) };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var list = window.GetVisualDescendants().OfType<LocalBrowserView>().Single().FindControl<ListBox>("LocalList")!;
        var item = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        var point = item.TranslatePoint(new Point(8, 8), window)!.Value;
        for (var i = 0; i < 2; i++) { window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); }
        await BrowserFixture.UntilAsync(() => vm.DirectoryPath.EndsWith("folder", StringComparison.Ordinal));
        CancellationToken observed = default;
        files.Read = async (path, ct) => { observed = ct; await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(path, []); };
        var pending = vm.RefreshCommand.ExecuteAsync(null); Assert.True(vm.IsBusy);
        window.Close(); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(observed.IsCancellationRequested); Assert.False(vm.IsBusy);
    }
    [Fact]
    public void Display_escapes_directional_controls_without_rewriting_native_identity()
    {
        const string name = "e\u0301\u202E\n";
        var entry = new LocalEntry(name, Path.Combine(Path.GetTempPath(), name), LocalEntryKind.SymbolicLink, false, null, null);
        var row = new LocalRow(entry);
        Assert.DoesNotContain('\u202E', row.Name); Assert.Contains("\\u202E", row.Name); Assert.Equal(name, row.Entry.Name);
        Assert.Equal("Link", row.Type);
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Dispatcher.UIThread.RunJobs(); }
}

internal sealed class LocalFixture : ILocalDirectoryBrowser
{
    public string HomeDirectory { get; } = Path.Combine(Path.GetTempPath(), "bucketbus-local-vm-" + Guid.NewGuid().ToString("N"));
    public LocalEntry[] Entries => [new("folder", Path.Combine(HomeDirectory, "folder"), LocalEntryKind.Directory, true, null, null),
        new("z-file", Path.Combine(HomeDirectory, "z-file"), LocalEntryKind.File, false, 2, DateTimeOffset.UnixEpoch),
        new("a-file", Path.Combine(HomeDirectory, "a-file"), LocalEntryKind.File, false, 9, DateTimeOffset.UnixEpoch.AddDays(1))];
    public int Reads;
    public Func<string, CancellationToken, Task<LocalDirectoryResult>>? Read;
    public Func<CancellationToken, Task<IReadOnlyList<string>>>? GetRoots;
    public Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken cancellationToken = default) =>
        GetRoots?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<string>>([Path.GetPathRoot(HomeDirectory)!]);
    public Task<LocalDirectoryResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    { Reads++; return Read?.Invoke(path, cancellationToken) ?? Task.FromResult(new LocalDirectoryResult(path, Entries)); }
}
