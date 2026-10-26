using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BucketBus.Core;
using BucketBus.Desktop.Presentation;
using BucketBus.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace BucketBus.Desktop.Tests;

public sealed class DisplayPreferencesTests
{
    [Theory]
    [InlineData("prefix/é é % +.txt", "prefix/", "é é % +.txt")]
    [InlineData("prefix//a\\b", "prefix/", "/a\\b")]
    [InlineData("prefix/../file", "prefix/", "../file")]
    [InlineData("elsewhere/file", "prefix/", "elsewhere/file")]
    [InlineData("prefix/\u202E\n", "prefix/", "\u202E\n")]
    [InlineData("prefix/folder/", "prefix/", "folder/")]
    public void Relative_names_preserve_unicode_and_exact_identity(string key, string prefix, string relative)
    {
        Assert.Equal(BrowserText.Display(relative), RemoteNameConverter.Display(key, BrowserRowKind.Object, prefix, RemoteNameDisplay.Relative));
        Assert.Equal(BrowserText.Display(key), RemoteNameConverter.Display(key, BrowserRowKind.Object, prefix, RemoteNameDisplay.FullKey));
        Assert.Equal(BrowserText.Display(key), RemoteNameConverter.Display(key, BrowserRowKind.Bucket, prefix, RemoteNameDisplay.Relative));
    }
    [Fact]
    public void Prefix_markers_and_empty_names_remain_visible()
    {
        Assert.Equal("folder/", RemoteNameConverter.Display("prefix/folder/", BrowserRowKind.Prefix, "prefix/", RemoteNameDisplay.Relative));
        Assert.Equal("(key equals current prefix)", RemoteNameConverter.Display("prefix/", BrowserRowKind.Object, "prefix/", RemoteNameDisplay.Relative));
        Assert.Equal("prefix/", RemoteNameConverter.Display("prefix/", BrowserRowKind.Object, "prefix/", RemoteNameDisplay.FullKey));
    }
    [AvaloniaFact]
    public async Task Settings_controls_update_actual_cells_and_cached_metadata_without_requests_or_selection_changes()
    {
        var display = new DisplayOptions(); var store = new PreferencesFake(); var settings = new SettingsViewModel(store, display);
        using var f = new BrowserFixture(prefix: "prefix/", display: display); var lists = 0; var heads = 0;
        f.Factory.CreateSession = (profile, _, _) => new BrowserSession(profile)
        {
            Objects = (_, _) => new BrowserListing<StorageEntry>(_ =>
            {
                lists++; return Task.FromResult(StorageResult<StoragePage<StorageEntry>>.Success(new([
                    new ObjectEntry("prefix/é.txt", 170283, null, null), new PrefixEntry("prefix/folder/"), new ObjectEntry("prefix/", 0, null, null)], true)));
            }),
            Metadata = (_, key, _) => { heads++; return Task.FromResult(StorageResult<ObjectMetadata>.Success(new(key, 170283, null, "opaque", null, null, new Dictionary<string, string>()))); }
        };
        await f.ConnectAsync(); f.Browser.SelectedRow = f.Browser.Rows[0]; await f.Browser.PropertiesCommand.ExecuteAsync(null);
        var files = new LocalFixture(); using var local = new LocalBrowserViewModel(files, display); await local.InitializeAsync();
        var selected = f.Browser.SelectedRow; var rows = f.Browser.Rows; var page = f.Browser.PageSummary;
        var main = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Browser, local: local, settings: settings);
        main.CompleteInitialization(); var window = new MainWindow { DataContext = main };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var remote = window.GetVisualDescendants().OfType<RemoteBrowserView>().Single();
            var list = remote.FindControl<ListBox>("ObjectList")!;
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "é.txt");
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "166.3 KiB");
            Assert.Contains("166.3 KiB", f.Browser.Metadata); Assert.Contains("170,283 B", f.Browser.Metadata);
            var button = window.FindControl<Button>("SettingsButton")!; button.Flyout!.ShowAt(button);
            var panel = Assert.IsType<StackPanel>(Assert.IsType<Flyout>(button.Flyout).Content);
            var combos = panel.GetVisualDescendants().OfType<ComboBox>().ToArray(); Assert.Equal(2, combos.Length);
            combos[0].SelectedItem = FileSizeDisplay.ExactBytes; combos[1].SelectedItem = RemoteNameDisplay.FullKey;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(FileSizeDisplay.ExactBytes, display.Current.FileSizes); Assert.Equal(RemoteNameDisplay.FullKey, display.Current.RemoteNames);
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "prefix/é.txt");
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "170,283 B");
            Assert.Contains("Size: 170,283 B", f.Browser.Metadata); Assert.Contains("prefix/", f.Browser.Metadata);
            Assert.Same(selected, f.Browser.SelectedRow); Assert.Same(rows, f.Browser.Rows);
            Assert.Equal(page, f.Browser.PageSummary); Assert.Equal("prefix/", f.Browser.ActivePrefix);
            Assert.Equal(1, lists); Assert.Equal(1, heads); Assert.Equal(1, files.Reads);
            await settings.SaveCommand.ExecuteAsync(null); Assert.Equal(display.Current, store.Saved); Assert.Contains("saved", settings.Status);
            button.Flyout.Hide();
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task Numeric_sorting_is_independent_of_display_and_preserves_selection()
    {
        var display = new DisplayOptions(); var files = new LocalFixture();
        files.Read = (path, _) => Task.FromResult(new LocalDirectoryResult(path, [
            new("a", Path.Combine(path, "a"), LocalEntryKind.File, false, 170283, null),
            new("b", Path.Combine(path, "b"), LocalEntryKind.File, false, 900, null),
            new("c", Path.Combine(path, "c"), LocalEntryKind.File, false, 2048, null)]));
        using var local = new LocalBrowserViewModel(files, display); await local.InitializeAsync();
        var selected = local.Rows[0]; local.SelectedRows.Add(selected); local.SortCommand.Execute(LocalSortField.Size);
        Assert.Equal(new long?[] { 900, 2048, 170283 }, local.Rows.Select(r => r.Entry.Size));
        var rows = local.Rows; display.Current = new(FileSizeDisplay.ExactBytes, RemoteNameDisplay.FullKey);
        Assert.Same(rows, local.Rows); Assert.Same(selected, Assert.Single(local.SelectedRows)); Assert.Equal(1, files.Reads);
    }
    [Fact]
    public async Task Save_failure_keeps_immediate_choices_and_never_claims_saved()
    {
        var display = new DisplayOptions(); var store = new PreferencesFake { Fail = true }; var settings = new SettingsViewModel(store, display);
        await settings.InitializeAsync(); settings.FileSizes = FileSizeDisplay.ExactBytes;
        Assert.Equal(FileSizeDisplay.ExactBytes, display.Current.FileSizes);
        await settings.SaveCommand.ExecuteAsync(null); Assert.Contains("Could not save", settings.Status); Assert.Contains("session only", settings.Status);
    }
    [Fact]
    public async Task Settings_restart_restores_preferences_and_late_load_does_not_overwrite_user_edits()
    {
        var store = new PreferencesFake(); var first = new SettingsViewModel(store, new()); first.RemoteNames = RemoteNameDisplay.FullKey;
        await first.SaveCommand.ExecuteAsync(null);
        var second = new SettingsViewModel(store, new()); await second.InitializeAsync(); Assert.Equal(RemoteNameDisplay.FullKey, second.RemoteNames);
        var read = new TaskCompletionSource<PreferencesReadResult>(); store.Read = read.Task;
        var third = new SettingsViewModel(store, new()); var pending = third.InitializeAsync(); third.FileSizes = FileSizeDisplay.ExactBytes;
        read.SetResult(new(new(), PreferencesReadStatus.Loaded)); await pending; Assert.Equal(FileSizeDisplay.ExactBytes, third.FileSizes);
    }
    [Fact]
    public async Task Transfer_history_changes_presentation_without_changing_records_or_recovery_eligibility()
    {
        var display = new DisplayOptions(); var record = new TransferRecord(Guid.NewGuid(), 1, Guid.NewGuid(), 1, "target", "https://storage.example.test",
            "bucket", "key", Path.GetFullPath("file"), true, TransferState.OutcomeUnknown, Bytes: 170283, Total: 3150938283, Sha256: Convert.ToBase64String(new byte[32]));
        var row = new TransferHistoryRow(record, display); Assert.Contains("166.3 KiB", row.Details); Assert.Contains("170,283 B", row.Details);
        display.Current = new(FileSizeDisplay.ExactBytes); row.RefreshDisplay(); Assert.Contains("3,150,938,283 B", row.Details);
        Assert.Same(record, row.Record); Assert.True(row.Record.CanReconcile); Assert.False(row.Record.CanRestart);
        var store = new PreferencesFake { Fail = true }; var settings = new SettingsViewModel(store, display);
        await settings.SaveCommand.ExecuteAsync(null); Assert.Same(record, row.Record);
    }
    private sealed class PreferencesFake : IPreferencesStore
    {
        public DisplayPreferences Saved = new(); public bool Fail; public Task<PreferencesReadResult>? Read;
        public Task<PreferencesReadResult> ReadAsync(CancellationToken token) => Read ?? Task.FromResult(new PreferencesReadResult(Saved, PreferencesReadStatus.Loaded));
        public Task<bool> SaveAsync(DisplayPreferences value, CancellationToken token) { if (!Fail) Saved = value; return Task.FromResult(!Fail); }
    }
}
