using System.Net;
using BucketBus.Core;

namespace BucketBus.S3.Tests;

public sealed class SessionLifecycleTests
{
    [Theory]
    [InlineData("endpoint")]
    [InlineData("region")]
    [InlineData("addressing")]
    public async Task Profile_authority_change_invalidates_running_session_and_requires_rebinding(string change)
    {
        var profiles = new MemoryProfiles(); using var credentials = new SessionCredentialStore();
        var manager = new ProfileManager(profiles, credentials, credentials);
        var settings = new ProfileSettings("Example", "https://storage.example.test", "test-region-1", AddressingStyle.Path);
        var id = Guid.NewGuid();
        await manager.SaveAsync(id, 0, settings, true, CredentialStorage.Session, new("KEY", "synthetic-secret"), false, TestContext.Current.CancellationToken);
        var factory = new FixtureFactory();
        await using var controller = new ReadOnlySessionController(manager, factory);
        var open = await controller.OpenAsync(id, 1, false, TestContext.Current.CancellationToken);
        Assert.Equal(StorageError.None, open.Error); Assert.NotNull(open.Session);
        Assert.Equal(0, Assert.Single(factory.Transports).Attempts); // Explicit direct-bucket open does no discovery or HEAD.
        var listing = open.Session.ListObjects("test-bucket");
        Assert.True((await listing.NextAsync(TestContext.Current.CancellationToken)).IsSuccess);
        var edited = change switch
        {
            "endpoint" => settings with { Endpoint = "https://other.example.test" },
            "region" => settings with { Region = "other-region-1" },
            _ => settings with { Addressing = AddressingStyle.VirtualHosted }
        };
        Assert.Equal(ProfileStatus.Success, (await manager.SaveAsync(id, 1, edited, false, CredentialStorage.Session, null, false, TestContext.Current.CancellationToken)).Status);
        Assert.True(open.Session.IsInvalidated);
        Assert.Equal(StorageError.SessionExpired, (await open.Session.ListBuckets().NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Null((await controller.OpenAsync(id, 2, false, TestContext.Current.CancellationToken)).Session);
        Assert.Single(factory.Transports);
    }
    [Fact]
    public async Task Replacement_disposes_old_client_and_unrelated_profile_edits_do_not_retarget_active_session()
    {
        var profiles = new MemoryProfiles(); using var credentials = new SessionCredentialStore();
        var manager = new ProfileManager(profiles, credentials, credentials);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var settings = new ProfileSettings("Example", "https://storage.example.test", "test-region-1", AddressingStyle.Path);
        foreach (var id in new[] { a, b })
            await manager.SaveAsync(id, 0, settings, true, CredentialStorage.Session, new("KEY", "synthetic-secret"), false, TestContext.Current.CancellationToken);
        var factory = new FixtureFactory(); await using var controller = new ReadOnlySessionController(manager, factory);
        var first = (await controller.OpenAsync(a, 1, false, TestContext.Current.CancellationToken)).Session!;
        await manager.SaveAsync(b, 1, settings with { Name = "Renamed" }, false, CredentialStorage.Session, null, false, TestContext.Current.CancellationToken);
        Assert.False(first.IsInvalidated);
        var second = (await controller.OpenAsync(b, 2, false, TestContext.Current.CancellationToken)).Session!;
        Assert.True(first.IsInvalidated); Assert.True(factory.Transports[0].Disposed);
        Assert.NotSame(first, second); Assert.Equal(b, second.Profile.Id); Assert.Equal(2, second.Profile.Revision);
        await controller.DisposeAsync(); Assert.True(second.IsInvalidated); Assert.True(factory.Transports[1].Disposed);
    }
    [Fact]
    public async Task External_profile_revision_change_is_detected_before_next_request()
    {
        var profiles = new MemoryProfiles(); using var credentials = new SessionCredentialStore();
        var manager = new ProfileManager(profiles, credentials, credentials);
        var id = Guid.NewGuid(); var settings = new ProfileSettings("Example", "https://storage.example.test", "test-region-1", AddressingStyle.Path);
        await manager.SaveAsync(id, 0, settings, true, CredentialStorage.Session, new("KEY", "synthetic-secret"), false, TestContext.Current.CancellationToken);
        var factory = new FixtureFactory(); await using var controller = new ReadOnlySessionController(manager, factory);
        var session = (await controller.OpenAsync(id, 1, false, TestContext.Current.CancellationToken)).Session!;
        profiles.Snapshot = new(2, [profiles.Snapshot.Profiles[0] with { Revision = 2 }]);
        Assert.Equal(StorageError.SessionExpired, (await session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Equal(0, factory.Transports[0].Attempts);
    }
    [Fact]
    public async Task Cancelled_page_is_not_advanced_when_post_response_authority_check_is_cancelled()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var checks = 0; var requests = 0;
        await using var fixture = new SdkFixture((request, _) =>
        {
            requests++;
            Assert.False(SdkFixture.Query(request.RequestUri!).ContainsKey("continuation-token"));
            return Task.FromResult(SdkFixture.Xml(SdkFixture.Objects(SdkFixture.Item("first"), true, "next-page")));
        }, current: _ => { if (++checks == 2) cancel.Cancel(); return Task.FromResult(true); });
        var listing = fixture.Session.ListObjects("test-bucket");
        Assert.Equal(StorageError.Cancelled, (await listing.NextAsync(cancel.Token)).Error);
        var retry = await listing.NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("first", Assert.Single(retry.Value!.Entries).Key);
        Assert.Equal(2, requests);
    }
    private sealed class MemoryProfiles : IProfileStore
    {
        public ProfileSnapshot Snapshot = new(0, []);
        public Task<ProfileReadResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProfileReadResult(ProfileStatus.Success, Snapshot));
        public Task<ProfileStatus> WriteAsync(ProfileSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        { Snapshot = snapshot; return Task.FromResult(ProfileStatus.Success); }
    }
    private sealed class FixtureFactory : IReadOnlyStorageSessionFactory
    {
        public List<ScriptTransport> Transports { get; } = [];
        public IReadOnlyStorageSession Create(ConnectionProfile profile, Credentials credentials, Func<CancellationToken, Task<bool>> isCurrent)
        {
            var transport = new ScriptTransport((_, _) => Task.FromResult(SdkFixture.Xml(SdkFixture.Objects())));
            Transports.Add(transport);
            return S3ReadOnlySessionFactory.Create(profile, credentials, isCurrent, transport, TimeSpan.FromSeconds(30));
        }
    }
}
