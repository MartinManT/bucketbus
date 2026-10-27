using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BucketBus.Core;
using BucketBus.Desktop.Presentation;
using BucketBus.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace BucketBus.Desktop.Tests;

public sealed class ResponsiveLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1920, 1080, 1)] [InlineData(1600, 900, 1)] [InlineData(1366, 768, 1)]
    [InlineData(1280, 720, 1)] [InlineData(1024, 768, 1)]
    [InlineData(1366, 768, 1.25)] [InlineData(1366, 768, 1.5)] [InlineData(1024, 768, 2)]
    public async Task Browsing_dominates_collapsed_workspace_and_inspector_stays_inside_client(int width, int height, double scale)
    {
        using var fixture = new BrowserFixture(prefix: "long-prefix/e\u0301//../%2F/");
        var reads = 0;
        fixture.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        {
            Objects = (_, prefix) => new BrowserListing<StorageEntry>(_ =>
            {
                reads++;
                return Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new(
                    Enumerable.Range(0, 100).Select(n => (StorageEntry)new ObjectEntry(prefix + new string('x', 150) + n, 170283, DateTimeOffset.UnixEpoch, null)).ToArray(), true)));
            })
        };
        await fixture.ConnectAsync();
        using var local = new LocalBrowserViewModel(new LocalFixture()); await local.InitializeAsync();
        using var transfers = TransferTestComposition.Create(fixture.Manager, new TransferFilesStub(), new TransferFactoryStub(), new MemoryJournal(), local, fixture.Browser);
        await transfers.InitializeAsync();
        var shell = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: fixture.Browser, local: local, transfers: transfers);
        shell.CompleteInitialization();
        var window = new MainWindow { Width = width, Height = height, DataContext = shell };
        try
        {
            window.Show(); window.SetRenderScaling(scale); Layout(window);
            Assert.Equal(new Size(width, height), window.ClientSize);
            var remote = window.FindControl<RemoteBrowserView>("BrowserView")!;
            var localView = window.FindControl<LocalBrowserView>("LocalView")!;
            var list = remote.FindControl<ListBox>("ObjectList")!;
            var localList = localView.FindControl<ListBox>("LocalList")!;
            Capture(window, $"ux3-{width}x{height}-{scale}x-initial");
            foreach (var pane in new[] { "LocalPane", "RemotePane" })
            {
                var control = window.FindControl<Border>(pane)!;
                Assert.True(control.Bounds.Height >= height * .65, $"{pane}: {control.Bounds}"); Inside(control, window);
            }
            Assert.True(list.Bounds.Height >= height * .45, $"Remote list: {list.Bounds}");
            Assert.True(localList.Bounds.Height >= height * .45, $"Local list: {localList.Bounds}");
            foreach (var name in new[] { "BrowserProfiles", "ConnectButton", "VaultConsent", "BucketInput", "PrefixInput", "GoButton", "PropertiesButton" })
                Inside(remote.FindControl<Control>(name)!, window, remote);
            Assert.False(fixture.Browser.AllowUnlock);
            var selected = fixture.Browser.Rows[0]; list.SelectedItem = selected; Layout(window);
            Capture(window, $"ux3-{width}x{height}-{scale}x-collapsed");
            window.FindControl<Expander>("TransferHistoryPanel")!.IsExpanded = true; Layout(window);
            Inside(window.FindControl<TabControl>("InspectorTabs")!, window);
            Inside(window.FindControl<TextBlock>("ShellStatus")!, window);
            Assert.True(list.Bounds.Height >= 80);
            Assert.Same(selected, fixture.Browser.SelectedRow); Assert.Equal(1, reads);
            Capture(window, $"ux3-{width}x{height}-{scale}x-expanded");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Compact_switch_and_splitter_keep_same_panes_exact_selection_and_focus_without_requests()
    {
        using var fixture = new BrowserFixture(prefix: "a//e\u0301/"); var reads = 0;
        fixture.Factory.CreateSession = (p, _, _) => new BrowserSession(p) { Objects = (_, prefix) => new BrowserListing<StorageEntry>(_ =>
        { reads++; return Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new(Enumerable.Range(0, 100).Select(n => (StorageEntry)new ObjectEntry(prefix + n, 36, null, null)).ToArray(), true))); }) };
        await fixture.ConnectAsync();
        using var local = new LocalBrowserViewModel(new LocalFixture()); await local.InitializeAsync();
        var shell = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: fixture.Browser, local: local);
        var window = new MainWindow { DataContext = shell };
        try
        {
            window.Show(); Layout(window);
            var localView = window.FindControl<LocalBrowserView>("LocalView")!;
            var remoteView = window.FindControl<RemoteBrowserView>("BrowserView")!;
            var localList = localView.FindControl<ListBox>("LocalList")!;
            var remoteList = remoteView.FindControl<ListBox>("ObjectList")!;
            localList.SelectedIndex = 1; remoteList.SelectedIndex = 30; remoteList.Focus(); Layout(window);
            var selected = fixture.Browser.SelectedRow; var localSelected = local.SelectedRows.ToArray();
            var scroll = remoteList.GetVisualDescendants().OfType<ScrollViewer>().First();
            remoteList.ScrollIntoView(fixture.Browser.Rows[30]); Layout(window); var scrollOffset = scroll.Offset;
            Assert.True(scrollOffset.Y > 0);
            remoteList.Focus(); window.Width = 850; Layout(window);
            Assert.False(window.FindControl<Border>("LocalPane")!.IsVisible);
            Assert.True(window.FindControl<Border>("RemotePane")!.IsVisible);
            Assert.Same(remoteList, window.FocusManager!.GetFocusedElement());
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control); Layout(window);
            Assert.True(window.FindControl<Border>("LocalPane")!.IsVisible);
            Assert.Same(localList, window.FocusManager.GetFocusedElement());
            Press(window, PhysicalKey.L, RawInputModifiers.Control);
            Assert.Same(window.FindControl<TextBox>("LocalLocation"), window.FocusManager.GetFocusedElement());
            window.Width = 1366; Layout(window);
            Assert.Same(localView, window.FindControl<LocalBrowserView>("LocalView"));
            Assert.Same(remoteView, window.FindControl<RemoteBrowserView>("BrowserView"));
            Assert.Same(selected, fixture.Browser.SelectedRow); Assert.Equal(localSelected, local.SelectedRows);
            Assert.Equal(scrollOffset, scroll.Offset);
            Assert.Equal(1, reads); Assert.False(fixture.Browser.AllowUnlock);
            // Container-aware column collapse also applies when the user drags the splitter.
            var grid = window.FindControl<Grid>("PaneGrid")!;
            grid.ColumnDefinitions[0].Width = new GridLength(400); Layout(window);
            Assert.Contains("narrow", localView.Classes);
            Assert.All(localList.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("fileColumns")),
                g => Assert.Equal(0, g.ColumnDefinitions[2].Width.Value));
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData("a//e\u0301/../%2F/", "a/", "a//", "a//e\u0301/", "a//e\u0301/../", "a//e\u0301/../%2F/")]
    [InlineData("/é/\\\u202E", "/", "/é/", "/é/\\\u202E")]
    public void Remote_breadcrumbs_preserve_exact_opaque_prefixes(string prefix, params string[] expected)
    {
        var crumbs = LocationCrumb.Remote("bucket", prefix);
        Assert.Equal(new[] { "" }.Concat(expected), crumbs.Select(c => c.Target));
        Assert.DoesNotContain(crumbs, c => c.Label.Contains('\u202E'));
        Assert.Equal(prefix, crumbs[^1].Target);
    }

    [AvaloniaFact]
    public async Task Breadcrumb_command_uses_exact_target_and_properties_open_one_inspector_without_unlock_consent()
    {
        using var f = new BrowserFixture(prefix: "e\u0301//../%2F/"); var prefixes = new List<string>(); var heads = 0;
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        {
            Objects = (_, prefix) => { prefixes.Add(prefix); return new BrowserListing<StorageEntry>(_ => Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new([new ObjectEntry(prefix + "key", 36, null, null)], true)))); },
            Metadata = (_, _, _) => { heads++; return Task.FromResult(StorageResult<ObjectMetadata>.Failure(StorageError.AccessDenied)); }
        };
        await f.ConnectAsync();
        var shell = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Browser);
        var window = new MainWindow { DataContext = shell };
        try
        {
            window.Show(); Layout(window);
            var crumb = f.Browser.Breadcrumbs[2]; await f.Browser.NavigateBreadcrumbCommand.ExecuteAsync(crumb.Target);
            Assert.Equal("e\u0301//", prefixes[^1]);
            f.Browser.SelectedRow = f.Browser.Rows[0]; await f.Browser.PropertiesCommand.ExecuteAsync(null); Layout(window);
            Assert.True(window.FindControl<Expander>("TransferHistoryPanel")!.IsExpanded);
            Assert.Equal(1, shell.InspectorTab);
            Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "ObjectMetadata");
            Inside(window.FindControl<TextBox>("ObjectMetadata")!, window);
            window.FindControl<Expander>("TransferHistoryPanel")!.IsExpanded = false;
            await f.Browser.PropertiesCommand.ExecuteAsync(null); Layout(window);
            Assert.True(window.FindControl<Expander>("TransferHistoryPanel")!.IsExpanded);
            Assert.Equal(2, heads); Assert.False(f.Vault.AllowedUnlock); Assert.False(f.Browser.AllowUnlock);
        }
        finally { window.Close(); }
    }
    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Inside(Control control, Window window, Control? container = null)
    {
        Assert.True(control.IsEffectivelyVisible, control.Name);
        var target = container ?? window;
        var bounds = new Rect(control.TranslatePoint(default, target)!.Value, control.Bounds.Size);
        Assert.True(new Rect(target.Bounds.Size).Inflate(1).Contains(bounds), $"{control.Name}: {bounds} outside {target.Bounds}");
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Layout(window); }
    internal static void Capture(Window window, string name)
    {
        // Optional real Avalonia/Skia rendering, never a browser or production service.
        if (Environment.GetEnvironmentVariable("BUCKETBUS_UI_CAPTURE") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, name + ".layout.txt"), window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Name is not null || c is Expander || c.GetVisualAncestors().OfType<Expander>().Any())
            .Select(c => $"{c.GetType().Name} {c.Name}: {c.Bounds}; visible={c.IsEffectivelyVisible}; margin={c.Margin}"));
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame); frame.Save(Path.Combine(directory, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    private sealed class TransferFilesStub : ITransferFiles
    {
        public Task<ITransferSource> OpenSourceAsync(string path, CancellationToken token) => throw new InvalidOperationException("No transfers during layout");
        public Task<ITransferDestination> CreateDestinationAsync(string dir, string name, CancellationToken token, TemporaryReceipt? previous = null) => throw new InvalidOperationException("No transfers during layout");
    }
    private sealed class TransferFactoryStub : IObjectTransferFactory
    {
        public IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials) => throw new InvalidOperationException("No network during layout");
    }
}
