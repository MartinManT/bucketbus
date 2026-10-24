using BucketBus.Core;

namespace BucketBus.S3.IntegrationTests;

public sealed class GuardTests
{
    [Theory]
    [InlineData(null)] [InlineData("0")] [InlineData("true")]
    public void Disabled_run_does_not_even_read_configuration(string? optIn) =>
        Assert.Null(LiveGate.Load(optIn, "/invalid", "/invalid", _ => throw new Exception("External I/O attempted"), null));
    [Fact]
    public void Enabled_without_configuration_fails_before_any_io() =>
        Assert.Throws<InvalidOperationException>(() => LiveGate.Load("1", null, "/invalid", _ => throw new Exception("I/O attempted"), "https://storage.example.test"));
    [Theory]
    [InlineData("http://storage.example.test", "Path")]
    [InlineData("https://user@storage.example.test", "Path")]
    [InlineData("https://storage.example.test", "Unknown")]
    public void Unsafe_targets_are_rejected(string endpoint, string addressing) =>
        Assert.ThrowsAny<Exception>(() => new LiveConfig(1, endpoint, endpoint, "us-east-1", addressing, "bucket", "synthetic", false, 2, [], "/unused").Validate());
    [Fact]
    public void Named_credentials_cannot_fall_back_to_another_section()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "[default]\naws_access_key_id=synthetic\naws_secret_access_key=synthetic\n");
            Assert.Throws<InvalidOperationException>(() => LiveGate.ReadNamedCredentials("selected", file));
            File.WriteAllText(file, "[selected]\naws_access_key_id=synthetic\naws_secret_access_key=synthetic\ncredential_process=never-run\n");
            Assert.Throws<InvalidOperationException>(() => LiveGate.ReadNamedCredentials("selected", file));
        }
        finally { File.Delete(file); }
    }
    [Theory]
    [InlineData("POST")] [InlineData("PUT")] [InlineData("DELETE")]
    public async Task Final_test_guard_blocks_mutations_before_inner_transport(string method)
    {
        using var client = new HttpClient(new CountingReadHandler(new NeverSend()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new(new HttpMethod(method), "https://storage.example.test"), TestContext.Current.CancellationToken));
    }
    [Fact]
    public void Approved_authority_mismatch_and_excessive_bounds_are_rejected()
    {
        var config = new LiveConfig(1, "https://storage.example.test", "https://other.example.test", "us-east-1", "Path", "bucket", "synthetic", false, 2, [], "/unused");
        Assert.Throws<InvalidOperationException>(() => config.Validate());
        config = config with { ApprovedEndpoint = config.Endpoint, MaximumPages = 9 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }
    [Fact]
    public void Private_path_escape_is_rejected_before_configuration_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-internal");
        Assert.Throws<InvalidOperationException>(() => LiveGate.Load("1", Path.GetFullPath(Path.Combine(root, "../public.json")), root, _ => throw new Exception("I/O attempted"), "https://storage.example.test"));
    }
    [Fact]
    public void Explicit_nondefault_literal_credentials_round_trip_without_default_fallback()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "[selected]\naws_access_key_id=synthetic\naws_secret_access_key=synthetic-secret\naws_session_token=synthetic-token\n");
            var result = LiveGate.ReadNamedCredentials("selected", file);
            Assert.True(result.AccessKey == "synthetic" && result.SecretKey == "synthetic-secret" && result.SessionToken == "synthetic-token", "Explicit source mismatch");
            Assert.DoesNotContain("synthetic", result.ToString());
        }
        finally { File.Delete(file); }
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("http://storage.example.test")]
    [InlineData("https://user@storage.example.test")]
    public void Missing_or_unsafe_independent_approval_fails_before_configuration_io(string? approval) =>
        CheckRejectedApproval(approval);
    private static void CheckRejectedApproval(string? approval)
    {
        var reads = 0;
        Assert.ThrowsAny<Exception>(() => LiveGate.Load("1", "/unused", "/unused", _ => { reads++; return ""; }, approval));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("https://other.example.test")]
    [InlineData("https://storage.example.test:9443")]
    public void Changing_both_json_endpoints_cannot_override_operator_approval(string changed)
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-approval-" + Guid.NewGuid());
        var config = new LiveConfig(1, changed, changed, "us-east-1", "Path", "bucket", "synthetic", false, 2, [], Path.Combine(root, "evidence.json"));
        Assert.Throws<InvalidOperationException>(() => LiveGate.Load("1", Path.Combine(root, "config.json"), root,
            _ => System.Text.Json.JsonSerializer.Serialize(config), "https://storage.example.test"));
        // Load must succeed before the live entry point is allowed to resolve credentials or build a client.
    }
    [Fact]
    public void Independently_approved_authority_is_canonicalized()
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-approval-" + Guid.NewGuid());
        var config = new LiveConfig(1, "https://storage.example.test", "https://storage.example.test", "us-east-1", "Path", "bucket", "synthetic", false, 2, [], Path.Combine(root, "evidence.json"));
        Assert.NotNull(LiveGate.Load("1", Path.Combine(root, "config.json"), root,
            _ => System.Text.Json.JsonSerializer.Serialize(config), "https://STORAGE.example.test:443/"));
    }
    private sealed class NeverSend : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new Exception("Network attempted"); }
}

public sealed class WriteGuardTests
{
    [Theory]
    [InlineData(null)] [InlineData("0")] [InlineData("true")]
    public void Disabled_write_gate_has_zero_external_io(string? optIn) =>
        Assert.Null(WriteGate.Load(optIn, "/invalid", "/invalid", _ => throw new Exception("I/O"), null, null));
    [Theory]
    [InlineData(null, "bucket/phase/")]
    [InlineData("https://storage.example.test", null)]
    [InlineData("http://storage.example.test", "bucket/phase/")]
    public void Missing_independent_write_approval_stops_before_configuration(string? endpoint, string? scope)
    {
        var reads = 0;
        Assert.ThrowsAny<Exception>(() => WriteGate.Load("1", "/invalid", "/invalid", _ => { reads++; return ""; }, endpoint, scope));
        Assert.Equal(0, reads);
    }
    [Theory]
    [InlineData("https://other.example.test", "bucket", "phase/")]
    [InlineData("https://storage.example.test", "other", "phase/")]
    [InlineData("https://storage.example.test", "bucket", "elsewhere/")]
    public void Changing_configuration_cannot_bypass_independent_target_and_scope(string endpoint, string bucket, string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), "bucketbus-private-" + Guid.NewGuid());
        var config = new WriteConfig(new(1, endpoint, endpoint, "us-east-1", "Path", bucket, "synthetic", false, 1, [], Path.Combine(root, "evidence.json")),
            prefix, Path.Combine(root, "manifest.json"), Path.Combine(root, "qualification.json"));
        Assert.ThrowsAny<Exception>(() => WriteGate.Load("1", Path.Combine(root, "config.json"), root, _ => System.Text.Json.JsonSerializer.Serialize(config),
            "https://storage.example.test", "bucket/phase/"));
    }
}
