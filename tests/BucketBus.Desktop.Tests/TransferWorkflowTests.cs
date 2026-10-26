using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace BucketBus.Desktop.Tests;

public sealed class TransferWorkflowTests
{
    [AvaloniaFact]
    public async Task Keyboard_upload_captures_exact_target_and_browsing_remains_independent()
    {
        using var remote = new BrowserFixture(prefix: "exact//é/"); await remote.ConnectAsync();
        using var local = new LocalBrowserViewModel(new LocalFixture()); await local.InitializeAsync();
        var files = new Files(); var factory = new Factory();
        using var vm = new TransferViewModel(new(remote.Manager, files, factory, new MemoryJournal()), local, remote.Browser);
        Assert.False(vm.UploadCommand.CanExecute(null));
        local.SelectedRows.Add(local.Rows.Single(r => r.Entry.Kind == LocalEntryKind.File && r.Entry.Name == "a-file"));
        Assert.True(vm.UploadCommand.CanExecute(null));
        var release = new TaskCompletionSource<TransferResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Run = (ct) => release.Task;
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: remote.Browser, local: local, transfers: vm) };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            window.KeyPressQwerty(PhysicalKey.U, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.U, RawInputModifiers.Control);
            await BrowserFixture.UntilAsync(() => factory.Key is not null);
            Assert.True(vm.IsBusy); Assert.False(vm.UploadCommand.CanExecute(null)); Assert.False(vm.DownloadCommand.CanExecute(null));
            Assert.Equal("exact//é/a-file", factory.Key);
            await local.NavigateAsync(Path.Combine(local.DirectoryPath, "other"));
            remote.Browser.PrefixInput = "other/"; await remote.Browser.GoCommand.ExecuteAsync(null);
            Assert.True(remote.Browser.IsConnected); Assert.True(vm.IsBusy);
            release.SetResult(new(TransferState.Completed, Verification: "SHA-256 fixture"));
            await vm.UploadCommand.ExecutionTask!;
            Assert.Contains("Completed", vm.Status); Assert.Equal("other/", remote.Browser.ActivePrefix); Assert.EndsWith("other", local.DirectoryPath);
            Assert.Equal(100, vm.Percent); Assert.Equal("1 / 1 B", vm.Bytes);
            var notice = window.FindControl<TextBlock>("UploadProviderNotice")!;
            Assert.True(notice.IsVisible);
            Assert.Contains("provider honoring create-only", notice.Text);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Preferences_reformat_active_progress_without_cancelling_transfer_or_changing_percent()
    {
        var display = new DisplayOptions(); using var remote = new BrowserFixture(display: display); await remote.ConnectAsync();
        using var local = new LocalBrowserViewModel(new LocalFixture(), display); await local.InitializeAsync();
        local.SelectedRows.Add(local.Rows.First(r => r.Entry.Kind == LocalEntryKind.File));
        var files = new Files { SourceLength = 3150938283 }; var factory = new Factory { Progress = new(170283, 3150938283, "Uploading") };
        var release = new TaskCompletionSource<TransferResult>(); CancellationToken observed = default;
        factory.Run = token => { observed = token; return release.Task; };
        using var vm = new TransferViewModel(new(remote.Manager, files, factory, new MemoryJournal()), local, remote.Browser, display);
        var pending = vm.UploadCommand.ExecuteAsync(null);
        await BrowserFixture.UntilAsync(() => observed.CanBeCanceled); Dispatcher.UIThread.RunJobs();
        Assert.Equal("166.3 KiB / 2.93 GiB", vm.Bytes); var percent = vm.Percent;
        display.Current = new(FileSizeDisplay.ExactBytes, RemoteNameDisplay.FullKey);
        Assert.Equal("170,283 / 3,150,938,283 B", vm.Bytes); Assert.Equal(percent, vm.Percent);
        Assert.False(observed.IsCancellationRequested); Assert.True(vm.IsBusy); Assert.Equal(1, factory.Created);
        release.SetResult(new(TransferState.Completed)); await pending;
        Assert.Contains("Completed", vm.Status);
    }
    [AvaloniaFact]
    public async Task Closing_requests_cancellation_and_uncertain_write_remains_explicit()
    {
        using var remote = new BrowserFixture(); await remote.ConnectAsync();
        using var local = new LocalBrowserViewModel(new LocalFixture()); await local.InitializeAsync();
        local.SelectedRows.Add(local.Rows.First(r => r.Entry.Kind == LocalEntryKind.File));
        var factory = new Factory(); CancellationToken observed = default;
        factory.Run = async ct => { observed = ct; try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { } return new(TransferState.OutcomeUnknown); };
        using var vm = new TransferViewModel(new(remote.Manager, new Files(), factory, new MemoryJournal()), local, remote.Browser);
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, local: local, browser: remote.Browser, transfers: vm) };
        window.Show();
        var task = vm.UploadCommand.ExecuteAsync(null); await BrowserFixture.UntilAsync(() => observed.CanBeCanceled);
        window.Close(); await task;
        Assert.True(observed.IsCancellationRequested);
        Assert.Contains("Outcome Unknown", TransferViewModel.Describe(new(TransferState.OutcomeUnknown)));
    }
    [AvaloniaFact]
    public async Task Download_failure_never_commits_and_success_refreshes_current_local_directory()
    {
        using var remote = new BrowserFixture(); await remote.ConnectAsync();
        remote.Browser.SelectedRow = new("prefix/é.txt", BrowserRowKind.Object, 1);
        var localFiles = new LocalFixture(); using var local = new LocalBrowserViewModel(localFiles); await local.InitializeAsync();
        var files = new Files(); var factory = new Factory { Run = _ => Task.FromResult(new TransferResult(TransferState.Failed, TransferError.IntegrityMismatch)) };
        using var vm = new TransferViewModel(new(remote.Manager, files, factory, new MemoryJournal()), local, remote.Browser);
        Assert.True(vm.DownloadCommand.CanExecute(null));
        await vm.DownloadCommand.ExecuteAsync(null); Assert.Equal(0, files.Commits); Assert.Equal(1, files.Cleanups);
        Assert.Contains("integrity", vm.Status); Assert.Equal("é.txt", files.Name);
        factory.Run = _ => Task.FromResult(new TransferResult(TransferState.Completed));
        var before = localFiles.Reads;
        await vm.DownloadCommand.ExecuteAsync(null); Assert.Equal(1, files.Commits); Assert.Equal(before + 1, localFiles.Reads);
        Assert.Equal(2, files.Cleanups);
    }
    [Fact]
    public async Task Stale_profile_and_cross_profile_binding_cannot_create_transfer_client()
    {
        using var remote = new BrowserFixture(); var factory = new Factory();
        var coordinator = new TransferCoordinator(remote.Manager, new Files(), factory, new MemoryJournal());
        var profile = remote.Profiles.Snapshot.Profiles[0];
        var result = await coordinator.RunAsync(profile with { Revision = profile.Revision + 1 }, "test-bucket", "key", "file", true, false, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferError.ProfileChanged, result.Error); Assert.Equal(0, factory.Created);
        result = await coordinator.RunAsync(profile with { Id = Guid.NewGuid() }, "test-bucket", "key", "file", true, false, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferError.ProfileChanged, result.Error); Assert.Equal(0, factory.Created);
    }
    [Fact]
    public async Task Commit_conflict_and_cleanup_obligation_are_visible()
    {
        using var remote = new BrowserFixture(); var files = new Files { Conflict = true, CleanupRequired = true };
        var coordinator = new TransferCoordinator(remote.Manager, files, new Factory(), new MemoryJournal());
        var result = await coordinator.RunAsync(remote.Profiles.Snapshot.Profiles[0], "test-bucket", "key", Path.GetFullPath("directory"), false, false, null, TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Failed, result.State); Assert.Equal(TransferError.Conflict, result.Error); Assert.True(result.CleanupRequired);
        Assert.Contains("cleanup", TransferViewModel.Describe(result)); Assert.Equal(1, files.Cleanups);
    }
    [Theory]
    [InlineData(TransferState.Failed, TransferError.Conflict, "Destination already exists")]
    [InlineData(TransferState.Failed, TransferError.PermissionDenied, "Permission denied")]
    [InlineData(TransferState.Failed, TransferError.AuthenticationFailed, "credentials or request signature")]
    [InlineData(TransferState.Failed, TransferError.ConditionalWriteUnsupported, "does not support this conditional upload")]
    [InlineData(TransferState.Failed, TransferError.RemoteRejected, "Provider rejected")]
    [InlineData(TransferState.OutcomeUnknown, TransferError.NetworkFailure, "Outcome Unknown")]
    public void Transfer_messages_distinguish_provider_rejections_and_unknown_writes(TransferState state, TransferError error, string expected)
    {
        var message = TransferViewModel.Describe(new(state, error));
        Assert.Contains(expected, message);
        Assert.DoesNotContain("qualification", message, StringComparison.OrdinalIgnoreCase);
        if (error is TransferError.Conflict or TransferError.ConditionalWriteUnsupported)
            Assert.Contains("No unconditional replacement was attempted", message);
    }
    private sealed class Files : ITransferFiles
    {
        public int Commits, Cleanups; public long SourceLength = 1; public string? Name; public bool Conflict, CleanupRequired;
        public Task<ITransferSource> OpenSourceAsync(string path, CancellationToken token) => Task.FromResult<ITransferSource>(new Source(SourceLength));
        public Task<ITransferDestination> CreateDestinationAsync(string path, string name, CancellationToken token)
        { Name = name; return Task.FromResult<ITransferDestination>(new Destination(this)); }
        private sealed class Source(long length) : ITransferSource
        {
            public Stream Content { get; } = new MemoryStream([1]); public long Length => length; public string Sha256 => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(new byte[] { 1 })); public string Md5 => "fixture";
            public Task ValidateAsync(CancellationToken t) => Task.CompletedTask;
            public ValueTask DisposeAsync() { Content.Dispose(); return ValueTask.CompletedTask; }
        }
        private sealed class Destination(Files files) : ITransferDestination
        {
            public Stream Content { get; } = new MemoryStream(); public bool CleanupRequired => files.CleanupRequired;
            public Task CommitAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); if (files.Conflict) throw new TransferException(TransferError.Conflict); files.Commits++; return Task.CompletedTask; }
            public ValueTask DisposeAsync() { files.Cleanups++; Content.Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Factory : IObjectTransferFactory
    {
        public int Created; public string? Key; public TransferProgress Progress = new(1, 1, "Uploading");
        public Func<CancellationToken, Task<TransferResult>> Run = _ => Task.FromResult(new TransferResult(TransferState.Completed));
        public IObjectTransferSession Create(ConnectionProfile p, Credentials c) { Created++; return new Session(this); }
        private sealed class Session(Factory factory) : IObjectTransferSession
        {
            public Task<TransferResult> UploadAsync(string b, string k, ITransferSource s, IProgress<TransferProgress>? p, CancellationToken ct, Guid? operationId = null) { factory.Key = k; p?.Report(factory.Progress); return factory.Run(ct); }
            public Task<TransferResult> DownloadAsync(string b, string k, Stream s, IProgress<TransferProgress>? p, CancellationToken ct) { factory.Key = k; return factory.Run(ct); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
