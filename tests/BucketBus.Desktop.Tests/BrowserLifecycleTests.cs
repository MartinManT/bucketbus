using Avalonia.Headless.XUnit;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;

namespace BucketBus.Desktop.Tests;

public sealed class BrowserLifecycleTests
{
    private static StorageResult<StoragePage<StorageEntry>> Page(string key, bool complete = true) =>
        StorageResult<StoragePage<StorageEntry>>.Success(new([new ObjectEntry(key, null, null, null)], complete));

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Closing_window_cancels_pending_read_or_vault_and_awaits_disposal(bool vault)
    {
        using var f = new BrowserFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        if (vault) f.Vault.Read = async ct =>
        {
            observed = ct; started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(CredentialStatus.Missing);
        };
        else f.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        {
            Objects = (_, _) => new BrowserListing<StorageEntry>(async ct =>
            {
                observed = ct; started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Page("unreachable");
            })
        };
        var window = new MainWindow
        {
            ShutdownAsync = async () => Assert.True(await DesktopShutdown.StopAsync(
                f.Browser.Disconnect, f.Controller.DisposeAsync, TimeSpan.FromSeconds(2)))
        };
        window.Show(); await f.SelectAsync();
        var pending = f.Browser.ConnectCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.Close();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await BrowserFixture.UntilAsync(() => !window.IsVisible);
        Assert.True(observed.IsCancellationRequested); Assert.False(f.Browser.IsConnected);
        if (vault) Assert.Equal(0, f.Factory.Created);
    }
    [AvaloniaFact]
    public async Task Shutdown_deadline_does_not_block_dispatcher_or_hide_incomplete_cleanup()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var stopping = DesktopShutdown.StopAsync(() => cancelled = true, () => new(cleanup.Task), TimeSpan.FromMilliseconds(50));
        var dispatcherRan = false;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => dispatcherRan = true);
        Assert.True(dispatcherRan); Assert.True(cancelled);
        Assert.False(await stopping.WaitAsync(TimeSpan.FromSeconds(2)));
        cleanup.SetException(new IOException("synthetic private detail")); // Late errors remain observed/suppressed.
    }
    [AvaloniaFact]
    public async Task Late_navigation_and_metadata_cannot_replace_the_new_location_or_selection()
    {
        using var f = new BrowserFixture();
        var oldPage = new TaskCompletionSource<StorageResult<StoragePage<StorageEntry>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldMetadata = new TaskCompletionSource<StorageResult<ObjectMetadata>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pageToken = default, metadataToken = default;
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        {
            Objects = (_, prefix) => new BrowserListing<StorageEntry>(ct =>
            {
                if (prefix == "old/") { pageToken = ct; pageStarted.SetResult(); return oldPage.Task; }
                return Task.FromResult(Page(prefix + "object"));
            }),
            Metadata = (_, _, ct) => { metadataToken = ct; metadataStarted.SetResult(); return oldMetadata.Task; }
        };
        await f.ConnectAsync(); var vm = f.Browser;
        vm.SelectedRow = vm.Rows[0]; var metadata = vm.PropertiesCommand.ExecuteAsync(null); await metadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.PrefixInput = "old/"; var navigation = vm.GoCommand.ExecuteAsync(null); await pageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.PrefixInput = "new/"; await vm.GoCommand.ExecuteAsync(null);
        Assert.True(pageToken.IsCancellationRequested); Assert.True(metadataToken.IsCancellationRequested);
        oldPage.SetResult(Page("old/object"));
        oldMetadata.SetResult(StorageResult<ObjectMetadata>.Success(new("old-object", 1, null, null, "stale-content", null, new Dictionary<string, string>())));
        await Task.WhenAll(navigation, metadata);
        Assert.Equal("new/object", Assert.Single(vm.Rows).Key); Assert.Equal("new/", vm.PrefixInput);
        Assert.DoesNotContain("stale-content", vm.Metadata); Assert.Equal(BrowserState.Ready, vm.State);
    }
    [AvaloniaFact]
    public async Task Late_page_from_replaced_profile_cannot_repopulate_the_new_session()
    {
        using var f = new BrowserFixture();
        var response = new TaskCompletionSource<StorageResult<StoragePage<StorageEntry>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BrowserSession? old = null;
        f.Factory.CreateSession = (p, _, _) => old = new BrowserSession(p)
        { Objects = (_, _) => new BrowserListing<StorageEntry>(_ => { started.SetResult(); return response.Task; }) };
        await f.SelectAsync(); var pending = f.Browser.ConnectCommand.ExecuteAsync(null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var prior = f.Profiles.Snapshot.Profiles[0]; var id = Guid.NewGuid();
        var replacement = prior with { Id = id, Credential = prior.Credential! with { ProfileId = id } };
        f.Profiles.Snapshot = new(2, [replacement]);
        f.Browser.SelectedProfile = replacement;
        Assert.True(old!.IsInvalidated);
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        { Objects = (_, _) => new BrowserListing<StorageEntry>(_ => Task.FromResult(Page("new-profile-object"))) };
        await f.Browser.ConnectCommand.ExecuteAsync(null);
        response.SetResult(Page("old-profile-object")); await pending;
        Assert.Equal("new-profile-object", Assert.Single(f.Browser.Rows).Key);
        Assert.Equal(replacement.Id, f.Browser.SelectedProfile!.Id); Assert.True(f.Browser.IsConnected);
    }
    [AvaloniaFact]
    public async Task Selecting_another_row_cancels_metadata_and_discards_a_late_result()
    {
        using var f = new BrowserFixture();
        var response = new TaskCompletionSource<StorageResult<ObjectMetadata>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        {
            Objects = (_, _) => new BrowserListing<StorageEntry>(_ => Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new(
                [new ObjectEntry("first", null, null, null), new ObjectEntry("second", null, null, null)], true)))),
            Metadata = (_, _, ct) => { observed = ct; started.SetResult(); return response.Task; }
        };
        await f.ConnectAsync(); f.Browser.SelectedRow = f.Browser.Rows[0];
        var pending = f.Browser.PropertiesCommand.ExecuteAsync(null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Browser.SelectedRow = f.Browser.Rows[1]; Assert.True(observed.IsCancellationRequested);
        response.SetResult(StorageResult<ObjectMetadata>.Success(new("first", 1, null, null, "stale", null, new Dictionary<string, string>())));
        await pending; Assert.DoesNotContain("stale", f.Browser.Metadata); Assert.Equal("second", f.Browser.SelectedRow.Key);
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_replacement_or_disconnect_cancels_vault_prompt_and_never_publishes_client(bool replace)
    {
        using var f = new BrowserFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        f.Vault.Read = async ct => { observed = ct; started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(CredentialStatus.Found); };
        await f.SelectAsync(); f.Browser.AllowUnlock = true;
        var open = f.Browser.ConnectCommand.ExecuteAsync(null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (replace) f.Browser.SelectedProfile = f.Browser.SelectedProfile! with { Id = Guid.NewGuid() };
        else f.Browser.DisconnectCommand.Execute(null);
        await open.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observed.IsCancellationRequested); Assert.True(f.Vault.AllowedUnlock);
        Assert.Equal(0, f.Factory.Created); Assert.False(f.Browser.IsConnected); Assert.Equal(BrowserState.Disconnected, f.Browser.State);
    }
    [AvaloniaTheory]
    [InlineData(CredentialStatus.Missing, "missing")]
    [InlineData(CredentialStatus.Locked, "locked")]
    [InlineData(CredentialStatus.Unavailable, "unavailable")]
    [InlineData(CredentialStatus.Denied, "denied")]
    [InlineData(CredentialStatus.Cancelled, "cancelled")]
    [InlineData(CredentialStatus.RetrievalFailure, "failed")]
    public async Task Credential_failures_are_actionable_without_constructing_s3_client(CredentialStatus status, string expected)
    {
        using var f = new BrowserFixture(); f.Vault.Read = _ => Task.FromResult(new CredentialResult(status));
        await f.ConnectAsync(); Assert.Contains(expected, f.Browser.Status); Assert.Equal(0, f.Factory.Created);
        Assert.False(f.Browser.IsConnected); Assert.False(f.Browser.IsBusy);
    }
    [AvaloniaFact]
    public async Task Unexpected_exception_is_not_displayed_or_logged_as_provider_details()
    {
        using var f = new BrowserFixture();
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        { Objects = (_, _) => new BrowserListing<StorageEntry>(_ => throw new IOException("Authorization: synthetic-secret")) };
        await f.ConnectAsync(); Assert.Equal(BrowserState.Error, f.Browser.State);
        Assert.DoesNotContain("synthetic-secret", f.Browser.Status); Assert.Contains("BB-BROWSE", f.Browser.Status);
    }
    [AvaloniaFact]
    public async Task Persisted_profile_revision_change_disconnects_and_requires_explicit_reconnect()
    {
        using var f = new BrowserFixture(); BrowserSession? session = null;
        f.Factory.CreateSession = (p, _, _) => session = new(p);
        await f.ConnectAsync(); var selected = f.Browser.SelectedProfile!;
        await f.Manager.SaveAsync(selected.Id, selected.Revision, selected.Settings with { Name = "Changed" }, false, CredentialStorage.Secure, null, false);
        await BrowserFixture.UntilAsync(() => f.Browser.SelectedProfile?.Revision == 2);
        Assert.False(f.Browser.IsConnected); Assert.True(session!.IsInvalidated); Assert.True(session.Disposed);
        Assert.Empty(f.Browser.Rows); Assert.Equal(1, f.Factory.Created);
    }
    [AvaloniaFact]
    public async Task Million_object_traversal_retains_only_five_pages_and_does_not_prefetch()
    {
        using var f = new BrowserFixture(); var pagesRead = 0;
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p)
        {
            Objects = (_, _) => new BrowserListing<StorageEntry>(_ =>
            {
                var page = ++pagesRead;
                return Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new(
                    Enumerable.Range(0, 1000).Select(i => (StorageEntry)new ObjectEntry($"{page}/{i}", i, null, null)).ToArray(), page == 1000)));
            })
        };
        await f.ConnectAsync(); Assert.Equal(1, pagesRead);
        var first = new WeakReference(f.Browser.Rows[0]);
        for (var i = 1; i < 1000; i++)
        {
            await f.Browser.NextCommand.ExecuteAsync(null);
            Assert.InRange(f.Browser.RetainedRowCount, 1000, 5000);
        }
        Assert.Equal(1000, pagesRead); Assert.Equal(5, f.Browser.RetainedPageCount);
        Assert.False(f.Browser.NextCommand.CanExecute(null)); Assert.Contains("Page 1000", f.Browser.PageSummary);
        f.Browser.PreviousCommand.Execute(null); Assert.Contains("Page 999", f.Browser.PageSummary);
        await f.Browser.NextCommand.ExecuteAsync(null); Assert.Equal(1000, pagesRead);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.False(first.IsAlive);
        f.Browser.Disconnect(); Assert.Equal(0, f.Browser.RetainedRowCount);
    }
    [AvaloniaFact]
    public async Task Refresh_restores_exact_selection_when_present_and_reports_absence()
    {
        using var f = new BrowserFixture(); var key = "e\u0301//+%";
        f.Factory.CreateSession = (p, _, _) => new BrowserSession(p) { Objects = (_, _) => new BrowserListing<StorageEntry>(_ => Task.FromResult(Page(key))) };
        await f.ConnectAsync(); f.Browser.SelectedRow = f.Browser.Rows[0];
        await f.Browser.RefreshCommand.ExecuteAsync(null); Assert.Equal(key, f.Browser.SelectedRow!.Key);
        key = "different"; await f.Browser.RefreshCommand.ExecuteAsync(null);
        Assert.Null(f.Browser.SelectedRow); Assert.Contains("previous selection", f.Browser.Status);
    }
    [AvaloniaTheory]
    [InlineData("/", "")]
    [InlineData("//", "/")]
    [InlineData("a//", "a/")]
    [InlineData("/a/b/", "/a/")]
    [InlineData("a/b", "a/")]
    public async Task Up_removes_one_prefix_component_without_path_normalization(string prefix, string expected)
    {
        using var f = new BrowserFixture(prefix: prefix); await f.ConnectAsync();
        await f.Browser.UpCommand.ExecuteAsync(null); Assert.Equal(expected, f.Browser.PrefixInput);
    }
    [Fact]
    public void Visually_ambiguous_names_have_distinct_identity_and_escaped_control_display()
    {
        var composed = new BrowserRow("é", BrowserRowKind.Object);
        var decomposed = new BrowserRow("e\u0301", BrowserRowKind.Object);
        Assert.NotEqual(composed.ExactIdentity, decomposed.ExactIdentity);
        var unusual = new BrowserRow("<b>{Binding}\n\u202E\\/", BrowserRowKind.Object);
        Assert.DoesNotContain('\n', unusual.Name); Assert.DoesNotContain('\u202E', unusual.Name);
        Assert.Contains("\\u202E", unusual.Name); Assert.Contains("<b>{Binding}", unusual.Name);
    }
}
