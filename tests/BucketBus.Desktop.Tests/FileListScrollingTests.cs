using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

public sealed class FileListScrollingTests
{
    [AvaloniaTheory]
    [InlineData(false, -1)] [InlineData(true, -1)]
    [InlineData(false, -.1)] [InlineData(true, -.1)]
    [InlineData(false, -.01)] [InlineData(true, -.01)]
    public async Task Pointer_wheel_over_rows_scrolls_without_changing_selection_or_loading_pages(bool remote, double delta)
    {
        var localReads = 0; var remoteReads = 0;
        var files = new LocalFixture();
        files.Read = (path, _) => { localReads++; return Task.FromResult(new LocalDirectoryResult(path,
            Enumerable.Range(0, 200).Select(i => new LocalEntry("file-" + i, Path.Combine(path, "file-" + i), LocalEntryKind.File, false, i, null)).ToArray())); };
        using var local = new LocalBrowserViewModel(files); await local.InitializeAsync();
        using var f = new BrowserFixture();
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p) { Objects = (_, prefix) => new BrowserListing<StorageEntry>(_ =>
        { remoteReads++; return Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new(
            Enumerable.Range(0, 200).Select(i => (StorageEntry)new ObjectEntry(prefix + "file-" + i, i, null, null)).ToArray(), true))); }) };
        await f.ConnectAsync();
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Browser, local: local) };
        try
        {
            window.Show(); Layout(window);
            var list = remote ? window.FindControl<RemoteBrowserView>("BrowserView")!.FindControl<ListBox>("ObjectList")!
                : window.FindControl<LocalBrowserView>("LocalView")!.FindControl<ListBox>("LocalList")!;
            list.SelectedIndex = 0; window.FindControl<TextBox>("LocalLocation")!.Focus(); Layout(window);
            var selected = list.SelectedItem;
            var view = list.GetVisualAncestors().OfType<UserControl>().First();
            var columns = view.GetVisualDescendants().OfType<FileColumnsGrid>().ToArray();
            Assert.All(columns, grid => Assert.Equal(columns[0].ColumnDefinitions[2].Width, grid.ColumnDefinitions[2].Width));
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(viewer.Extent.Height > viewer.Viewport.Height);
            Assert.Equal(ScrollBarVisibility.Visible, viewer.VerticalScrollBarVisibility);
            Assert.False(viewer.AllowAutoHide);
            var point = list.TranslatePoint(new Point(80, 50), window)!.Value;
            window.MouseMove(point);
            for (var i = 0; i < 20; i++) { window.MouseWheel(point, new Vector(0, delta)); Layout(window); }
            Assert.True(viewer.Offset.Y > 0, $"Wheel {delta}: {viewer.Offset}; extent={viewer.Extent}; viewport={viewer.Viewport}");
            Assert.Same(selected, list.SelectedItem); Assert.Equal(1, localReads); Assert.Equal(1, remoteReads);
            var before = viewer.Offset.Y;
            for (var i = 0; i < 20; i++) { window.MouseWheel(point, new Vector(0, -delta)); Layout(window); }
            Assert.True(viewer.Offset.Y < before);
            viewer.Offset = default; Layout(window);
            var bar = viewer.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.True(bar.IsEffectivelyVisible); Assert.True(bar.Bounds.Width >= 12);
            var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(0, 60), RawInputModifiers.LeftMouseButton);
            window.MouseUp(start + new Vector(0, 60), MouseButton.Left); Layout(window);
            Assert.True(viewer.Offset.Y > 0); Assert.Same(selected, list.SelectedItem);
        }
        finally { window.Close(); }
    }
    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
