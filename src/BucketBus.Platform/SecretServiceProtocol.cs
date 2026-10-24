using Tmds.DBus;

namespace BucketBus.Platform;

// Public for Tmds' runtime-generated proxy assembly. No application services depend on these types.
[DBusInterface("org.freedesktop.Secret.Service")]
public interface ISecretService : IDBusObject
{
    Task<(object Output, ObjectPath Session)> OpenSessionAsync(string algorithm, object input);
    Task<(ObjectPath[] Unlocked, ObjectPath[] Locked)> SearchItemsAsync(IDictionary<string, string> attributes);
    Task<(ObjectPath[] Unlocked, ObjectPath Prompt)> UnlockAsync(ObjectPath[] objects);
    Task<ObjectPath> ReadAliasAsync(string name);
}
[DBusInterface("org.freedesktop.Secret.Collection")]
public interface ISecretCollection : IDBusObject
{
    Task<T> GetAsync<T>(string property);
    Task<(ObjectPath Item, ObjectPath Prompt)> CreateItemAsync(IDictionary<string, object> properties,
        (ObjectPath Session, byte[] Parameters, byte[] Value, string ContentType) secret, bool replace);
}
[DBusInterface("org.freedesktop.Secret.Item")]
public interface ISecretItem : IDBusObject
{
    [return: Argument]
    Task<(ObjectPath Session, byte[] Parameters, byte[] Value, string ContentType)> GetSecretAsync(ObjectPath session);
    Task<ObjectPath> DeleteAsync();
}
[DBusInterface("org.freedesktop.Secret.Session")]
public interface ISecretSession : IDBusObject { Task CloseAsync(); }
[DBusInterface("org.freedesktop.Secret.Prompt")]
public interface ISecretPrompt : IDBusObject
{
    Task PromptAsync(string windowId);
    Task DismissAsync();
    Task<IDisposable> WatchCompletedAsync(Action<(bool Dismissed, object Result)> handler, Action<Exception> onError);
}

internal interface ISecretBus : IDisposable
{
    Task ConnectAsync();
    T Proxy<T>(ObjectPath path) where T : class, IDBusObject;
}
internal sealed class SecretBus(string address) : ISecretBus
{
    private readonly Connection _connection = new(address);
    public Task ConnectAsync() => _connection.ConnectAsync();
    public T Proxy<T>(ObjectPath path) where T : class, IDBusObject => _connection.CreateProxy<T>("org.freedesktop.secrets", path);
    public void Dispose() => _connection.Dispose();
}
