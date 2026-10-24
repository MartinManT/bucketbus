namespace BucketBus.Core;

public enum TransferState { Completed, Failed, Cancelled, OutcomeUnknown }
public enum TransferError
{
    None, Conflict, InvalidName, UnsupportedFilesystem, PermissionDenied, DiskFull, LocalFailure,
    IntegrityMismatch, SourceChanged, NetworkFailure, Timeout, CredentialsUnavailable, ProfileChanged,
    ConditionalWriteUnsupported, RemoteRejected, Busy, CleanupRequired, AuthenticationFailed
}
public sealed record TransferResult(TransferState State, TransferError Error = TransferError.None,
    string Verification = "", bool CleanupRequired = false);
public sealed record TransferProgress(long Bytes, long? Total, string Stage);
public sealed class TransferException(TransferError error) : Exception("Transfer operation failed.")
{ public TransferError Error { get; } = error; }

public interface ITransferSource : IAsyncDisposable
{
    Stream Content { get; }
    long Length { get; }
    string Sha256 { get; }
    string Md5 { get; }
    Task ValidateAsync(CancellationToken token);
}
public interface ITransferDestination : IAsyncDisposable
{
    Stream Content { get; }
    // The commit is the cancellation boundary: once committed the result is Completed.
    Task CommitAsync(CancellationToken token);
    bool CleanupRequired { get; }
}
public interface ITransferFiles
{
    Task<ITransferSource> OpenSourceAsync(string path, CancellationToken token);
    Task<ITransferDestination> CreateDestinationAsync(string directory, string name, CancellationToken token);
}
public interface IObjectTransferSession : IAsyncDisposable
{
    Task<TransferResult> UploadAsync(string bucket, string key, ITransferSource source, IProgress<TransferProgress>? progress, CancellationToken token);
    Task<TransferResult> DownloadAsync(string bucket, string key, Stream destination, IProgress<TransferProgress>? progress, CancellationToken token);
}
public interface IObjectTransferFactory
{ IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials); }

/// <summary>One explicit operation; independent of browse navigation and never persisted/replayed.</summary>
public sealed class TransferCoordinator(ProfileManager profiles, ITransferFiles files, IObjectTransferFactory factory)
{
    private int _active;
    public async Task<TransferResult> RunAsync(ConnectionProfile profile, string bucket, string key, string localPath,
        bool upload, bool allowUnlock, IProgress<TransferProgress>? progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _active, 1) != 0) return new(TransferState.Failed, TransferError.Busy);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        void Changed(Guid id) { if (id == profile.Id) lifetime.Cancel(); }
        profiles.ProfileChanged += Changed;
        ITransferDestination? destination = null;
        TransferResult result;
        var uploadStarted = false;
        var cleanupFailed = false;
        try
        {
            var ct = lifetime.Token;
            var snapshot = await profiles.ReadAsync(ct).ConfigureAwait(false);
            if (snapshot.Status != ProfileStatus.Success || !snapshot.Snapshot!.Profiles.Contains(profile))
                return new(TransferState.Failed, TransferError.ProfileChanged);
            var credential = await profiles.ReadCredentialAsync(profile.Id, profile.Revision, allowUnlock, ct).ConfigureAwait(false);
            if (credential.Status != CredentialStatus.Found || credential.Value is null)
                return new(ct.IsCancellationRequested ? TransferState.Cancelled : TransferState.Failed, TransferError.CredentialsUnavailable);
            ct.ThrowIfCancellationRequested();
            await using var remote = factory.Create(profile, credential.Value);
            if (upload)
            {
                await using var source = await files.OpenSourceAsync(localPath, ct).ConfigureAwait(false);
                var latest = await profiles.ReadAsync(ct).ConfigureAwait(false);
                if (latest.Status != ProfileStatus.Success || !latest.Snapshot!.Profiles.Contains(profile))
                    return new(TransferState.Failed, TransferError.ProfileChanged);
                ct.ThrowIfCancellationRequested();
                uploadStarted = true;
                result = await remote.UploadAsync(bucket, key, source, progress, ct).ConfigureAwait(false);
            }
            else
            {
                destination = await files.CreateDestinationAsync(localPath, TransferNames.FromKey(key), ct).ConfigureAwait(false);
                result = await remote.DownloadAsync(bucket, key, destination.Content, progress, ct).ConfigureAwait(false);
                if (result.State == TransferState.Completed) await destination.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { result = new(uploadStarted ? TransferState.OutcomeUnknown : TransferState.Cancelled); }
        catch (TransferException e) { result = new(uploadStarted ? TransferState.OutcomeUnknown : TransferState.Failed, e.Error); }
        catch (Exception) { result = new(uploadStarted ? TransferState.OutcomeUnknown : TransferState.Failed, TransferError.LocalFailure); }
        finally
        {
            if (destination is not null)
                try { await destination.DisposeAsync().ConfigureAwait(false); } catch { cleanupFailed = true; }
            profiles.ProfileChanged -= Changed;
            Volatile.Write(ref _active, 0);
        }
        return cleanupFailed || destination?.CleanupRequired == true ? result with { CleanupRequired = true } : result;
    }
}

public static class TransferNames
{
    // A single visible leaf, never a trusted path; no silent sanitizing or normalization.
    public static string FromKey(string key)
    {
        if (key.Split('/').Any(p => p is "." or "..")) throw new TransferException(TransferError.InvalidName);
        var name = key[(key.LastIndexOf('/') + 1)..];
        Validate(name);
        return name;
    }
    public static void Validate(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0
            || name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new TransferException(TransferError.InvalidName);
        try { if (new System.Text.UTF8Encoding(false, true).GetByteCount(name) > 240) throw new TransferException(TransferError.InvalidName); }
        catch (System.Text.EncoderFallbackException) { throw new TransferException(TransferError.InvalidName); }
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]))
            throw new TransferException(TransferError.InvalidName);
    }
}
