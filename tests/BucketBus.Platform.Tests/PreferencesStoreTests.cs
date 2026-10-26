using BucketBus.Core;
using BucketBus.Platform;

namespace BucketBus.Platform.Tests;

public sealed class PreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bucketbus-preferences-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Missing_preferences_use_defaults_without_creating_journal_or_directory()
    {
        var read = await new JsonPreferencesStore(_directory).ReadAsync(Token);
        Assert.Equal(PreferencesReadStatus.Missing, read.Status); Assert.Equal(new(), read.Value);
        Assert.False(Directory.Exists(_directory));
    }
    [Fact]
    public async Task Preferences_survive_restart_without_profiles_credentials_or_sqlite()
    {
        var value = new DisplayPreferences(FileSizeDisplay.ExactBytes, RemoteNameDisplay.FullKey);
        Assert.True(await new JsonPreferencesStore(_directory).SaveAsync(value, Token));
        var read = await new JsonPreferencesStore(_directory).ReadAsync(Token);
        Assert.Equal(value, read.Value); Assert.Equal(PreferencesReadStatus.Loaded, read.Status);
        Assert.Equal(new[] { "preferences.json", "preferences.lock" }, Directory.GetFiles(_directory).Select(Path.GetFileName).Order().ToArray());
        var json = await File.ReadAllTextAsync(Path.Combine(_directory, "preferences.json"), Token);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("endpoint", json, StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":2,\"FileSizes\":\"ExactBytes\"}")]
    [InlineData("{\"SchemaVersion\":1,\"FileSizes\":\"UnknownChoice\"}")]
    [InlineData("{\"SchemaVersion\":1,\"FileSizes\":999}")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1}")]
    public async Task Invalid_preferences_fall_back_without_overwriting_original(string json)
    {
        Directory.CreateDirectory(_directory); var file = Path.Combine(_directory, "preferences.json");
        await File.WriteAllTextAsync(file, json, Token);
        var read = await new JsonPreferencesStore(_directory).ReadAsync(Token);
        Assert.Equal(PreferencesReadStatus.Invalid, read.Status); Assert.Equal(new(), read.Value);
        Assert.Equal(json, await File.ReadAllTextAsync(file, Token));
    }
    [Fact]
    public async Task Interrupted_atomic_save_preserves_previous_document_and_removes_owned_temp()
    {
        var store = new JsonPreferencesStore(_directory); Assert.True(await store.SaveAsync(new(), Token));
        store.BeforeCommit = () => throw new IOException("synthetic interrupted save");
        Assert.False(await store.SaveAsync(new(FileSizeDisplay.ExactBytes), Token));
        Assert.Equal(new(), (await new JsonPreferencesStore(_directory).ReadAsync(Token)).Value);
        Assert.Empty(Directory.GetFiles(_directory, ".preferences-*.tmp"));
    }
    [Fact]
    public async Task Unwritable_location_reports_failure_and_read_defaults()
    {
        Directory.CreateDirectory(_directory); var blocked = Path.Combine(_directory, "blocked");
        await File.WriteAllTextAsync(blocked, "fixture", Token);
        var store = new JsonPreferencesStore(blocked);
        Assert.False(await store.SaveAsync(new(), Token));
        Assert.Equal(new(), (await store.ReadAsync(Token)).Value);
        Assert.Equal("fixture", await File.ReadAllTextAsync(blocked, Token));
    }
    [Fact]
    public async Task Corrupt_journal_is_independent_of_preferences()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "transfers"));
        var journal = Path.Combine(_directory, "transfers", "transfers.db");
        await File.WriteAllTextAsync(journal, "deliberately corrupt fixture", Token);
        var store = new JsonPreferencesStore(_directory); Assert.True(await store.SaveAsync(new(), Token));
        Assert.Equal(PreferencesReadStatus.Loaded, (await store.ReadAsync(Token)).Status);
        Assert.Equal("deliberately corrupt fixture", await File.ReadAllTextAsync(journal, Token));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
