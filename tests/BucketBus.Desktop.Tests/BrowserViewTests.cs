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

public sealed class BrowserViewTests
{
    [AvaloniaFact]
    public async Task Actual_controls_connect_select_navigate_and_request_metadata_by_keyboard()
    {
        using var f = new BrowserFixture(); var heads = 0;
        f.UseSdk((request, _) =>
        {
            if (request.Method == HttpMethod.Head) { heads++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)); }
            return Task.FromResult(BrowserFixture.Xml(BrowserFixture.Objects(request.RequestUri!.Query.Contains("prefix=folder", StringComparison.Ordinal)
                ? BrowserFixture.Item("folder/é") : "<CommonPrefixes><Prefix>folder%2F</Prefix></CommonPrefixes>")));
        });
        await f.Browser.ReloadProfilesAsync();
        var vm = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Browser);
        vm.CompleteInitialization(); var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            var view = window.GetVisualDescendants().OfType<RemoteBrowserView>().Single();
            var combo = view.FindControl<ComboBox>("BrowserProfiles")!;
            combo.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.Equal(f.Profiles.Snapshot.Profiles[0], f.Browser.SelectedProfile);
            var connect = view.FindControl<Button>("ConnectButton")!; Assert.True(connect.IsEffectivelyEnabled);
            connect.Focus(); Press(window, PhysicalKey.Space);
            await BrowserFixture.UntilAsync(() => f.Browser.State == BrowserState.Ready);
            Assert.Equal("Ready · Connected", window.FindControl<TextBlock>("ShellStatus")!.Text);
            var list = view.FindControl<ListBox>("ObjectList")!;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Assert.True(list.Focus()); Press(window, PhysicalKey.ArrowDown);
            Assert.Equal("folder/", f.Browser.SelectedRow!.Key);
            Press(window, PhysicalKey.Enter);
            await BrowserFixture.UntilAsync(() => f.Browser.Rows.FirstOrDefault()?.Key == "folder/é");
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Assert.True(list.IsKeyboardFocusWithin); Press(window, PhysicalKey.ArrowDown); Assert.Equal("folder/é", f.Browser.SelectedRow!.Key);
            Assert.Equal(0, heads);
            Press(window, PhysicalKey.Enter, RawInputModifiers.Alt);
            await BrowserFixture.UntilAsync(() => f.Browser.Metadata.Contains("not found", StringComparison.Ordinal));
            Assert.Equal(1, heads); Assert.Contains("not found", window.FindControl<TextBox>("ObjectMetadata")!.Text);
            Press(window, PhysicalKey.Backspace);
            await BrowserFixture.UntilAsync(() => f.Browser.PrefixInput == "" && f.Browser.State == BrowserState.Ready);
            Assert.Equal(BrowserRowKind.Prefix, Assert.Single(f.Browser.Rows).Kind);
            // Backspace while editing a prefix must never navigate.
            var prefix = view.FindControl<TextBox>("PrefixInput")!; prefix.Focus(); prefix.Text = "editable"; prefix.CaretIndex = 8;
            Press(window, PhysicalKey.Backspace); Assert.Equal("editabl", prefix.Text); Assert.Equal(BrowserRowKind.Prefix, Assert.Single(f.Browser.Rows).Kind);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Large_page_uses_virtualized_containers_and_plain_text_bindings()
    {
        using var f = new BrowserFixture();
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        {
            Objects = (_, _) => new BrowserListing<StorageEntry>(_ => Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new(
                Enumerable.Range(0, 1000).Select(i => (StorageEntry)new ObjectEntry(i == 0 ? "<script>{Binding Secret}é\n" : $"object-{i}", null, null, null)).ToArray(), true))))
        };
        await f.ConnectAsync();
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Browser) };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<RemoteBrowserView>().Single();
            var list = view.FindControl<ListBox>("ObjectList")!;
            Assert.Equal(1000, list.ItemCount);
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 60);
            Assert.True(list.Bounds.Height >= 80);
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "<script>{Binding Secret}é\\u000A");
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Unknown");
            list.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.Same(f.Browser.Rows[0], f.Browser.SelectedRow);
            Assert.Contains("\\u00E9", f.Browser.SelectionIdentity);
            Assert.False(view.FindControl<Button>("NextPage")!.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Loading_and_safe_error_are_bound_and_disconnect_clears_visible_rows()
    {
        using var f = new BrowserFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<StorageResult<StoragePage<StorageEntry>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        { Objects = (_, _) => new BrowserListing<StorageEntry>(_ => { started.SetResult(); return response.Task; }) };
        await f.SelectAsync();
        var view = new RemoteBrowserView { DataContext = f.Browser }; var window = new Window { Content = view, Width = 650, Height = 700 };
        try
        {
            window.Show(); var connection = f.Browser.ConnectCommand.ExecuteAsync(null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs(); Assert.True(view.FindControl<ProgressBar>("BrowserProgress")!.IsVisible);
            response.SetResult(StorageResult<StoragePage<StorageEntry>>.Failure(StorageError.AccessDenied)); await connection;
            Dispatcher.UIThread.RunJobs(); Assert.False(view.FindControl<ProgressBar>("BrowserProgress")!.IsVisible);
            Assert.Contains("Access denied", view.FindControl<TextBlock>("BrowserStatus")!.Text);
            f.Browser.DisconnectCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.Contains("Disconnected", view.FindControl<TextBlock>("BrowserStatus")!.Text); Assert.Equal(0, view.FindControl<ListBox>("ObjectList")!.ItemCount);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Closing_window_cancels_pending_credentials_and_pane_shortcut_targets_the_list()
    {
        using var f = new BrowserFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        f.Vault.Read = async ct => { observed = ct; started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(CredentialStatus.Cancelled); };
        await f.SelectAsync();
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Browser) };
        try
        {
            window.Show(); Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            var view = window.GetVisualDescendants().OfType<RemoteBrowserView>().Single();
            Assert.Same(view.FindControl<ListBox>("ObjectList"), window.FocusManager!.GetFocusedElement());
            Press(window, PhysicalKey.L, RawInputModifiers.Control);
            Assert.Same(window.FindControl<RemoteBrowserView>("BrowserView")!.FindControl<TextBox>("PrefixInput"), window.FocusManager.GetFocusedElement());
            var open = f.Browser.ConnectCommand.ExecuteAsync(null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close(); await open.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observed.IsCancellationRequested); Assert.Equal(0, f.Factory.Created);
            Assert.Equal(BrowserState.Disconnected, f.Browser.State);
        }
        finally { window.Close(); }
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Dispatcher.UIThread.RunJobs(); }
}
