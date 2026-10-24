using System.Security.Cryptography;
using BucketBus.Core;
using Tmds.DBus;

namespace BucketBus.Platform;

public sealed class LinuxSecretServiceStore : ICredentialStore
{
    private readonly Func<ISecretBus?> _connect;
    private readonly TimeSpan _deadline;
    public LinuxSecretServiceStore() : this(CreateLocalBus, TimeSpan.FromSeconds(60)) { }
    internal LinuxSecretServiceStore(Func<ISecretBus?> connect, TimeSpan? deadline = null)
    { _connect = connect; _deadline = deadline ?? TimeSpan.FromSeconds(60); }

    private static ISecretBus? CreateLocalBus()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        // Plain Secret Service sessions are restricted to the authenticated local Unix bus.
        // Never send a secret over a TCP bus or an address fallback list.
        return address is not null && address.StartsWith("unix:", StringComparison.Ordinal)
            && !address.Contains(';') ? new SecretBus(address) : null;
    }
    public Task<CredentialResult> ReadAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default) =>
        ExecuteAsync(reference, null, Operation.Read, allowUnlock, cancellationToken);
    public async Task<CredentialStatus> WriteAsync(CredentialReference reference, Credentials credentials, bool allowUnlock, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(reference, credentials, Operation.Write, allowUnlock, cancellationToken)).Status;
    public async Task<CredentialStatus> DeleteAsync(CredentialReference reference, bool allowUnlock, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(reference, null, Operation.Delete, allowUnlock, cancellationToken)).Status;
    private enum Operation { Read, Write, Delete }

    private async Task<CredentialResult> ExecuteAsync(CredentialReference reference, Credentials? credentials, Operation operation,
        bool allowUnlock, CancellationToken cancellationToken)
    {
        if (!reference.IsValid || reference.Storage != CredentialStorage.Secure || credentials?.SessionToken is not null)
            return new(CredentialStatus.Denied);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);
        var token = deadline.Token;
        var failure = operation == Operation.Read ? CredentialStatus.RetrievalFailure : CredentialStatus.StorageFailure;
        try
        {
            token.ThrowIfCancellationRequested();
            using var bus = _connect();
            if (bus is null) return new(CredentialStatus.Unavailable);
            await bus.ConnectAsync().WaitAsync(token);
            var service = bus.Proxy<ISecretService>(new ObjectPath("/org/freedesktop/secrets"));
            var attributes = new Dictionary<string, string> { ["application"] = "BucketBus", ["binding"] = reference.VaultKey };
            var matches = await service.SearchItemsAsync(attributes).WaitAsync(token);
            if (!ValidMatches(matches)) return new(failure);
            if (matches.Locked.Length > 0)
            {
                if (!allowUnlock) return new(CredentialStatus.Locked);
                await UnlockAsync(bus, service, matches.Locked, token);
                matches = await service.SearchItemsAsync(attributes).WaitAsync(token);
                if (!ValidMatches(matches)) return new(failure);
                if (matches.Locked.Length > 0) return new(CredentialStatus.Locked);
            }
            if (operation == Operation.Delete)
            {
                if (matches.Unlocked.Length == 0) return new(CredentialStatus.Missing);
                var prompt = await bus.Proxy<ISecretItem>(matches.Unlocked[0]).DeleteAsync().WaitAsync(token);
                // Delete has no specified result variant: confirm absence, not a truthy result.
                await PromptAsync(bus, prompt, allowUnlock, token);
                var remaining = await service.SearchItemsAsync(attributes).WaitAsync(token);
                return new(remaining.Unlocked.Length == 0 && remaining.Locked.Length == 0
                    ? CredentialStatus.Success : failure);
            }
            if (operation == Operation.Read && matches.Unlocked.Length == 0) return new(CredentialStatus.Missing);
            // Bindings are write-once. Replacement uses a fresh reference, never an in-place overwrite.
            if (operation == Operation.Write && matches.Unlocked.Length != 0) return new(CredentialStatus.Denied);
            ObjectPath collection = default;
            if (operation == Operation.Write)
            {
                collection = await service.ReadAliasAsync("default").WaitAsync(token);
                if (!IsObject(collection)) return new(CredentialStatus.Unavailable);
                var proxy = bus.Proxy<ISecretCollection>(collection);
                if (await proxy.GetAsync<bool>("Locked").WaitAsync(token))
                {
                    if (!allowUnlock) return new(CredentialStatus.Locked);
                    await UnlockAsync(bus, service, [collection], token);
                    if (await proxy.GetAsync<bool>("Locked").WaitAsync(token)) return new(CredentialStatus.Locked);
                }
            }
            var (_, session) = await service.OpenSessionAsync("plain", "").WaitAsync(token);
            if (!IsObject(session)) return new(failure);
            try
            {
                if (operation == Operation.Read)
                {
                    var secret = await bus.Proxy<ISecretItem>(matches.Unlocked[0]).GetSecretAsync(session).WaitAsync(token);
                    try
                    {
                        if (secret.Session != session || secret.Parameters.Length != 0 || secret.Value.Length > 8192)
                            return new(CredentialStatus.RetrievalFailure);
                        return new(CredentialStatus.Found, CredentialCodec.Decode(secret.Value));
                    }
                    finally { CryptographicOperations.ZeroMemory(secret.Value); }
                }
                var bytes = CredentialCodec.Encode(credentials!);
                try
                {
                    var properties = new Dictionary<string, object>
                    {
                        ["org.freedesktop.Secret.Item.Label"] = "BucketBus connection credential",
                        ["org.freedesktop.Secret.Item.Attributes"] = attributes
                    };
                    var result = await bus.Proxy<ISecretCollection>(collection).CreateItemAsync(properties,
                        (session, [], bytes, "application/json"), false).WaitAsync(token);
                    var completion = await PromptAsync(bus, result.Prompt, allowUnlock, token);
                    ObjectPath created;
                    if (result.Prompt.ToString() == "/") created = result.Item;
                    else
                    {
                        // A prompted CreateItem returns '/' immediately; Completed carries an object path.
                        if (result.Item.ToString() != "/" || completion is not ObjectPath item) return new(failure);
                        created = item;
                    }
                    if (!IsObject(created)) return new(failure);
                    var confirmed = await service.SearchItemsAsync(attributes).WaitAsync(token);
                    // Never accept an unrelated, absent, locked or ambiguous item as our new binding.
                    return new(confirmed.Locked.Length == 0 && confirmed.Unlocked.Length == 1
                        && confirmed.Unlocked[0] == created ? CredentialStatus.Success : failure);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally
            {
                try { await bus.Proxy<ISecretSession>(session).CloseAsync().WaitAsync(token); }
                catch (Exception) { /* Fixed result only; never log a native error or secret. Connection is disposed next. */ }
            }
        }
        catch (VaultStatusException e) { return new(e.Status); }
        catch (OperationCanceledException) { return new(cancellationToken.IsCancellationRequested ? CredentialStatus.Cancelled : failure); }
        catch (ConnectException) { return new(CredentialStatus.Unavailable); }
        catch (DisconnectedException) { return new(CredentialStatus.Unavailable); }
        catch (DBusException e)
        {
            // Only an explicit empty owned-entry search establishes Missing for cleanup.
            return new(operation != Operation.Read && e.ErrorName == "org.freedesktop.Secret.Error.NoSuchObject"
                ? failure : MapError(e.ErrorName, failure));
        }
        catch (Exception) { return new(failure); }
    }
    private static bool IsObject(ObjectPath path) => path.ToString() is { Length: > 1 } value && value[0] == '/';
    private static bool ValidMatches((ObjectPath[] Unlocked, ObjectPath[] Locked) matches) =>
        matches.Unlocked.Length + matches.Locked.Length <= 1 && matches.Unlocked.Concat(matches.Locked).All(IsObject);
    private static async Task UnlockAsync(ISecretBus bus, ISecretService service, ObjectPath[] paths, CancellationToken token)
    {
        var result = await service.UnlockAsync(paths).WaitAsync(token);
        await PromptAsync(bus, result.Prompt, true, token);
    }
    private static async Task<object?> PromptAsync(ISecretBus bus, ObjectPath path, bool allowed, CancellationToken token)
    {
        if (path.ToString() == "/") return null;
        if (!IsObject(path)) throw new VaultStatusException(CredentialStatus.StorageFailure);
        var prompt = bus.Proxy<ISecretPrompt>(path);
        try
        {
            if (!allowed) throw new VaultStatusException(CredentialStatus.Locked);
            var completion = new TaskCompletionSource<(bool Dismissed, object Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = await prompt.WatchCompletedAsync(r => completion.TrySetResult(r),
                e => completion.TrySetException(e)).WaitAsync(token);
            await prompt.PromptAsync("").WaitAsync(token);
            var completed = await completion.Task.WaitAsync(token);
            if (completed.Dismissed) throw new VaultStatusException(CredentialStatus.Cancelled);
            return completed.Result;
        }
        catch
        {
            try { await prompt.DismissAsync().WaitAsync(token); } catch (Exception) { }
            throw;
        }
    }
    internal static CredentialStatus MapError(string name, CredentialStatus failure) => name switch
    {
        "org.freedesktop.Secret.Error.IsLocked" => CredentialStatus.Locked,
        "org.freedesktop.Secret.Error.NoSuchObject" => CredentialStatus.Missing,
        "org.freedesktop.DBus.Error.AccessDenied" or "org.freedesktop.DBus.Error.AuthFailed" => CredentialStatus.Denied,
        "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner"
            or "org.freedesktop.DBus.Error.NoServer" or "org.freedesktop.DBus.Error.Disconnected" => CredentialStatus.Unavailable,
        _ => failure
    };
    private sealed class VaultStatusException(CredentialStatus status) : Exception
    { public CredentialStatus Status { get; } = status; }
}
