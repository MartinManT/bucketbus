using System.Diagnostics;
using System.Net;
using Avalonia.Headless.XUnit;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;
using BucketBus.Platform;
using BucketBus.S3;

namespace BucketBus.Desktop.Tests;

public sealed class TransferRecoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Journal_failure_before_effect_blocks_upload_and_download(int failure)
    {
        using var fixture = new RecoveryDirectory(); using var profiles = new BrowserFixture();
        foreach (var upload in new[] { true, false })
        {
            var journal = new MemoryJournal { FailAt = failure }; var factory = new FakeFactory();
            var coordinator = new TransferCoordinator(profiles.Manager, new TransferFiles(), factory, journal);
            var result = await coordinator.RunAsync(profiles.Profiles.Snapshot.Profiles[0], "test-bucket", "result", upload ? fixture.Source : fixture.Path, upload, false, null, Token);
            Assert.Equal(TransferError.JournalUnavailable, result.Error); Assert.Equal(0, factory.Calls);
            Assert.False(File.Exists(Path.Combine(fixture.Path, "result")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path, ".bucketbus-*"));
        }
    }
    [Fact]
    public async Task Failure_recording_success_keeps_durable_running_not_false_success()
    {
        using var fixture = new RecoveryDirectory(); using var profiles = new BrowserFixture();
        var journal = new MemoryJournal { FailAt = 3 }; var factory = new FakeFactory();
        var coordinator = new TransferCoordinator(profiles.Manager, new TransferFiles(), factory, journal);
        var result = await coordinator.RunAsync(profiles.Profiles.Snapshot.Profiles[0], "test-bucket", "key", fixture.Source, true, false, null, Token);
        Assert.Equal(TransferState.RequiresReconciliation, result.State); Assert.Equal(TransferError.JournalUnavailable, result.Error);
        var record = Assert.Single(journal.Records.Values); Assert.Equal(TransferState.Running, record.State); Assert.Equal(record.Id, factory.Operation);
    }
    [Fact]
    public async Task Unknown_upload_cannot_be_bypassed_by_clicking_upload_again()
    {
        using var fixture = new RecoveryDirectory(); using var profiles = new BrowserFixture();
        var journal = new MemoryJournal(); var factory = new FakeFactory { Result = new(TransferState.OutcomeUnknown) };
        var coordinator = new TransferCoordinator(profiles.Manager, new TransferFiles(), factory, journal);
        var profile = profiles.Profiles.Snapshot.Profiles[0];
        Assert.Equal(TransferState.OutcomeUnknown, (await coordinator.RunAsync(profile, "test-bucket", "key", fixture.Source, true, false, null, Token)).State);
        Assert.Equal(TransferError.RecoveryUnavailable, (await coordinator.RunAsync(profile, "test-bucket", "key", fixture.Source, true, false, null, Token)).Error);
        Assert.Equal(1, factory.Calls);
        var record = Assert.Single(journal.Records.Values);
        Assert.Equal(TransferState.OutcomeUnknown, (await coordinator.RecoverAsync(record.Id, true, false, null, Token)).State);
        Assert.Equal(1, factory.Calls); Assert.False(journal.Records[record.Id].Resolved);
    }
    [Fact]
    public async Task Download_restart_requires_confirmation_and_preserves_identity()
    {
        using var fixture = new RecoveryDirectory(); using var profiles = new BrowserFixture();
        var files = new TransferFiles(); var journal = new MemoryJournal(); var factory = new FakeFactory();
        var profile = profiles.Profiles.Snapshot.Profiles[0];
        TemporaryReceipt? receipt;
        await using (var previous = await files.CreateDestinationAsync(fixture.Path, "result", Token)) receipt = previous.Receipt;
        Assert.NotNull(receipt);
        var record = Saved(profile, fixture.Path, false) with { Temporary = receipt, Phase = TransferPhase.Transferring };
        journal.Records[record.Id] = record;
        var coordinator = new TransferCoordinator(profiles.Manager, files, factory, journal);
        Assert.Equal(TransferError.RecoveryUnavailable, (await coordinator.RecoverAsync(record.Id, false, false, null, Token)).Error);
        Assert.Equal(0, factory.Calls);
        Assert.Equal(TransferState.Completed, (await coordinator.RecoverAsync(record.Id, true, false, null, Token)).State);
        Assert.Equal("fixture", await File.ReadAllTextAsync(Path.Combine(fixture.Path, "result"), Token));
        Assert.Equal(record.Id, Assert.Single(journal.Records.Values).Id); Assert.Equal(1, factory.Calls);
    }
    [Theory]
    [InlineData("profile")] [InlineData("destination")] [InlineData("ownership")]
    public async Task Unsafe_restart_remains_unresolved(string change)
    {
        using var fixture = new RecoveryDirectory(); using var profiles = new BrowserFixture();
        var files = new TransferFiles(); var journal = new MemoryJournal(); var factory = new FakeFactory();
        var profile = profiles.Profiles.Snapshot.Profiles[0]; TemporaryReceipt? receipt;
        await using (var previous = await files.CreateDestinationAsync(fixture.Path, "result", Token)) receipt = previous.Receipt;
        var record = Saved(profile, fixture.Path, false) with { Temporary = change == "ownership" ? null : receipt };
        journal.Records[record.Id] = record;
        if (change == "destination") await File.WriteAllTextAsync(Path.Combine(fixture.Path, "result"), "existing", Token);
        if (change == "profile") profiles.Profiles.Snapshot = new(2, [profile with { Revision = profile.Revision + 1 }]);
        var coordinator = new TransferCoordinator(profiles.Manager, files, factory, journal);
        var result = await coordinator.RecoverAsync(record.Id, true, false, null, Token);
        Assert.NotEqual(TransferState.Completed, result.State); Assert.Equal(0, factory.Calls);
        Assert.False(journal.Records[record.Id].Resolved);
        if (change == "destination") Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(fixture.Path, "result"), Token));
    }
    [AvaloniaFact]
    public async Task History_load_is_credential_free_and_unresolved_actions_are_explicit()
    {
        using var fixture = new RecoveryDirectory(); using var profiles = new BrowserFixture();
        profiles.Vault.Read = _ => throw new InvalidOperationException("Must not resolve credentials");
        var journal = new MemoryJournal(); var factory = new FakeFactory(); var profile = profiles.Profiles.Snapshot.Profiles[0];
        var upload = Saved(profile, fixture.Source, true); journal.Records[upload.Id] = upload;
        using var local = new LocalBrowserViewModel(new LocalFixture());
        using var vm = new TransferViewModel(new(profiles.Manager, new TransferFiles(), factory, journal), local, profiles.Browser);
        await vm.InitializeAsync(); vm.SelectedHistory = Assert.Single(vm.History);
        Assert.True(vm.ReconcileCommand.CanExecute(null)); Assert.False(vm.RestartCommand.CanExecute(null)); Assert.False(vm.DismissCommand.CanExecute(null));
        Assert.Contains("Upload restart is unavailable", vm.RecoveryDetails); Assert.Equal(0, factory.Created);
        Assert.False(profiles.Browser.IsConnected);
        var window = new MainWindow { DataContext = new MainWindowViewModel(Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance,
            local: local, browser: profiles.Browser, transfers: vm) };
        try
        {
            window.Show();
            var panel = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Expander>(window, "TransferHistoryPanel")!;
            Assert.False(panel.IsExpanded); panel.IsExpanded = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var height = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Border>(window, "LocalPane")!.Bounds.Height;
            Assert.True(height > 180, $"Pane height {height}; window {window.Bounds.Height}; history {panel.Bounds.Height}");
            var list = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.ListBox>(window, "TransferHistory")!;
            Assert.Single(list.Items); list.SelectedIndex = 0;
            Assert.Equal(upload.Id, vm.SelectedHistory!.Record.Id);
        }
        finally { window.Close(); }

    }
    [Theory]
    [InlineData("before-put")] [InlineData("after-put")] [InlineData("lost-response")]
    public async Task Subprocess_termination_around_synthetic_put_recovers_unknown_without_replay(string mode)
    {
        using var fixture = new RecoveryDirectory();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(TransferRecoveryTests).Assembly.Location); start.ArgumentList.Add("-method"); start.ArgumentList.Add("*TransferRecoveryTests.Crash_upload_worker");
        start.Environment["BUCKETBUS_TRANSFER_CRASH_DIR"] = fixture.Path; start.Environment["BUCKETBUS_TRANSFER_CRASH_MODE"] = mode;
        using var child = Process.Start(start)!; var output = child.StandardOutput.ReadToEndAsync(Token); var error = child.StandardError.ReadToEndAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(fixture.Path, "ready")))
            { Assert.False(child.HasExited, "Crash worker exited before the transfer barrier."); await Task.Delay(20, deadline.Token); }
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(Token); await output; await error; }
        await using var journal = new SqliteTransferJournal(Path.Combine(fixture.Path, "journal"));
        var recovered = Assert.Single(await journal.ReadAsync(Token));
        Assert.Equal(TransferState.OutcomeUnknown, recovered.State); Assert.NotNull(recovered.Sha256);
        if (mode == "before-put") Assert.False(File.Exists(Path.Combine(fixture.Path, "put")));
        else Assert.Equal(recovered.Id.ToString("N"), await File.ReadAllTextAsync(Path.Combine(fixture.Path, "put"), Token));
        using var profiles = new BrowserFixture(); profiles.Vault.Read = _ => throw new InvalidOperationException("Offline startup");
        var factory = new FakeFactory(); var coordinator = new TransferCoordinator(profiles.Manager, new TransferFiles(), factory, journal);
        Assert.Single(await coordinator.HistoryAsync(Token)); Assert.Equal(0, factory.Created);
        // Original source may disappear/change. Recovery listing and saved verification evidence do not depend on it.
        File.Delete(fixture.Source); Assert.Single(await coordinator.HistoryAsync(Token));
    }
    [Fact]
    public async Task Crash_upload_worker()
    {
        var root = Environment.GetEnvironmentVariable("BUCKETBUS_TRANSFER_CRASH_DIR");
        var mode = Environment.GetEnvironmentVariable("BUCKETBUS_TRANSFER_CRASH_MODE");
        if (root is null || mode is null) return;
        using var profiles = new BrowserFixture(); await using var journal = new SqliteTransferJournal(Path.Combine(root, "journal"));
        var coordinator = new TransferCoordinator(profiles.Manager, new TransferFiles(), new CrashFactory(root, mode), new CrashJournal(journal, root, mode));
        var result = await coordinator.RunAsync(profiles.Profiles.Snapshot.Profiles[0], "test-bucket", "result", Path.Combine(root, "source"), true, false, null, Token);
        Assert.Equal(TransferState.OutcomeUnknown, result.State);
        await Pause(root);
    }
    private static async Task Pause(string root)
    { await File.WriteAllTextAsync(Path.Combine(root, "ready"), "ready"); await Task.Delay(Timeout.InfiniteTimeSpan, Token); }
    private sealed class CrashJournal(ITransferJournal inner, string root, string mode) : ITransferJournal
    {
        public Task<IReadOnlyList<TransferRecord>> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
        public Task DismissAsync(Guid id, CancellationToken ct) => inner.DismissAsync(id, ct);
        public async Task SaveAsync(TransferRecord record, CancellationToken ct)
        { await inner.SaveAsync(record, ct); if (mode == "before-put" && record.State == TransferState.Running) await Pause(root); }
    }
    private sealed class CrashFactory(string root, string mode) : IObjectTransferFactory
    {
        public IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials) =>
            S3TransferFactory.Create(profile, credentials, new Transport(root, mode), TimeSpan.FromMinutes(1));
        private sealed class Transport(string root, string mode) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (request.Method == HttpMethod.Put)
                {
                    await request.Content!.CopyToAsync(Stream.Null, token);
                    await File.WriteAllTextAsync(Path.Combine(root, "put"), request.Headers.GetValues("x-amz-meta-bucketbus-operation").Single(), token);
                    if (mode == "after-put") await Pause(root);
                    throw new IOException("Synthetic lost PUT response");
                }
                return new(HttpStatusCode.NotFound) { Content = new StringContent("<Error><Code>NoSuchKey</Code></Error>") };
            }
        }
    }
    private static TransferRecord Saved(ConnectionProfile profile, string path, bool upload) => new(Guid.NewGuid(), 2,
        profile.Id, profile.Revision, profile.Settings.TargetFingerprint(), profile.Settings.Endpoint, "test-bucket", "result", path, upload,
        upload ? TransferState.OutcomeUnknown : TransferState.Interrupted, Total: upload ? 7 : null,
        Sha256: upload ? Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("fixture"u8)) : null, Created: DateTimeOffset.UtcNow, Updated: DateTimeOffset.UtcNow);
    private sealed class FakeFactory : IObjectTransferFactory
    {
        public int Created, Calls; public Guid? Operation; public TransferResult Result = new(TransferState.Completed);
        public IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials) { Created++; return new Session(this); }
        private sealed class Session(FakeFactory owner) : IObjectTransferSession
        {
            public Task<TransferResult> UploadAsync(string b, string k, ITransferSource source, IProgress<TransferProgress>? progress, CancellationToken ct, Guid? operationId = null)
            { owner.Calls++; owner.Operation = operationId; return Task.FromResult(owner.Result); }
            public async Task<TransferResult> DownloadAsync(string b, string k, Stream output, IProgress<TransferProgress>? progress, CancellationToken ct)
            { owner.Calls++; await output.WriteAsync("fixture"u8.ToArray(), ct); return owner.Result; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class RecoveryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bucketbus-recovery-" + Guid.NewGuid().ToString("N"));
        public string Source => System.IO.Path.Combine(Path, "source");
        public RecoveryDirectory() { Directory.CreateDirectory(Path); File.WriteAllText(Source, "fixture"); }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
