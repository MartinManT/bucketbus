namespace BucketBus.Core;

public enum LocalEntryKind { Directory, File, SymbolicLink, Other, Unavailable }
public enum LocalBrowseError { None, InvalidPath, AccessDenied, NotFound, Unavailable, Cancelled }

// Exact native names/paths stay separate from display formatting. No file contents are read.
public sealed record LocalEntry(string Name, string FullPath, LocalEntryKind Kind, bool CanEnter,
    long? Size, DateTimeOffset? Modified, bool MetadataAvailable = true);
public sealed record LocalDirectoryResult(string Directory, IReadOnlyList<LocalEntry> Entries,
    LocalBrowseError Error = LocalBrowseError.None, bool IsPartial = false);

// Only the operations used by the local pane, not a virtual filesystem or transfer API.
public interface ILocalDirectoryBrowser
{
    string HomeDirectory { get; }
    Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken cancellationToken = default);
    Task<LocalDirectoryResult> ReadAsync(string path, CancellationToken cancellationToken = default);
}
public static class LocalBrowsingLimits
{
    public const int MaximumEntries = 10_000;
}
