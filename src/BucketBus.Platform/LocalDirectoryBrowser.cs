using BucketBus.Core;

namespace BucketBus.Platform;

public sealed class LocalDirectoryBrowser : ILocalDirectoryBrowser
{
    private readonly Func<string, IEnumerable<FileSystemInfo>> _enumerate;
    public LocalDirectoryBrowser() : this(Enumerate) { }
    internal LocalDirectoryBrowser(Func<string, IEnumerable<FileSystemInfo>> enumerate) => _enumerate = enumerate;
    // At most one OS enumeration, even if a slow/removable filesystem ignores cancellation.
    private readonly SemaphoreSlim _enumerating = new(1, 1);
    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public async Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken cancellationToken = default)
    {
        var work = Task.Run<IReadOnlyList<string>>(() => OperatingSystem.IsWindows()
            ? Directory.GetLogicalDrives() : ["/"]);
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalDirectoryResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            // Do not trim whitespace, expand variables, Unicode-normalize or resolve links.
            // Reject dot segments rather than silently changing link/../ semantics.
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || path.Contains('\0')
                || path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(p => p is "." or ".."))
                return new(path, [], LocalBrowseError.InvalidPath);
            await _enumerating.WaitAsync(cancellationToken).ConfigureAwait(false);
            var work = Task.Run(() =>
            {
                try { return ReadDirectory(path, cancellationToken); }
                finally { _enumerating.Release(); }
            });
            // Cancellation stops the caller promptly; the single worker releases its gate
            // only when the OS call returns. A new navigation cannot accumulate blocked workers.
            return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(path, [], LocalBrowseError.Cancelled); }
        catch (Exception e) when (IsFilesystemError(e)) { return new(path, [], Classify(e)); }
    }

    private LocalDirectoryResult ReadDirectory(string path, CancellationToken token)
    {
        var entries = new List<LocalEntry>();
        try
        {
            using var enumerator = _enumerate(path).GetEnumerator();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext()) return new(path, entries);
                token.ThrowIfCancellationRequested();
                if (entries.Count == LocalBrowsingLimits.MaximumEntries) return new(path, entries, IsPartial: true);
                var info = enumerator.Current;
                try
                {
                    // Enumeration caches metadata; Refresh makes disappeared entries visible as unavailable.
                    info.Refresh();
                    var attributes = info.Attributes;
                    if ((int)attributes == -1) throw new FileNotFoundException();
                    var link = (attributes & FileAttributes.ReparsePoint) != 0;
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var kind = link ? LocalEntryKind.SymbolicLink : isDirectory ? LocalEntryKind.Directory
                        : (attributes & FileAttributes.Device) != 0 ? LocalEntryKind.Other : LocalEntryKind.File;
                    entries.Add(new(info.Name, info.FullName, kind, isDirectory,
                        kind == LocalEntryKind.File && info is FileInfo file ? file.Length : null,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                }
                catch (Exception e) when (IsFilesystemError(e))
                {
                    entries.Add(new(info.Name, info.FullName, LocalEntryKind.Unavailable, false, null, null, false));
                }
            }
        }
        catch (OperationCanceledException) { return new(path, [], LocalBrowseError.Cancelled); }
        catch (Exception e) when (IsFilesystemError(e)) { return new(path, entries, Classify(e), entries.Count != 0); }
    }
    private static IEnumerable<FileSystemInfo> Enumerate(string path) => new DirectoryInfo(path).EnumerateFileSystemInfos("*",
        new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false,
            AttributesToSkip = 0, ReturnSpecialDirectories = false });
    private static bool IsFilesystemError(Exception e) => e is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static LocalBrowseError Classify(Exception e) => e switch
    {
        UnauthorizedAccessException or System.Security.SecurityException => LocalBrowseError.AccessDenied,
        DirectoryNotFoundException or FileNotFoundException => LocalBrowseError.NotFound,
        ArgumentException or NotSupportedException or PathTooLongException => LocalBrowseError.InvalidPath,
        _ => LocalBrowseError.Unavailable
    };
}
