using BucketBus.Core;
using Tmds.DBus;

namespace BucketBus.Platform.Tests;

public sealed class SecretServiceTests
{
    private static CredentialReference Reference() => new(Guid.NewGuid(), Guid.NewGuid(), CredentialStorage.Secure,
        new ProfileSettings("Example", "https://storage.example.com", "us-east-1", AddressingStyle.Path).TargetFingerprint());
    [Theory]
    [InlineData(false, false, CredentialStatus.Missing)]
    [InlineData(true, false, CredentialStatus.Locked)]
    [InlineData(true, true, CredentialStatus.Found)]
    public async Task Locked_items_require_explicit_unlock_and_are_requeried(bool locked, bool allowUnlock, CredentialStatus expected)
    {
        var bus = new FakeBus { Locked = locked, Exists = locked };
        var result = await new LinuxSecretServiceStore(() => bus).ReadAsync(Reference(), allowUnlock, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.Equal(locked && allowUnlock ? 1 : 0, bus.Unlocks);
        Assert.True(bus.Disposed);
        if (expected == CredentialStatus.Found) { Assert.NotNull(result.Value); Assert.Equal(1, bus.SessionsClosed); Assert.All(bus.ReturnedBytes!, b => Assert.Equal(0, b)); }
    }
    [Fact]
    public async Task Prompt_subscription_precedes_prompt_and_dismissal_is_cancelled()
    {
        var bus = new FakeBus { Locked = true, Exists = true, UsePrompt = true, DismissPrompt = true };
        var result = await new LinuxSecretServiceStore(() => bus).ReadAsync(Reference(), true, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialStatus.Cancelled, result.Status);
        Assert.Equal(1, bus.PromptCalls);
        Assert.True(bus.WatchDisposed);
        Assert.True(bus.PromptDismissed);
    }
    [Fact]
    public async Task Cancellation_dismisses_prompt_and_disposes_connection()
    {
        using var cancellation = new CancellationTokenSource();
        var bus = new FakeBus { Locked = true, Exists = true, UsePrompt = true, OnPrompt = cancellation.Cancel };
        var result = await new LinuxSecretServiceStore(() => bus).ReadAsync(Reference(), true, cancellation.Token);
        Assert.Equal(CredentialStatus.Cancelled, result.Status);
        Assert.True(bus.PromptDismissed);
        Assert.True(bus.Disposed);
    }
    [Fact]
    public async Task Write_uses_only_owned_attributes_and_clears_payload_closes_session()
    {
        var bus = new FakeBus(); var reference = Reference();
        Assert.Equal(CredentialStatus.Success, await new LinuxSecretServiceStore(() => bus).WriteAsync(reference, new("key", "secret"), false, TestContext.Current.CancellationToken));
        Assert.Equal(reference.VaultKey, bus.Attributes!["binding"]);
        Assert.Equal("BucketBus", bus.Attributes["application"]);
        Assert.All(bus.WrittenBytes!, b => Assert.Equal(0, b));
        Assert.Equal(1, bus.SessionsClosed);
        Assert.False(bus.Replace);
    }
    [Fact]
    public async Task No_collection_no_bus_or_temporary_token_never_creates_a_keyring()
    {
        var reference = Reference();
        Assert.Equal(CredentialStatus.Unavailable, (await new LinuxSecretServiceStore(() => new FakeBus { CannotConnect = true })
            .ReadAsync(reference, false, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(CredentialStatus.Unavailable, await new LinuxSecretServiceStore(() => null).WriteAsync(reference, new("key", "secret"), false, TestContext.Current.CancellationToken));
        var bus = new FakeBus { MissingCollection = true };
        Assert.Equal(CredentialStatus.Unavailable, await new LinuxSecretServiceStore(() => bus).WriteAsync(reference, new("key", "secret"), true, TestContext.Current.CancellationToken));
        Assert.Null(bus.WrittenBytes);
        Assert.Equal(CredentialStatus.Denied, await new LinuxSecretServiceStore(() => throw new Exception("must not connect"))
            .WriteAsync(reference, new("key", "secret", "token"), true, TestContext.Current.CancellationToken));
        Assert.Equal(CredentialStatus.Denied, await new WindowsCredentialStore().WriteAsync(reference, new("key", "secret", "token"), true, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Deadline_is_a_failure_and_duplicate_matches_are_not_chosen_arbitrarily()
    {
        var stalled = new FakeBus { Stall = true };
        Assert.Equal(CredentialStatus.RetrievalFailure, (await new LinuxSecretServiceStore(() => stalled, TimeSpan.FromMilliseconds(30)).ReadAsync(Reference(), false, TestContext.Current.CancellationToken)).Status);
        Assert.True(stalled.Disposed);
        var duplicate = new FakeBus { Exists = true, Duplicate = true };
        Assert.Equal(CredentialStatus.RetrievalFailure, (await new LinuxSecretServiceStore(() => duplicate).ReadAsync(Reference(), false, TestContext.Current.CancellationToken)).Status);
    }
    [Theory]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied", CredentialStatus.Denied)]
    [InlineData("org.freedesktop.Secret.Error.IsLocked", CredentialStatus.Locked)]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown", CredentialStatus.Unavailable)]
    [InlineData("org.freedesktop.Secret.Error.NoSuchObject", CredentialStatus.Missing)]
    [InlineData("unknown.synthetic", CredentialStatus.RetrievalFailure)]
    public async Task Native_error_names_are_mapped_without_exposing_messages(string name, CredentialStatus expected)
    {
        var bus = new FakeBus { Error = name };
        var result = await new LinuxSecretServiceStore(() => bus).ReadAsync(Reference(), false, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain("synthetic-secret", result.ToString());
    }
    [Theory]
    [InlineData(1168, CredentialStatus.Missing)]
    [InlineData(5, CredentialStatus.Denied)]
    [InlineData(1312, CredentialStatus.Unavailable)]
    [InlineData(1223, CredentialStatus.Cancelled)]
    [InlineData(999, CredentialStatus.RetrievalFailure)]
    public void Windows_error_mapping_is_offline_only(int error, CredentialStatus expected) => Assert.Equal(expected, WindowsCredentialStore.MapError(error, false));

    [Fact]
    public void Real_Tmds_proxy_generation_accepts_all_protocol_signatures_without_connecting()
    {
        using var connection = new Connection("unix:path=/nonexistent-bucketbus-test");
        var path = new ObjectPath("/org/freedesktop/secrets");
        Assert.NotNull(connection.CreateProxy<ISecretService>("org.freedesktop.secrets", path));
        Assert.NotNull(connection.CreateProxy<ISecretCollection>("org.freedesktop.secrets", path));
        Assert.NotNull(connection.CreateProxy<ISecretItem>("org.freedesktop.secrets", path));
        Assert.NotNull(connection.CreateProxy<ISecretSession>("org.freedesktop.secrets", path));
        Assert.NotNull(connection.CreateProxy<ISecretPrompt>("org.freedesktop.secrets", path));
    }
    [Theory]
    [InlineData("valid", CredentialStatus.Success)]
    [InlineData("dismissed", CredentialStatus.Cancelled)]
    [InlineData("wrong-type", CredentialStatus.StorageFailure)]
    [InlineData("false-result", CredentialStatus.StorageFailure)]
    [InlineData("root-path", CredentialStatus.StorageFailure)]
    [InlineData("other-path", CredentialStatus.StorageFailure)]
    [InlineData("absent", CredentialStatus.StorageFailure)]
    [InlineData("locked", CredentialStatus.StorageFailure)]
    [InlineData("verification-error", CredentialStatus.StorageFailure)]
    public async Task Prompted_create_requires_an_object_path_and_confirmed_owned_item(string scenario, CredentialStatus expected)
    {
        var reference = Reference();
        var bus = new FakeBus { MutationPrompt = true, DismissPrompt = scenario == "dismissed",
            MutationEffective = scenario != "absent", LockAfterMutation = scenario == "locked",
            VerificationError = scenario == "verification-error" };
        bus.MutationResult = scenario switch
        {
            "wrong-type" => (object)"/item", "false-result" => false, "root-path" => new ObjectPath("/"),
            "other-path" => new ObjectPath("/unrelated"), _ => new ObjectPath("/item")
        };
        var status = await new LinuxSecretServiceStore(() => bus).WriteAsync(reference, new("key", "secret"), true, TestContext.Current.CancellationToken);
        Assert.Equal(expected, status);
        Assert.True(bus.Disposed); Assert.True(bus.WatchDisposed);
        Assert.All(bus.WrittenBytes!, b => Assert.Equal(0, b));
        Assert.All(bus.Searches, attributes =>
        {
            Assert.Equal(2, attributes.Count); Assert.Equal("BucketBus", attributes["application"]);
            Assert.Equal(reference.VaultKey, attributes["binding"]);
        });
    }
    [Theory]
    [InlineData(false, true, false, CredentialStatus.Success)]
    [InlineData(true, true, false, CredentialStatus.Success)]
    [InlineData(false, false, false, CredentialStatus.StorageFailure)]
    [InlineData(true, false, false, CredentialStatus.StorageFailure)]
    [InlineData(true, false, true, CredentialStatus.StorageFailure)]
    public async Task Delete_confirms_absence_in_both_locked_and_unlocked_results(
        bool prompted, bool removed, bool locked, CredentialStatus expected)
    {
        var bus = new FakeBus { Exists = true, MutationPrompt = prompted, MutationEffective = removed,
            LockAfterMutation = locked, MutationResult = false }; // Delete's result variant is unspecified.
        Assert.Equal(expected, await new LinuxSecretServiceStore(() => bus).DeleteAsync(Reference(), true, TestContext.Current.CancellationToken));
        Assert.Equal(2, bus.Searches.Count); Assert.True(bus.Disposed);
    }
    [Fact]
    public async Task Dismissed_delete_does_not_confirm_cleanup()
    {
        var bus = new FakeBus { Exists = true, MutationPrompt = true, DismissPrompt = true };
        Assert.Equal(CredentialStatus.Cancelled, await new LinuxSecretServiceStore(() => bus).DeleteAsync(Reference(), true, TestContext.Current.CancellationToken));
        Assert.True(bus.Exists); Assert.True(bus.Disposed);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancelled_mutation_prompt_dismisses_and_never_reports_success(bool delete)
    {
        using var cancellation = new CancellationTokenSource();
        var bus = new FakeBus { Exists = delete, MutationPrompt = true, OnPrompt = cancellation.Cancel };
        var store = new LinuxSecretServiceStore(() => bus);
        var result = delete ? await store.DeleteAsync(Reference(), true, cancellation.Token)
            : await store.WriteAsync(Reference(), new("key", "secret"), true, cancellation.Token);
        Assert.Equal(CredentialStatus.Cancelled, result);
        Assert.True(bus.PromptDismissed); Assert.True(bus.Disposed); Assert.True(bus.WatchDisposed);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Stalled_mutation_confirmation_uses_original_deadline(bool delete)
    {
        var bus = new FakeBus { Exists = delete, MutationPrompt = true, StallVerification = true };
        var store = new LinuxSecretServiceStore(() => bus, TimeSpan.FromMilliseconds(50));
        var result = delete ? store.DeleteAsync(Reference(), true, TestContext.Current.CancellationToken) : store.WriteAsync(Reference(), new("key", "secret"), true, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialStatus.StorageFailure, await result.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(bus.Disposed);
    }
    private sealed class FakeBus : ISecretBus, ISecretService, ISecretCollection, ISecretItem, ISecretSession, ISecretPrompt
    {
        private static ObjectPath None => new("/");
        private static ObjectPath Item => new("/item");
        private static ObjectPath Session => new("/session");
        public ObjectPath ObjectPath => new("/test");
        public bool Locked, Exists, UsePrompt, DismissPrompt, Disposed, WatchDisposed, PromptDismissed, MissingCollection, Replace, Stall, Duplicate, CannotConnect;
        public int Unlocks, PromptCalls, SessionsClosed;
        public bool MutationPrompt, LockAfterMutation, VerificationError, StallVerification;
        public bool MutationEffective = true;
        public object MutationResult = new ObjectPath("/item");
        private string? _mutation;
        public List<IDictionary<string, string>> Searches = [];
        public string? Error;
        public byte[]? ReturnedBytes, WrittenBytes;
        public IDictionary<string, string>? Attributes;
        public Action? OnPrompt;
        private Action<(bool Dismissed, object Result)>? _completed;
        public Task ConnectAsync() => CannotConnect ? Task.FromException(new ConnectException("synthetic connection failure"))
            : Stall ? new TaskCompletionSource().Task : Error is null ? Task.CompletedTask : Task.FromException(new DBusException(Error, "synthetic-secret"));
        public T Proxy<T>(ObjectPath path) where T : class, IDBusObject => (T)(object)this;
        public void Dispose() => Disposed = true;
        public Task<(object Output, ObjectPath Session)> OpenSessionAsync(string algorithm, object input)
        { Assert.Equal("plain", algorithm); return Task.FromResult<(object, ObjectPath)>(("", Session)); }
        public Task<(ObjectPath[] Unlocked, ObjectPath[] Locked)> SearchItemsAsync(IDictionary<string, string> attributes)
        {
            Searches.Add(new Dictionary<string, string>(attributes));
            if (_mutation is not null && VerificationError) throw new DBusException("org.freedesktop.Secret.Error.NoSuchObject", "synthetic");
            if (_mutation is not null && StallVerification) return new TaskCompletionSource<(ObjectPath[], ObjectPath[])>().Task;
            return Task.FromResult<(ObjectPath[], ObjectPath[])>(Duplicate ? ([Item, Item], []) : !Exists ? ([], []) : Locked ? ([], [Item]) : ([Item], []));
        }
        public Task<(ObjectPath[] Unlocked, ObjectPath Prompt)> UnlockAsync(ObjectPath[] objects)
        { Unlocks++; if (!UsePrompt) Locked = false; return Task.FromResult<(ObjectPath[], ObjectPath)>((UsePrompt ? [] : objects, UsePrompt ? new("/prompt") : None)); }
        public Task<ObjectPath> ReadAliasAsync(string name) => Task.FromResult(MissingCollection ? None : new ObjectPath("/collection"));
        public Task<T> GetAsync<T>(string property) => Task.FromResult((T)(object)Locked);
        public Task<(ObjectPath Item, ObjectPath Prompt)> CreateItemAsync(IDictionary<string, object> properties,
            (ObjectPath Session, byte[] Parameters, byte[] Value, string ContentType) secret, bool replace)
        {
            Attributes = (IDictionary<string, string>)properties["org.freedesktop.Secret.Item.Attributes"];
            WrittenBytes = secret.Value; Replace = replace; _mutation = "create";
            if (!MutationPrompt) ApplyMutation();
            return Task.FromResult(MutationPrompt ? (None, new ObjectPath("/prompt")) : (Item, None));
        }
        public Task<(ObjectPath Session, byte[] Parameters, byte[] Value, string ContentType)> GetSecretAsync(ObjectPath session)
        { ReturnedBytes = CredentialCodec.Encode(new("key", "secret")); return Task.FromResult<(ObjectPath, byte[], byte[], string)>((Session, [], ReturnedBytes, "application/json")); }
        public Task<ObjectPath> DeleteAsync()
        {
            _mutation = "delete"; if (!MutationPrompt) ApplyMutation();
            return Task.FromResult(MutationPrompt ? new ObjectPath("/prompt") : None);
        }
        private void ApplyMutation()
        {
            if (MutationEffective) Exists = _mutation == "create";
            Locked = LockAfterMutation;
        }
        public Task CloseAsync() { SessionsClosed++; return Task.CompletedTask; }
        public Task PromptAsync(string windowId)
        {
            PromptCalls++; Assert.NotNull(_completed);
            if (OnPrompt is not null) OnPrompt();
            else
            {
                if (!DismissPrompt) { if (_mutation is null) Locked = false; else ApplyMutation(); }
                _completed((DismissPrompt, _mutation is null ? new[] { Item } : MutationResult));
            }
            return Task.CompletedTask;
        }
        public Task DismissAsync() { PromptDismissed = true; return Task.CompletedTask; }
        public Task<IDisposable> WatchCompletedAsync(Action<(bool Dismissed, object Result)> handler, Action<Exception> onError)
        { _completed = handler; return Task.FromResult<IDisposable>(new Callback(() => WatchDisposed = true)); }
    }
    private sealed class Callback(Action action) : IDisposable { public void Dispose() => action(); }
}
