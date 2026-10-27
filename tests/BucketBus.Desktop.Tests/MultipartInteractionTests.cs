using System.Net;
using BucketBus.S3;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace BucketBus.Desktop.Tests;

public sealed class MultipartInteractionTests
{
    [AvaloniaTheory]
    [InlineData("unchanged")]
    [InlineData("profile-edited")]
    [InlineData("locked")]
    public async Task Actual_abort_click_runs_and_reports_the_result(string scenario)
    {
        await using var f = new MultipartWorkflowTests.Fixture(); f.Remote.Stop = "after-ack";
        await f.Add(); await f.Queue.RunAsync(false, null, TestContext.Current.CancellationToken);
        if (scenario == "profile-edited")
        {
            var profile = f.Profiles.Profiles.Snapshot.Profiles[0];
            f.Profiles.Profiles.Snapshot = new(2, [profile with { Revision = profile.Revision + 1 }]);
        }
        if (scenario == "locked") f.Profiles.Vault.Read = _ => Task.FromResult(new CredentialResult(CredentialStatus.Locked));
        using var local = new LocalBrowserViewModel(new LocalFixture());
        await using var vm = new TransferViewModel(f.Coordinator, f.Queue, local, f.Profiles.Browser);
        await vm.InitializeAsync(); vm.SelectedHistory = Assert.Single(vm.History);
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Profiles.Browser, local: local, transfers: vm) };
        try
        {
            window.Show(); window.FindControl<Expander>("TransferHistoryPanel")!.IsExpanded = true;
            Layout(window);
            var confirm = window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Confirm multipart recovery action"));
            var abort = window.GetVisualDescendants().OfType<Button>().Single(c => Equals(c.Content, "Abort incomplete upload"));
            Assert.Same(vm.AbortMultipartCommand, abort.Command);
            Assert.False(abort.IsEffectivelyEnabled);
            Click(window, confirm);
            Assert.True(vm.ConfirmMultipart); Assert.True(abort.IsEffectivelyEnabled);
            Click(window, abort);
            Assert.NotNull(vm.AbortMultipartCommand.ExecutionTask);
            await vm.AbortMultipartCommand.ExecutionTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Layout(window);
            Assert.False(vm.IsBusy); Assert.False(vm.ConfirmMultipart);
            if (scenario == "unchanged")
            {
                Assert.Equal(1, f.Remote.AbortCalls); Assert.True(Assert.Single(vm.History).Record.Resolved);
                Assert.Contains("Cancelled", vm.Status);
            }
            else
            {
                Assert.Equal(0, f.Remote.AbortCalls); Assert.False(Assert.Single(vm.History).Record.Resolved);
                Assert.Contains(scenario == "profile-edited" ? "Profile changed" : "Explicit credentials unavailable", vm.Status);
            }
            var details = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Operation details"));
            details.IsExpanded = true; Layout(window);
            var tabs = window.FindControl<TabControl>("InspectorTabs")!;
            var feedback = tabs.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RecoveryStatus" || t.Text == vm.Status);
            var bounds = new Rect(feedback.TranslatePoint(default, tabs)!.Value, feedback.Bounds.Size);
            Assert.True(new Rect(tabs.Bounds.Size).Contains(bounds), $"Recovery result is outside the inspector: {bounds}; inspector {tabs.Bounds}");
            foreach (var viewport in feedback.GetVisualAncestors().OfType<ScrollViewer>())
                Assert.True(new Rect(viewport.Bounds.Size).Contains(new Rect(feedback.TranslatePoint(default, viewport)!.Value, feedback.Bounds.Size)), "Recovery result is hidden below operation details");
            ResponsiveLayoutTests.Capture(window, "abort-result-" + scenario);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData("redirect", "redirect")]
    [InlineData("missing-bucket", "NoSuchBucket")]
    [InlineData("verification-missing-key", "NoSuchKey")]
    [InlineData("unknown-code", "unclassified provider error")]
    public async Task Sdk_abort_rejection_explains_stage_without_resolving_or_replaying(string scenario, string diagnostic)
    {
        await using var f = new MultipartWorkflowTests.Fixture(); f.Remote.Stop = "after-ack";
        await f.Add(); await f.Queue.RunAsync(false, null, TestContext.Current.CancellationToken);
        var transport = new AbortResponses(scenario);
        var coordinator = new TransferCoordinator(f.Profiles.Manager, f.Files, new SdkAbortFactory(transport), f.Journal);
        using var local = new LocalBrowserViewModel(new LocalFixture());
        await using var vm = new TransferViewModel(coordinator, f.Queue, local, f.Profiles.Browser);
        await vm.InitializeAsync(); vm.SelectedHistory = Assert.Single(vm.History);
        var window = new MainWindow { DataContext = new MainWindowViewModel(NullLogger<MainWindowViewModel>.Instance, browser: f.Profiles.Browser, local: local, transfers: vm) };
        try
        {
            window.Show(); window.FindControl<Expander>("TransferHistoryPanel")!.IsExpanded = true; Layout(window);
            Click(window, window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Confirm multipart recovery action")));
            Click(window, window.GetVisualDescendants().OfType<Button>().Single(c => Equals(c.Content, "Abort incomplete upload")));
            await vm.AbortMultipartCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); Layout(window);
            var record = Assert.Single(vm.History).Record;
            Assert.False(record.Resolved); Assert.True(record.CleanupRequired); Assert.True(record.CanAbortMultipart);
            Assert.Equal(TransferError.RemoteRejected, record.Error);
            Assert.Equal(scenario == "verification-missing-key" ? 2 : 1, transport.Methods.Count);
            Assert.Equal(HttpMethod.Delete, transport.Methods[0]);
            Assert.Contains(diagnostic, vm.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(diagnostic, window.FindControl<TextBlock>("RecoveryStatus")!.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(MultipartPhase.Aborting, record.Multipart!.Phase);
            Assert.DoesNotContain("PRIVATE-FIXTURE-DATA", vm.Status);
            ResponsiveLayoutTests.Capture(window, "abort-sdk-" + scenario);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData("absent")]
    [InlineData("already-absent")]
    public async Task Sdk_confirmed_absence_resolves_and_allows_explicit_fresh_enqueue(string scenario)
    {
        await using var f = new MultipartWorkflowTests.Fixture(); f.Remote.Stop = "after-ack";
        await f.Add(); await f.Queue.RunAsync(false, null, TestContext.Current.CancellationToken);
        var transport = new AbortResponses(scenario);
        var coordinator = new TransferCoordinator(f.Profiles.Manager, f.Files, new SdkAbortFactory(transport), f.Journal);
        using var local = new LocalBrowserViewModel(new LocalFixture());
        await using var vm = new TransferViewModel(coordinator, f.Queue, local, f.Profiles.Browser);
        await vm.InitializeAsync(); vm.SelectedHistory = Assert.Single(vm.History); vm.ConfirmMultipart = true;
        await vm.AbortMultipartCommand.ExecuteAsync(null);
        Assert.Equal(TransferState.Cancelled, Assert.Single(vm.History).Record.State);
        Assert.Equal(MultipartPhase.Aborted, Assert.Single(vm.History).Record.Multipart!.Phase);
        Assert.True(Assert.Single(vm.History).Record.Resolved); Assert.Contains("cleanup confirmed", vm.RecoveryFeedback);
        Assert.Equal(new[] { HttpMethod.Delete, HttpMethod.Get }, transport.Methods);
        await f.Add(); await vm.InitializeAsync();
        Assert.Single(vm.History, r => r.Record.State == TransferState.Queued);
        Assert.Equal(2, transport.Methods.Count); // Enqueue never starts another upload.
    }
    [Theory]
    [InlineData("authority", "authority")]
    [InlineData("method", "method")]
    [InlineData("key", "path")]
    [InlineData("upload-id", "parameters")]
    [InlineData("unsigned", "authorization")]
    [InlineData("repeat", "repeat")]
    public async Task Abort_guard_rejects_unexpected_request_without_exposing_values(string fault, string diagnostic)
    {
        using var fixture = new BrowserFixture(); var profile = fixture.Profiles.Snapshot.Profiles[0];
        var transport = new AbortResponses("unused");
        using var guard = new TransferTransport(profile, transport);
        guard.BeginMultipart("test-bucket", "key", HttpMethod.Delete, "OPERATION-OPAQUE-ID-EXACT");
        using var client = new HttpClient(guard);
        HttpRequestMessage Request()
        {
            var uri = (fault == "authority" ? "https://other.example.test" : profile.Settings.Endpoint) + "/test-bucket/"
                + (fault == "key" ? "other-key" : "key") + "?uploadId=" + (fault == "upload-id" ? "other-id" : "OPERATION-OPAQUE-ID-EXACT");
            var request = new HttpRequestMessage(fault == "method" ? HttpMethod.Put : HttpMethod.Delete, uri);
            if (fault != "unsigned") request.Headers.TryAddWithoutValidation("Authorization", "AWS4-HMAC-SHA256 Credential=SYNTHETIC/20261027/us-east-1/s3/aws4_request");
            return request;
        }
        if (fault == "repeat")
        {
            using var first = Request();
            using var response = await client.SendAsync(first, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        using var request = Request();
        var error = await Assert.ThrowsAsync<TransferException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(TransferError.RemoteRejected, error.Error);
        Assert.Contains(diagnostic, guard.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPERATION-OPAQUE-ID-EXACT", guard.FailureReason);
        Assert.DoesNotContain(profile.Settings.Endpoint, guard.FailureReason);
        Assert.Equal(fault == "repeat" ? 1 : 0, transport.Methods.Count);
    }
    private sealed class SdkAbortFactory(HttpMessageHandler transport) : IObjectTransferFactory
    {
        public IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials) =>
            S3TransferFactory.Create(profile, credentials, transport, TimeSpan.FromSeconds(5));
    }
    private sealed class AbortResponses(string scenario) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Methods.Add(request.Method);
            if (scenario == "unused") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) });
            Assert.Contains("uploadId=owned-id", request.RequestUri!.Query);
            if (scenario == "redirect") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) });
            if (scenario is "missing-bucket" or "unknown-code" or "already-absent" || request.Method == HttpMethod.Get)
            {
                var code = scenario switch { "missing-bucket" => "NoSuchBucket", "unknown-code" => "PRIVATE-FIXTURE-DATA", "absent" or "already-absent" => "NoSuchUpload", _ => "NoSuchKey" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                { Content = new StringContent("<Error><Code>" + code + "</Code></Error>", System.Text.Encoding.UTF8, "application/xml") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) });
        }
    }
    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(Math.Min(12, control.Bounds.Width / 2), control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout(window);
    }
}
