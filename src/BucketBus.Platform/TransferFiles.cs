using System.Security.Cryptography;
using BucketBus.Core;

namespace BucketBus.Platform;

public sealed class TransferFiles : ITransferFiles
{
    public Task<ITransferSource> OpenSourceAsync(string path, CancellationToken token) => Task.Run<ITransferSource>(async () =>
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var file = NativeTransferFile.OpenSource(path);
            try
            {
                var hashes = await HashAsync(file.Stream, token).ConfigureAwait(false);
                file.Validate(); file.Stream.Position = 0;
                return new Source(file, hashes.Sha, hashes.Md5);
            }
            catch { file.Dispose(); throw; }
        }
        catch (Exception e) { throw Map(e); }
    }, token);
    public Task<ITransferDestination> CreateDestinationAsync(string directory, string name, CancellationToken token) => Task.Run<ITransferDestination>(() =>
    {
        token.ThrowIfCancellationRequested(); TransferNames.Validate(name);
        try { return new Destination(NativeTransferFile.CreateDestination(directory, name)); }
        catch (Exception e) { throw Map(e); }
    }, token);
    public Task RecoverDestinationAsync(TransferRecord record, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (record.Upload || record.Temporary is not { } receipt) throw new TransferException(TransferError.RecoveryUnavailable);
        var suffix = receipt.Platform == "linux" ? ".tmp" : ".part";
        if (!receipt.Name.StartsWith(".bucketbus-", StringComparison.Ordinal) || !receipt.Name.EndsWith(suffix, StringComparison.Ordinal)
            || receipt.Name.Length != 11 + 32 + suffix.Length || !Guid.TryParseExact(receipt.Name[11..^suffix.Length], "N", out _)) throw new TransferException(TransferError.RecoveryUnavailable);
        TransferNames.Validate(receipt.Name);
        try
        {
            if (OperatingSystem.IsLinux() && receipt.Platform == "linux") LinuxTransferFile.Recover(record.LocalPath, TransferNames.FromKey(record.Key), receipt);
            else if (OperatingSystem.IsWindows() && receipt.Platform == "windows") WindowsTransferFile.Recover(record.LocalPath, TransferNames.FromKey(record.Key), receipt);
            else throw new TransferException(TransferError.UnsupportedFilesystem);
        }
        catch (Exception e) { throw Map(e); }
    }, token);
    private static async Task<(string Sha, string Md5)> HashAsync(Stream stream, CancellationToken token)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        { sha.AppendData(buffer, 0, count); md5.AppendData(buffer, 0, count); }
        return (Convert.ToBase64String(sha.GetHashAndReset()), Convert.ToBase64String(md5.GetHashAndReset()));
    }
    internal static Exception Map(Exception e) => e switch
    {
        TransferException or OperationCanceledException => e,
        UnauthorizedAccessException => new TransferException(TransferError.PermissionDenied),
        IOException io when (io.HResult & 0xffff) is 28 or 112 => new TransferException(TransferError.DiskFull),
        _ => new TransferException(TransferError.LocalFailure)
    };
    private sealed class Source(NativeTransferFile file, string sha, string md5) : ITransferSource
    {
        public Stream Content => file.Stream;
        public long Length => file.Stream.Length;
        public string Sha256 => sha;
        public string Md5 => md5;
        public async Task ValidateAsync(CancellationToken token)
        {
            file.Validate(); file.Stream.Position = 0;
            var current = await HashAsync(file.Stream, token).ConfigureAwait(false);
            file.Validate(); file.Stream.Position = 0;
            if (current.Sha != sha) throw new TransferException(TransferError.SourceChanged);
        }
        public ValueTask DisposeAsync() { file.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Destination(NativeTransferFile file) : ITransferDestination
    {
        public Stream Content => file.Stream;
        public TemporaryReceipt? Receipt => file.Receipt;
        public bool CleanupRequired => file.CleanupRequired;
        public Task CommitAsync(CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try { file.Stream.Flush(true); token.ThrowIfCancellationRequested(); file.Commit(); }
            catch (Exception e) { throw Map(e); }
        }, CancellationToken.None);
        public ValueTask DisposeAsync() { file.Dispose(); return ValueTask.CompletedTask; }
    }
}

internal abstract class NativeTransferFile : IDisposable
{
    public abstract FileStream Stream { get; }
    public virtual TemporaryReceipt? Receipt => null;
    public bool CleanupRequired { get; protected set; }
    public abstract void Validate();
    public abstract void Commit();
    public abstract void Dispose();
    public static NativeTransferFile OpenSource(string path) => OperatingSystem.IsLinux() ? LinuxTransferFile.OpenSource(path)
        : OperatingSystem.IsWindows() ? WindowsTransferFile.OpenSource(path) : throw new TransferException(TransferError.UnsupportedFilesystem);
    public static NativeTransferFile CreateDestination(string directory, string name) => OperatingSystem.IsLinux() ? LinuxTransferFile.CreateDestination(directory, name)
        : OperatingSystem.IsWindows() ? WindowsTransferFile.CreateDestination(directory, name) : throw new TransferException(TransferError.UnsupportedFilesystem);
}
