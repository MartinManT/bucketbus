namespace BucketBus.Core.Tests;

public sealed class ConnectionProfileTests
{
    [Theory]
    [InlineData("http://storage.example.com")]
    [InlineData("https://user:password@storage.example.com")]
    [InlineData("https://storage.example.com?")]
    [InlineData("https://storage.example.com#")]
    [InlineData("https://storage.example.com/a/..")]
    [InlineData("https://storage.example.com\\other")]
    [InlineData(" https://storage.example.com")]
    [InlineData("https://storage.example.com/%2f")]
    [InlineData("https://storage.example.com:0")]
    [InlineData("file:///tmp/example")]
    [InlineData("https://storage.example.com//")]
    public void Unsafe_endpoints_are_rejected_before_normalization(string endpoint) =>
        Assert.Throws<ArgumentException>(() => Settings(endpoint).Validate());

    [Theory]
    [InlineData("https://STORAGE.example.com:443/", "https://storage.example.com")]
    [InlineData("https://[::1]:9443", "https://[::1]:9443")]
    public void Safe_endpoints_are_canonical_but_prefixes_are_exact(string endpoint, string expected)
    {
        const string prefix = "e\u0301/é//%2F+\t";
        var settings = (Settings(endpoint) with { Prefix = prefix }).Validate();
        Assert.Equal(expected, settings.Endpoint);
        Assert.Equal(prefix, settings.Prefix);
    }
    [Theory]
    [InlineData("name\u202Etxt")] [InlineData("name\u2066txt\u2069")]
    [InlineData("name\u200F")] [InlineData("name\u061C")]
    public void Directional_format_controls_in_profile_names_are_rejected(string name) =>
        Assert.Throws<ArgumentException>(() => (Settings() with { Name = name }).Validate());

    [Fact]
    public void Invalid_utf16_and_overlong_utf8_prefixes_are_rejected_without_replacement()
    {
        Assert.ThrowsAny<ArgumentException>(() => (Settings() with { Prefix = "\ud800" }).Validate());
        Assert.ThrowsAny<ArgumentException>(() => (Settings() with { Prefix = new string('é', 513) }).Validate());
        Assert.Equal(new string('é', 512), (Settings() with { Prefix = new string('é', 512) }).Validate().Prefix);
    }
    [Fact]
    public void References_cannot_cross_profile_or_target_boundaries()
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), 1, Settings(), null);
        var reference = new CredentialReference(profile.Id, Guid.NewGuid(), CredentialStorage.Secure, profile.Settings.TargetFingerprint());
        Assert.Equal(reference, (profile with { Credential = reference }).Validate().Credential);
        Assert.Throws<ArgumentException>(() => (profile with { Id = Guid.NewGuid(), Credential = reference }).Validate());
        Assert.Throws<ArgumentException>(() => (profile with { Settings = Settings("https://other.example.com"), Credential = reference }).Validate());
    }
    [Fact]
    public async Task Session_credentials_are_isolated_cancelable_and_discarded_on_dispose()
    {
        using var store = new SessionCredentialStore();
        var reference = new CredentialReference(Guid.NewGuid(), Guid.NewGuid(), CredentialStorage.Session, Settings().TargetFingerprint());
        var credentials = new Credentials("synthetic-key", "synthetic-secret", "synthetic-token");
        Assert.Equal(CredentialStatus.Success, await store.WriteAsync(reference, credentials, false, TestContext.Current.CancellationToken));
        Assert.Same(credentials, (await store.ReadAsync(reference, false, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(CredentialStatus.Missing, (await store.ReadAsync(reference with { ProfileId = Guid.NewGuid() }, false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(CredentialStatus.Cancelled, (await store.ReadAsync(reference, false, new(true))).Status);
        Assert.DoesNotContain(credentials.SecretKey, credentials.ToString());
        Assert.DoesNotContain(credentials.SecretKey, new CredentialResult(CredentialStatus.Found, credentials).ToString());
        store.Dispose();
        Assert.Equal(CredentialStatus.Missing, (await store.ReadAsync(reference, false, TestContext.Current.CancellationToken)).Status);
    }
    internal static ProfileSettings Settings(string endpoint = "https://storage.example.com") => new("Example", endpoint, "us-east-1", AddressingStyle.Path);
}
