using BucketBus.Core;

namespace BucketBus.Platform.Tests;

public sealed class NativeCredentialTests
{
    [Fact]
    [Trait("Category", "NativeVault")]
    public async Task Opted_in_disposable_entry_round_trip_and_cleanup()
    {
        // Guard before constructing an adapter or reading a vault. No existing entry is enumerated.
        if (Environment.GetEnvironmentVariable("BUCKETBUS_RUN_NATIVE_VAULT_TESTS") != "1")
            Assert.Skip("Native vault validation BLOCKED: explicit opt-in required.");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) Assert.Skip("Unsupported OS.");
        if (OperatingSystem.IsLinux() && (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))))
            Assert.Skip("Native Linux validation BLOCKED: an interactive desktop session is required.");
        ICredentialStore store = OperatingSystem.IsWindows() ? new WindowsCredentialStore() : new LinuxSecretServiceStore();
        var settings = new ProfileSettings("Disposable test", "https://test.example.com", "us-east-1", AddressingStyle.Path);
        var reference = new CredentialReference(Guid.NewGuid(), Guid.NewGuid(), CredentialStorage.Secure, settings.TargetFingerprint());
        var credentials = new Credentials("bucketbus-disposable-test", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(CredentialStatus.Missing, (await store.ReadAsync(reference, true, TestContext.Current.CancellationToken)).Status);
            Assert.Equal(CredentialStatus.Success, await store.WriteAsync(reference, credentials, true, TestContext.Current.CancellationToken));
            var result = await store.ReadAsync(reference, true, TestContext.Current.CancellationToken);
            Assert.Equal(CredentialStatus.Found, result.Status);
            // Never include even generated secret values in assertion diagnostics.
            Assert.True(result.Value?.SecretKey == credentials.SecretKey, "Disposable credential did not round-trip.");
        }
        finally
        {
            var cleanup = await store.DeleteAsync(reference, true, CancellationToken.None);
            Assert.True(cleanup is CredentialStatus.Success or CredentialStatus.Missing, "Disposable vault entry cleanup failed; inspect BucketBus entries in the OS vault.");
        }
    }
}
