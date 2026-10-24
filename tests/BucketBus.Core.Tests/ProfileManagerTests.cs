namespace BucketBus.Core.Tests;

public sealed class ProfileManagerTests
{
    [Theory]
    [InlineData(CredentialStatus.Locked)]
    [InlineData(CredentialStatus.Unavailable)]
    [InlineData(CredentialStatus.Denied)]
    [InlineData(CredentialStatus.Cancelled)]
    [InlineData(CredentialStatus.StorageFailure)]
    public async Task Failed_vault_writes_do_not_save_profiles_or_fall_back(CredentialStatus status)
    {
        var profiles = new FakeProfiles();
        var vault = new FakeVault { WriteStatus = status };
        using var session = new SessionCredentialStore();
        var manager = new ProfileManager(profiles, vault, session);
        var result = await manager.SaveAsync(Guid.NewGuid(), 0, ConnectionProfileTests.Settings(), true,
            CredentialStorage.Secure, new("synthetic-key", "synthetic-secret"), false, TestContext.Current.CancellationToken);
        Assert.Equal(status, result.CredentialStatus);
        Assert.Empty(profiles.Snapshot.Profiles);
        Assert.Empty(vault.Values);
    }
    [Fact]
    public async Task Endpoint_change_detaches_credentials_and_stale_edit_cannot_overwrite()
    {
        var profiles = new FakeProfiles(); var vault = new FakeVault();
        using var session = new SessionCredentialStore();
        var manager = new ProfileManager(profiles, vault, session);
        var id = Guid.NewGuid();
        await manager.SaveAsync(id, 0, ConnectionProfileTests.Settings(), true, CredentialStorage.Secure, new("key", "secret"), false, TestContext.Current.CancellationToken);
        var first = Assert.Single(profiles.Snapshot.Profiles);
        var result = await manager.SaveAsync(id, 1, ConnectionProfileTests.Settings("https://new.example.com"), false,
            CredentialStorage.Secure, null, false, TestContext.Current.CancellationToken);
        Assert.Equal(ProfileStatus.Success, result.Status);
        Assert.Null(Assert.Single(profiles.Snapshot.Profiles).Credential);
        Assert.Empty(vault.Values);
        Assert.Equal(ProfileStatus.Conflict, (await manager.SaveAsync(id, 1, first.Settings, false, CredentialStorage.Secure, null, false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(CredentialStatus.Denied, (await manager.ReadCredentialAsync(id, 1, false, TestContext.Current.CancellationToken)).Status);
    }
    [Theory]
    [InlineData(CredentialStatus.Locked)]
    [InlineData(CredentialStatus.StorageFailure)]
    [InlineData(CredentialStatus.Cancelled)]
    public async Task Failed_metadata_commit_preserves_old_credential_and_exposes_failed_cleanup(CredentialStatus cleanupStatus)
    {
        var profiles = new FakeProfiles(); var vault = new FakeVault();
        using var session = new SessionCredentialStore();
        var manager = new ProfileManager(profiles, vault, session);
        var id = Guid.NewGuid();
        await manager.SaveAsync(id, 0, ConnectionProfileTests.Settings(), true, CredentialStorage.Secure, new("key", "old-secret"), false, TestContext.Current.CancellationToken);
        var old = profiles.Snapshot;
        profiles.WriteStatus = ProfileStatus.Failure;
        vault.DeleteStatus = cleanupStatus;
        var result = await manager.SaveAsync(id, 1, ConnectionProfileTests.Settings(), true, CredentialStorage.Secure, new("key", "new-secret"), false, TestContext.Current.CancellationToken);
        Assert.Equal(ProfileStatus.Failure, result.Status);
        Assert.True(result.CleanupRequired);
        Assert.Same(old, profiles.Snapshot);
        Assert.Equal("old-secret", (await manager.ReadCredentialAsync(id, 1, false, TestContext.Current.CancellationToken)).Value!.SecretKey);
        vault.DeleteStatus = CredentialStatus.Success;
        Assert.True(await manager.RetryCleanupAsync(false, TestContext.Current.CancellationToken));
        Assert.Single(vault.Values);
    }
    [Fact]
    public async Task Delete_commits_metadata_before_cleanup_and_never_touches_other_profile()
    {
        var profiles = new FakeProfiles(); var vault = new FakeVault();
        using var session = new SessionCredentialStore();
        var manager = new ProfileManager(profiles, vault, session);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        foreach (var id in new[] { a, b }) await manager.SaveAsync(id, 0, ConnectionProfileTests.Settings(), true,
            CredentialStorage.Secure, new("key", "secret"), false, TestContext.Current.CancellationToken);
        profiles.WriteStatus = ProfileStatus.Failure;
        Assert.Equal(ProfileStatus.Failure, (await manager.DeleteAsync(a, 1, false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(2, vault.Values.Count);
        profiles.WriteStatus = ProfileStatus.Success;
        Assert.Equal(ProfileStatus.Success, (await manager.DeleteAsync(a, 1, false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(b, Assert.Single(profiles.Snapshot.Profiles).Id);
        Assert.Equal(CredentialStatus.Found, (await manager.ReadCredentialAsync(b, 1, false, TestContext.Current.CancellationToken)).Status);
        Assert.Single(vault.Values);
    }
    [Fact]
    public async Task Temporary_credentials_require_explicit_memory_storage()
    {
        var profiles = new FakeProfiles(); var vault = new FakeVault();
        using var session = new SessionCredentialStore();
        var manager = new ProfileManager(profiles, vault, session);
        var id = Guid.NewGuid(); var credentials = new Credentials("key", "secret", "token");
        Assert.Equal(ProfileStatus.Invalid, (await manager.SaveAsync(id, 0, ConnectionProfileTests.Settings(), true,
            CredentialStorage.Secure, credentials, false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ProfileStatus.Success, (await manager.SaveAsync(id, 0, ConnectionProfileTests.Settings(), true,
            CredentialStorage.Session, credentials, false, TestContext.Current.CancellationToken)).Status);
        Assert.Empty(vault.Values);
        Assert.Equal(CredentialStatus.Found, (await manager.ReadCredentialAsync(id, 1, false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(CredentialStatus.Cancelled, (await manager.ReadCredentialAsync(id, 1, false, new(true))).Status);
    }
    private sealed class FakeProfiles : IProfileStore
    {
        public ProfileSnapshot Snapshot = new(0, []);
        public ProfileStatus WriteStatus = ProfileStatus.Success;
        public Task<ProfileReadResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(cancellationToken.IsCancellationRequested
            ? new ProfileReadResult(ProfileStatus.Cancelled) : new(ProfileStatus.Success, Snapshot));
        public Task<ProfileStatus> WriteAsync(ProfileSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        { if (WriteStatus == ProfileStatus.Success) Snapshot = snapshot; return Task.FromResult(WriteStatus); }
    }
    private sealed class FakeVault : ICredentialStore
    {
        public Dictionary<string, Credentials> Values = [];
        public CredentialStatus WriteStatus = CredentialStatus.Success, DeleteStatus = CredentialStatus.Success;
        public Task<CredentialResult> ReadAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(reference.VaultKey, out var value) ? new CredentialResult(CredentialStatus.Found, value) : new(CredentialStatus.Missing));
        public Task<CredentialStatus> WriteAsync(CredentialReference reference, Credentials credentials, bool allowUnlock, CancellationToken cancellationToken = default)
        { if (WriteStatus == CredentialStatus.Success) Values[reference.VaultKey] = credentials; return Task.FromResult(WriteStatus); }
        public Task<CredentialStatus> DeleteAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default)
        { if (DeleteStatus == CredentialStatus.Success) Values.Remove(reference.VaultKey); return Task.FromResult(DeleteStatus); }
    }
}
