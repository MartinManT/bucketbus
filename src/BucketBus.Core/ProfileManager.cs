namespace BucketBus.Core;

public sealed record ProfileChangeResult(ProfileStatus Status, CredentialStatus? CredentialStatus = null,
    bool CleanupRequired = false);

/// <summary>Coordinates metadata and vault writes without treating them as one atomic transaction.</summary>
public sealed class ProfileManager(IProfileStore profiles, ICredentialStore secure, SessionCredentialStore session)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<CredentialReference> _cleanup = [];
    public event Action<Guid>? ProfileChanged;
    public bool HasPendingCleanup => _cleanup.Count != 0;
    public Task<ProfileReadResult> ReadAsync(CancellationToken token = default) => profiles.ReadAsync(token);
    private ICredentialStore Store(CredentialReference reference) => reference.Storage == CredentialStorage.Session ? session : secure;

    public async Task<CredentialResult> ReadCredentialAsync(Guid id, long expectedRevision, bool allowUnlock, CancellationToken token = default)
    {
        var read = await profiles.ReadAsync(token);
        if (read.Status == ProfileStatus.Cancelled) return new(CredentialStatus.Cancelled);
        if (read.Status != ProfileStatus.Success) return new(CredentialStatus.RetrievalFailure);
        var profile = read.Snapshot!.Profiles.SingleOrDefault(p => p.Id == id);
        if (profile is null || profile.Revision != expectedRevision) return new(CredentialStatus.Denied);
        return profile.Credential is null ? new(CredentialStatus.Missing)
            : await Store(profile.Credential).ReadAsync(profile.Credential, allowUnlock, token);
    }

    public async Task<ProfileChangeResult> SaveAsync(Guid id, long expectedRevision, ProfileSettings settings,
        bool replaceCredentials, CredentialStorage storage, Credentials? credentials, bool allowUnlock,
        CancellationToken token = default)
    {
        try { settings = settings.Validate(); }
        catch (ArgumentException) { return new(ProfileStatus.Invalid); }
        if (id == Guid.Empty || !Enum.IsDefined(storage) || credentials is not null && !replaceCredentials
            || credentials?.SessionToken is not null && storage != CredentialStorage.Session)
            return new(ProfileStatus.Invalid);
        try { await _gate.WaitAsync(token); }
        catch (OperationCanceledException) { return new(ProfileStatus.Cancelled); }
        try
        {
            var read = await profiles.ReadAsync(token);
            if (read.Status != ProfileStatus.Success) return new(read.Status);
            var snapshot = read.Snapshot!;
            var old = snapshot.Profiles.SingleOrDefault(p => p.Id == id);
            if ((old?.Revision ?? 0) != expectedRevision) return new(ProfileStatus.Conflict);
            var targetChanged = old is not null && old.Settings.TargetFingerprint() != settings.TargetFingerprint();
            var binding = replaceCredentials || targetChanged ? null : old?.Credential;
            CredentialReference? created = null;
            if (replaceCredentials && credentials is not null)
            {
                created = new(id, Guid.NewGuid(), storage, settings.TargetFingerprint());
                // Fresh names ensure a failed update never replaces credentials used by the old profile.
                var status = await Store(created).WriteAsync(created, credentials, allowUnlock, token);
                if (status != CredentialStatus.Success)
                {
                    // A timeout/cancellation can happen after the vault accepted a write.
                    _cleanup.Add(created);
                    await CleanupOneAsync(created, false);
                    return new(ProfileStatus.Failure, status, HasPendingCleanup);
                }
                binding = created;
            }
            var updated = new ConnectionProfile(id, checked(expectedRevision + 1), settings, binding).Validate();
            var next = snapshot.Profiles.Where(p => p.Id != id).Append(updated).ToArray();
            var saved = await profiles.WriteAsync(new(snapshot.Revision + 1, next), snapshot.Revision, token);
            if (saved != ProfileStatus.Success)
            {
                if (created is not null) { _cleanup.Add(created); await CleanupOneAsync(created, false); }
                return new(saved, CleanupRequired: HasPendingCleanup);
            }
            ProfileChanged?.Invoke(id);
            if (old?.Credential is not null && old.Credential != binding)
            { _cleanup.Add(old.Credential); await CleanupOneAsync(old.Credential, allowUnlock && !token.IsCancellationRequested); }
            return new(ProfileStatus.Success, CleanupRequired: HasPendingCleanup);
        }
        finally { _gate.Release(); }
    }

    public async Task<ProfileChangeResult> DeleteAsync(Guid id, long expectedRevision, bool allowUnlock, CancellationToken token = default)
    {
        try { await _gate.WaitAsync(token); }
        catch (OperationCanceledException) { return new(ProfileStatus.Cancelled); }
        try
        {
            var read = await profiles.ReadAsync(token);
            if (read.Status != ProfileStatus.Success) return new(read.Status);
            var snapshot = read.Snapshot!;
            var old = snapshot.Profiles.SingleOrDefault(p => p.Id == id);
            if (old is null || old.Revision != expectedRevision) return new(ProfileStatus.Conflict);
            var saved = await profiles.WriteAsync(new(snapshot.Revision + 1,
                snapshot.Profiles.Where(p => p.Id != id).ToArray()), snapshot.Revision, token);
            if (saved != ProfileStatus.Success) return new(saved);
            ProfileChanged?.Invoke(id);
            if (old.Credential is not null) { _cleanup.Add(old.Credential); await CleanupOneAsync(old.Credential, allowUnlock && !token.IsCancellationRequested); }
            return new(ProfileStatus.Success, CleanupRequired: HasPendingCleanup);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RetryCleanupAsync(bool allowUnlock, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            foreach (var reference in _cleanup.ToArray())
            {
                await CleanupOneAsync(reference, allowUnlock, token);
                token.ThrowIfCancellationRequested();
            }
            return !HasPendingCleanup;
        }
        finally { _gate.Release(); }
    }
    private async Task CleanupOneAsync(CredentialReference reference, bool allowUnlock, CancellationToken token = default)
    {
        var status = await Store(reference).DeleteAsync(reference, allowUnlock, token);
        if (status is CredentialStatus.Success or CredentialStatus.Missing) _cleanup.Remove(reference);
    }
}
