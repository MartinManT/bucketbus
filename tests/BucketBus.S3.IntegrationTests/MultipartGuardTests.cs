using System.Text.Json;
using BucketBus.Core;

namespace BucketBus.S3.IntegrationTests;

public sealed class MultipartGuardTests
{
    [Theory]
    [InlineData(null)] [InlineData("0")] [InlineData("true")]
    public void Disabled_multipart_gate_has_zero_external_io(string? optIn) =>
        Assert.Null(MultipartGate.Load(optIn, "/invalid", "/invalid", _ => throw new Exception("Unexpected I/O"), null, null));
    [Theory]
    [InlineData(null, "bucket/multipart/")] [InlineData("https://storage.example.test", null)]
    public void Multipart_requires_fresh_endpoint_and_scope_before_configuration(string? endpoint, string? scope)
    {
        var reads = 0; Assert.ThrowsAny<Exception>(() => MultipartGate.Load("1", "/unused", "/unused", _ => { reads++; return ""; }, endpoint, scope)); Assert.Equal(0, reads);
    }
    [Theory]
    [InlineData("https://other.example.test", "bucket", "multipart/")]
    [InlineData("https://storage.example.test", "other", "multipart/")]
    [InlineData("https://storage.example.test", "bucket", "other/")]
    public void Configuration_cannot_override_multipart_approval(string endpoint, string bucket, string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-multipart-guard-" + Guid.NewGuid());
        var config = new WriteConfig(new(1, endpoint, endpoint, "us-east-1", "Path", bucket, "synthetic", false, 1, [], Path.Combine(root, "evidence.json")), prefix, Path.Combine(root, "manifest.json"), Path.Combine(root, "qualification.json"));
        Assert.ThrowsAny<Exception>(() => MultipartGate.Load("1", Path.Combine(root, "config.json"), root, _ => JsonSerializer.Serialize(config), "https://storage.example.test", "bucket/multipart/"));
    }
    [Theory]
    [InlineData("other", "owned-id")] [InlineData("owned", "other-id")]
    public async Task Manifest_guard_rejects_unowned_key_or_upload_id(string key, string id)
    {
        using var client = new HttpClient(new MultipartLabGuard(new("Test", "https://storage.example.test", "us-east-1", AddressingStyle.Path), "bucket", ["owned"], new() { ["owned-id"] = "owned" }, new NeverSend()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteAsync("https://storage.example.test/bucket/" + key + "?uploadId=" + id, TestContext.Current.CancellationToken));
    }
    private sealed class NeverSend : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new Exception("Unexpected network"); }
}
