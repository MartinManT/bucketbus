using System.Net;
using Avalonia.Headless.XUnit;
using BucketBus.Core;
using BucketBus.Desktop.ViewModels;

namespace BucketBus.Desktop.Tests;

public sealed class BrowserProtocolTests
{
    [AvaloniaFact]
    public async Task Actual_sdk_discovery_prefix_navigation_and_explicit_metadata_preserve_identity()
    {
        using var f = new BrowserFixture(bucket: null);
        var heads = 0; var gets = 0; var requested = new List<string>();
        const string key = "/e\u0301/+%2F\n<markup>";
        f.UseSdk((request, _) =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            if (request.Method == HttpMethod.Head)
            {
                heads++;
                Assert.Equal("/test-bucket/" + key, Uri.UnescapeDataString(request.RequestUri.AbsolutePath));
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentLength = 0; response.Headers.TryAddWithoutValidation("x-amz-meta-note", "<script>synthetic data</script>");
                return Task.FromResult(response);
            }
            gets++;
            if (!request.RequestUri.Query.Contains("list-type=2")) return Task.FromResult(BrowserFixture.Xml("<ListAllMyBucketsResult><Buckets><Bucket><Name>test-bucket</Name></Bucket></Buckets></ListAllMyBucketsResult>"));
            if (request.RequestUri.Query.Contains("prefix=%2F", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(BrowserFixture.Xml(BrowserFixture.Objects(BrowserFixture.Item(key))));
            return Task.FromResult(BrowserFixture.Xml(BrowserFixture.Objects("<CommonPrefixes><Prefix>%2F</Prefix></CommonPrefixes>" + BrowserFixture.Item("/", "<Size>0</Size>"))));
        });
        await f.ConnectAsync();
        var vm = f.Browser;
        Assert.Equal(BrowserRowKind.Bucket, Assert.Single(vm.Rows).Kind);
        vm.SelectedRow = vm.Rows[0]; await vm.OpenCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Rows.Count); Assert.Equal(0, heads);
        Assert.Contains(vm.Rows, r => r.Kind == BrowserRowKind.Object && r.Key == "/" && r.Type == "Marker object");
        vm.SelectedRow = vm.Rows.Single(r => r.Kind == BrowserRowKind.Prefix); await vm.OpenCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows); Assert.Equal(key, row.Key); Assert.Null(row.Size); Assert.Equal("Unknown", row.ModifiedText);
        vm.SelectedRow = row; Assert.Equal(0, heads);
        Assert.Contains("\\u0301", vm.SelectionIdentity); Assert.Contains("\\u000A", row.Name);
        await vm.PropertiesCommand.ExecuteAsync(null);
        Assert.Equal(1, heads); Assert.Contains("<script>synthetic data</script>", vm.Metadata);
        Assert.Contains("Size: 0", vm.Metadata); Assert.Equal(3, gets);
        await vm.UpCommand.ExecuteAsync(null); Assert.Equal("", vm.PrefixInput);
    }
    [AvaloniaFact]
    public async Task Discovery_denied_can_be_recovered_by_direct_bucket_without_reconnect_or_head()
    {
        using var f = new BrowserFixture(bucket: null); var discovery = 0; var objects = 0;
        f.UseSdk((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.Query.Contains("list-type=2")) { objects++; return Task.FromResult(BrowserFixture.Xml(BrowserFixture.Objects())); }
            discovery++; return Task.FromResult(BrowserFixture.Xml("<Error><Code>AccessDenied</Code><Message>synthetic-secret</Message></Error>", HttpStatusCode.Forbidden));
        });
        await f.ConnectAsync();
        Assert.Equal(BrowserState.Denied, f.Browser.State); Assert.True(f.Browser.IsConnected);
        Assert.DoesNotContain("synthetic-secret", f.Browser.Status);
        f.Browser.BucketInput = "test-bucket"; await f.Browser.GoCommand.ExecuteAsync(null);
        Assert.Equal(BrowserState.Empty, f.Browser.State); Assert.Equal(1, discovery); Assert.Equal(1, objects); Assert.Equal(1, f.Factory.Created);
    }
    [AvaloniaTheory]
    [InlineData("<broken>")]
    [InlineData("<WrongRoot/>")]
    [InlineData("<ListBucketResult><Name>test-bucket</Name></ListBucketResult>")]
    public async Task Malformed_actual_sdk_response_is_a_safe_visible_error(string body)
    {
        using var f = new BrowserFixture(); f.UseSdk((_, _) => Task.FromResult(BrowserFixture.Xml(body)));
        await f.ConnectAsync();
        Assert.Equal(BrowserState.Error, f.Browser.State); Assert.Contains("Invalid storage response", f.Browser.Status); Assert.Empty(f.Browser.Rows);
        Assert.False(f.Browser.NextCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task Repeated_sdk_token_stops_paging_and_keeps_last_valid_page()
    {
        using var f = new BrowserFixture(); var sends = 0;
        f.UseSdk((_, _) => { sends++; return Task.FromResult(BrowserFixture.Xml(BrowserFixture.Objects(BrowserFixture.Item("exact%+"), true, "opaque%2F+"))); });
        await f.ConnectAsync(); await f.Browser.NextCommand.ExecuteAsync(null);
        Assert.Equal(2, sends); Assert.Equal(BrowserState.Error, f.Browser.State);
        Assert.Equal("exact%+", Assert.Single(f.Browser.Rows).Key); Assert.Equal(1, f.Browser.RetainedPageCount);
        Assert.False(f.Browser.NextCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task Actual_sdk_retry_exhaustion_is_bounded_and_does_not_leak_response_messages()
    {
        using var f = new BrowserFixture(); var sends = 0;
        f.UseSdk((_, _) => { sends++; return Task.FromResult(BrowserFixture.Xml("<Error><Code>SlowDown</Code><Message>synthetic-secret</Message></Error>", HttpStatusCode.ServiceUnavailable)); });
        await f.ConnectAsync(); Assert.Equal(3, sends); Assert.Equal(BrowserState.Error, f.Browser.State);
        Assert.DoesNotContain("synthetic-secret", f.Browser.Status); Assert.Empty(f.Browser.Rows);
    }
    [AvaloniaFact]
    public async Task Actual_sdk_slow_request_can_be_cancelled_without_blocking_dispatcher()
    {
        using var f = new BrowserFixture(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.UseSdk(async (_, ct) => { started.SetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { cancelled.SetResult(); throw; } return BrowserFixture.Xml(""); });
        await f.SelectAsync(); var connect = f.Browser.ConnectCommand.ExecuteAsync(null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Browser.IsBusy); f.Browser.CancelCommand.Execute(null);
        await connect.WaitAsync(TimeSpan.FromSeconds(5)); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BrowserState.Cancelled, f.Browser.State); Assert.False(f.Browser.IsBusy); Assert.Empty(f.Browser.Rows);
    }
    [AvaloniaFact]
    public async Task Actual_sdk_deadline_and_on_demand_metadata_denial_are_visible()
    {
        using var f = new BrowserFixture();
        f.UseSdk(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return BrowserFixture.Xml(""); }, TimeSpan.FromMilliseconds(80));
        await f.ConnectAsync(); Assert.Contains("timed out", f.Browser.Status);
        f.Browser.Disconnect();
        f.UseSdk((request, _) => Task.FromResult(request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : BrowserFixture.Xml(BrowserFixture.Objects(BrowserFixture.Item("object")))));
        await f.Browser.ConnectCommand.ExecuteAsync(null); f.Browser.SelectedRow = Assert.Single(f.Browser.Rows);
        await f.Browser.PropertiesCommand.ExecuteAsync(null);
        Assert.Contains("Access denied", f.Browser.Metadata); Assert.Equal(BrowserState.Ready, f.Browser.State);
    }
}
