using System.Text.Json;
using BucketBus.Core;

namespace BucketBus.Platform.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bucketbus-test-" + Guid.NewGuid().ToString("N"));
    private JsonProfileStore Store() => new(_directory);
    private string Pathname => Path.Combine(_directory, "profiles.json");
    private static ConnectionProfile Profile() => new(Guid.NewGuid(), 1,
        new("Unicode é", "https://storage.example.com", "us-east-1", AddressingStyle.Path, "bucket", "e\u0301/é//%2F+\t"), null);
    [Fact]
    public async Task Round_trip_preserves_identity_revision_exact_prefix_and_opaque_binding_only()
    {
        var profile = Profile();
        profile = profile with { Credential = new(profile.Id, Guid.NewGuid(), CredentialStorage.Secure, profile.Settings.TargetFingerprint()) };
        Assert.Equal(ProfileStatus.Success, await Store().WriteAsync(new(1, [profile]), 0, TestContext.Current.CancellationToken));
        var loaded = await Store().ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(profile, Assert.Single(loaded.Snapshot!.Profiles));
        var json = await File.ReadAllTextAsync(Pathname, TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.DoesNotContain("AccessKey", json);
        Assert.DoesNotContain("SecretKey", json);
        Assert.DoesNotContain("SessionToken", json);
        Assert.DoesNotContain("VaultKey", json);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Pathname));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_directory));
        }
    }
    [Fact]
    public async Task Interrupted_or_cancelled_save_preserves_last_commit_and_removes_own_temporary_file()
    {
        var store = Store(); var profile = Profile();
        await store.WriteAsync(new(1, [profile]), 0, TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(Pathname, TestContext.Current.CancellationToken);
        store.BeforeCommit = () => throw new IOException("synthetic disk failure");
        Assert.Equal(ProfileStatus.Failure, await store.WriteAsync(new(2, []), 1, TestContext.Current.CancellationToken));
        Assert.Equal(before, await File.ReadAllBytesAsync(Pathname, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        using var cancellation = new CancellationTokenSource();
        store.BeforeCommit = cancellation.Cancel;
        Assert.Equal(ProfileStatus.Cancelled, await store.WriteAsync(new(2, []), 1, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(Pathname, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Optimistic_revision_and_exclusive_lock_prevent_lost_updates()
    {
        var store = Store(); await store.WriteAsync(new(1, [Profile()]), 0, TestContext.Current.CancellationToken);
        Assert.Equal(ProfileStatus.Conflict, await Store().WriteAsync(new(1, []), 0, TestContext.Current.CancellationToken));
        using (var locked = new FileStream(Path.Combine(_directory, "profiles.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(ProfileStatus.Failure, await Store().WriteAsync(new(2, []), 1, TestContext.Current.CancellationToken));
        Assert.Single((await store.ReadAsync(TestContext.Current.CancellationToken)).Snapshot!.Profiles);
    }
    [Theory]
    [InlineData("{broken", ProfileStatus.Invalid)]
    [InlineData("null", ProfileStatus.Invalid)]
    [InlineData("[]", ProfileStatus.Invalid)]
    [InlineData("{\"SchemaVersion\":\"bad\"}", ProfileStatus.Invalid)]
    [InlineData("{\"SchemaVersion\":2,\"FutureField\":true}", ProfileStatus.UnsupportedSchema)]
    [InlineData("{\"SchemaVersion\":1,\"Revision\":1,\"Profiles\":null}", ProfileStatus.Invalid)]
    [InlineData("{\"SchemaVersion\":1,\"Revision\":1,\"Profiles\":[null]}", ProfileStatus.Invalid)]
    [InlineData("{\"SchemaVersion\":1,\"Revision\":1,\"Profiles\":[],\"SecretKey\":\"synthetic\"}", ProfileStatus.Invalid)]
    [InlineData("{\"SchemaVersion\":1,\"Revision\":1,\"Revision\":2,\"Profiles\":[]}", ProfileStatus.Invalid)]
    public async Task Invalid_or_future_documents_are_never_replaced(string json, ProfileStatus status)
    {
        Directory.CreateDirectory(_directory); await File.WriteAllTextAsync(Pathname, json, TestContext.Current.CancellationToken);
        Assert.Equal(status, (await Store().ReadAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(status, await Store().WriteAsync(new(1, []), 0, TestContext.Current.CancellationToken));
        Assert.Equal(json, await File.ReadAllTextAsync(Pathname, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Missing_addressing_style_and_oversized_documents_fail_closed()
    {
        var store = Store(); await store.WriteAsync(new(1, [Profile()]), 0, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(Pathname, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Pathname, json.Replace("\"Addressing\": \"Path\",", "", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.Equal(ProfileStatus.Invalid, (await store.ReadAsync(TestContext.Current.CancellationToken)).Status);
        await File.WriteAllTextAsync(Pathname, new string(' ', 1_048_577), TestContext.Current.CancellationToken);
        Assert.Equal(ProfileStatus.Invalid, (await store.ReadAsync(TestContext.Current.CancellationToken)).Status);
    }
    [Fact]
    public async Task Missing_file_and_leftover_uncommitted_file_do_not_restore_incomplete_updates()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, ".profiles-interrupted.tmp"), "{partial", TestContext.Current.CancellationToken);
        var read = await Store().ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProfileStatus.Success, read.Status);
        Assert.Empty(read.Snapshot!.Profiles);
    }
    [Fact]
    public async Task Profile_links_and_cross_profile_bindings_are_rejected()
    {
        var profile = Profile();
        var invalid = profile with { Credential = new(Guid.NewGuid(), Guid.NewGuid(), CredentialStorage.Secure, profile.Settings.TargetFingerprint()) };
        Assert.Equal(ProfileStatus.Invalid, await Store().WriteAsync(new(1, [invalid]), 0, TestContext.Current.CancellationToken));
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
            var other = Path.Combine(_directory, "other.json"); await File.WriteAllTextAsync(other, "untouched", TestContext.Current.CancellationToken);
            File.CreateSymbolicLink(Pathname, other);
            Assert.Equal(ProfileStatus.Unavailable, (await Store().ReadAsync(TestContext.Current.CancellationToken)).Status);
            Assert.Equal("untouched", await File.ReadAllTextAsync(other, TestContext.Current.CancellationToken));
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
