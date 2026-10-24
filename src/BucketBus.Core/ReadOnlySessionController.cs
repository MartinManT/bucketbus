namespace BucketBus.Core;

/// <summary>One explicit remote authority at a time; no native or provider types.</summary>
public sealed class ReadOnlySessionController : IDisposable, IAsyncDisposable
{
    private readonly ProfileManager _profiles;
    private readonly IReadOnlyStorageSessionFactory _factory;
    private readonly SemaphoreSlim _opening = new(1, 1);
    private readonly object _state = new();
    private IReadOnlyStorageSession? _current;
    private long _generation;
    private Guid _selectedId;
    private bool _disposed;
    public ReadOnlySessionController(ProfileManager profiles, IReadOnlyStorageSessionFactory factory)
    { _profiles = profiles; _factory = factory; _profiles.ProfileChanged += OnProfileChanged; }

    public async Task<SessionOpenResult> OpenAsync(Guid profileId, long revision, bool allowUnlock, CancellationToken token = default)
    {
        long generation;
        lock (_state)
        {
            if (_disposed) return new(null, StorageError.SessionExpired);
            generation = ++_generation;
            _selectedId = profileId;
            _current?.Invalidate();
        }
        try { await _opening.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(null, StorageError.Cancelled); }
        try
        {
            IReadOnlyStorageSession? old;
            lock (_state) { old = _current; _current = null; }
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            var read = await _profiles.ReadAsync(token).ConfigureAwait(false);
            if (read.Status == ProfileStatus.Cancelled) return new(null, StorageError.Cancelled);
            if (read.Status != ProfileStatus.Success) return new(null, StorageError.InvalidRequest);
            var profile = read.Snapshot!.Profiles.SingleOrDefault(p => p.Id == profileId && p.Revision == revision);
            if (profile?.Credential is null) return new(null, StorageError.InvalidRequest, Core.CredentialStatus.Missing);
            var credential = await _profiles.ReadCredentialAsync(profileId, revision, allowUnlock, token).ConfigureAwait(false);
            if (credential.Status != Core.CredentialStatus.Found || credential.Value is null)
                return new(null, credential.Status == Core.CredentialStatus.Cancelled ? StorageError.Cancelled : StorageError.AuthenticationFailed, credential.Status);
            async Task<bool> IsCurrent(CancellationToken ct)
            {
                lock (_state) if (_disposed || generation != _generation) return false;
                var latest = await _profiles.ReadAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return latest.Status == ProfileStatus.Success && latest.Snapshot!.Profiles.Any(p => p == profile);
            }
            if (!await IsCurrent(token).ConfigureAwait(false)) return new(null, StorageError.SessionExpired);
            var session = _factory.Create(profile, credential.Value, IsCurrent);
            lock (_state)
            {
                if (!_disposed && generation == _generation) { _current = session; return new(session, StorageError.None); }
            }
            await session.DisposeAsync().ConfigureAwait(false);
            return new(null, StorageError.SessionExpired);
        }
        catch (OperationCanceledException) { return new(null, StorageError.Cancelled); }
        catch (Exception) { return new(null, StorageError.ServiceFailure); }
        finally { _opening.Release(); }
    }
    private void OnProfileChanged(Guid id)
    {
        lock (_state)
        {
            if (id != _selectedId) return;
            // Also invalidate an authority being resolved. A later explicit Open starts fresh.
            ++_generation;
            _current?.Invalidate();
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_state) { _disposed = true; ++_generation; _current?.Invalidate(); }
        _profiles.ProfileChanged -= OnProfileChanged;
        await _opening.WaitAsync().ConfigureAwait(false);
        try
        {
            IReadOnlyStorageSession? current;
            lock (_state) { current = _current; _current = null; }
            if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
        }
        finally { _opening.Release(); }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
