using System.Text.Json;
using System.Text.Json.Serialization;
using BucketBus.Core;

namespace BucketBus.Platform;

public sealed class JsonProfileStore(string directory) : IProfileStore
{
    private const int MaximumBytes = 1_048_576;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    internal Action? BeforeCommit { get; set; }
    private sealed record Document(int SchemaVersion, long Revision, ConnectionProfile[] Profiles);
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BucketBus");
    private string FilePath => Path.Combine(directory, "profiles.json");

    public Task<ProfileReadResult> ReadAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = Lock();
            return ReadLocked();
        }
        catch (OperationCanceledException) { return new ProfileReadResult(ProfileStatus.Cancelled); }
        catch (UnauthorizedAccessException) { return new ProfileReadResult(ProfileStatus.Unavailable); }
        catch (IOException) { return new ProfileReadResult(ProfileStatus.Failure); }
    });

    public Task<ProfileStatus> WriteAsync(ProfileSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Revision != checked(expectedRevision + 1) || !Valid(snapshot.Profiles)) return ProfileStatus.Invalid;
            using var lease = Lock();
            var current = ReadLocked();
            if (current.Status != ProfileStatus.Success) return current.Status;
            if (current.Snapshot!.Revision != expectedRevision) return ProfileStatus.Conflict;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, snapshot.Revision, snapshot.Profiles.ToArray()), Options);
            if (bytes.Length > MaximumBytes) return ProfileStatus.Invalid;
            temporary = Path.Combine(directory, $".profiles-{Guid.NewGuid():N}.tmp");
            using (var file = OpenPrivate(temporary, FileMode.CreateNew))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            BeforeCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            // Same-directory rename is the commit point. Never report cancellation after it.
            File.Move(temporary, FilePath, overwrite: true);
            temporary = null;
            return ProfileStatus.Success;
        }
        catch (OperationCanceledException) { return ProfileStatus.Cancelled; }
        catch (UnauthorizedAccessException) { return ProfileStatus.Unavailable; }
        catch (IOException) { return ProfileStatus.Failure; }
        catch (ArgumentException) { return ProfileStatus.Invalid; }
        catch (OverflowException) { return ProfileStatus.Invalid; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });

    private FileStream Lock()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RejectLink(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = Path.Combine(directory, "profiles.lock");
        RejectLink(lockPath);
        RejectLink(FilePath);
        return OpenPrivate(lockPath, FileMode.OpenOrCreate);
    }
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || (File.Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Profile links are not supported.");
    }
    private static FileStream OpenPrivate(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
    private ProfileReadResult ReadLocked()
    {
        if (!File.Exists(FilePath)) return new(ProfileStatus.Success, new(0, []));
        try
        {
            using var file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumBytes) return new(ProfileStatus.Invalid);
            using var root = JsonDocument.Parse(file);
            if (root.RootElement.ValueKind != JsonValueKind.Object) return new(ProfileStatus.Invalid);
            if (root.RootElement.TryGetProperty("SchemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number
                && schema.TryGetInt32(out var version) && version > 1)
                return new(ProfileStatus.UnsupportedSchema);
            var document = root.RootElement.Deserialize<Document>(Options);
            if (document is null || document.SchemaVersion < 1) return new(ProfileStatus.Invalid);
            if (document.SchemaVersion != 1) return new(ProfileStatus.UnsupportedSchema);
            if (document.Revision < 1 || !Valid(document.Profiles)) return new(ProfileStatus.Invalid);
            return new(ProfileStatus.Success, new(document.Revision, document.Profiles));
        }
        catch (JsonException) { return new(ProfileStatus.Invalid); }
        catch (ArgumentException) { return new(ProfileStatus.Invalid); }
    }
    private static bool Valid(IReadOnlyList<ConnectionProfile>? profiles)
    {
        if (profiles is null || profiles.Count > 1000 || profiles.Any(p => p is null)
            || profiles.Select(p => p.Id).Distinct().Count() != profiles.Count) return false;
        foreach (var profile in profiles) profile.Validate();
        return true;
    }
}
