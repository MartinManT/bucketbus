using System.Text.Json;
using System.Text.Json.Serialization;
using BucketBus.Core;

namespace BucketBus.Platform;

public sealed class JsonPreferencesStore(string directory) : IPreferencesStore
{
    private sealed record Document(int SchemaVersion, FileSizeDisplay FileSizes, RemoteNameDisplay RemoteNames);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    internal Action? BeforeCommit { get; set; }
    private string FilePath => Path.Combine(directory, "preferences.json");
    public Task<PreferencesReadResult> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        try
        {
            token.ThrowIfCancellationRequested(); ValidatePaths();
            if (!File.Exists(FilePath)) return new PreferencesReadResult(new(), PreferencesReadStatus.Missing);
            using var file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 4096) return new(new(), PreferencesReadStatus.Invalid);
            var document = JsonSerializer.Deserialize<Document>(file, Options);
            var value = document is null ? new() : new DisplayPreferences(document.FileSizes, document.RemoteNames);
            return document is { SchemaVersion: 1 } && value.IsValid ? new(value, PreferencesReadStatus.Loaded) : new(new(), PreferencesReadStatus.Invalid);
        }
        catch (JsonException) { return new(new(), PreferencesReadStatus.Invalid); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        { return new(new(), PreferencesReadStatus.Unavailable); }
    });
    public Task<bool> SaveAsync(DisplayPreferences value, CancellationToken token) => Task.Run(() =>
    {
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!value.IsValid) return false;
            ValidatePaths();
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var lockPath = Path.Combine(directory, "preferences.lock"); RejectLink(lockPath);
            using var owner = OpenPrivate(lockPath, FileMode.OpenOrCreate);
            temporary = Path.Combine(directory, $".preferences-{Guid.NewGuid():N}.tmp");
            using (var file = OpenPrivate(temporary, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(file, new Document(1, value.FileSizes, value.RemoteNames), Options);
                file.Flush(true);
            }
            BeforeCommit?.Invoke(); token.ThrowIfCancellationRequested(); RejectLink(FilePath);
            File.Move(temporary, FilePath, true); temporary = null;
            return true; // Rename is the commit boundary. Never report late cancellation as a failed save.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        { return false; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });
    private void ValidatePaths()
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Application data directory unavailable.");
        for (var path = directory; path is not null; path = Path.GetDirectoryName(path)) RejectLink(path);
        RejectLink(FilePath);
    }
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || (Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Preferences links are unsupported.");
    }
    private static FileStream OpenPrivate(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }
}
