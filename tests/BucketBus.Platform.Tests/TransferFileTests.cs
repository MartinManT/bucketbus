using System.Text;
using BucketBus.Core;

namespace BucketBus.Platform.Tests;

public sealed class TransferFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bucketbus-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly TransferFiles _files = new();
    public TransferFileTests() => Directory.CreateDirectory(_directory);
    [Theory]
    [InlineData("")] [InlineData("plain content")] [InlineData("é é 漢字 % +")]
    public async Task Completed_output_is_invisible_until_commit_and_owned_temporary_files_are_removed(string text)
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_directory, "é é 漢字.txt");
        await using (var destination = await _files.CreateDestinationAsync(_directory, Path.GetFileName(path), token))
        {
            await destination.Content.WriteAsync(Encoding.UTF8.GetBytes(text), token);
            Assert.False(File.Exists(path));
            await destination.CommitAsync(token);
            Assert.Equal(text, await File.ReadAllTextAsync(path, token));
        }
        Assert.Single(Directory.GetFileSystemEntries(_directory));
    }
    [Fact]
    public async Task Existing_and_concurrently_created_destinations_are_never_replaced()
    {
        var token = TestContext.Current.CancellationToken; var path = Path.Combine(_directory, "target");
        await using (var destination = await _files.CreateDestinationAsync(_directory, "target", token))
        {
            await destination.Content.WriteAsync(new byte[] { 1, 2 }, token);
            await File.WriteAllTextAsync(path, "original", token);
            var error = await Assert.ThrowsAsync<TransferException>(() => destination.CommitAsync(token));
            Assert.Equal(TransferError.Conflict, error.Error); Assert.Equal("original", File.ReadAllText(path));
        }
        // Windows checks at commit; Linux also rejects early. Both are fail closed.
        if (OperatingSystem.IsLinux())
            Assert.Equal(TransferError.Conflict, (await Assert.ThrowsAsync<TransferException>(() => _files.CreateDestinationAsync(_directory, "target", token))).Error);
        Assert.Single(Directory.GetFileSystemEntries(_directory));
    }
    [Fact]
    public async Task Cancellation_before_commit_removes_only_owned_temporary_output()
    {
        var token = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(_directory, "keep"), "keep");
        using var cancel = new CancellationTokenSource();
        await using (var destination = await _files.CreateDestinationAsync(_directory, "target", token))
        {
            await destination.Content.WriteAsync(new byte[] { 1 }, token); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destination.CommitAsync(cancel.Token));
        }
        Assert.Equal(new[] { "keep" }, Directory.GetFiles(_directory).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(_directory));
    }
    [Fact]
    public async Task Source_hashes_are_verified_and_in_place_mutation_is_detected()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_directory, "source"); await File.WriteAllTextAsync(path, "initial", token);
        await using var source = await _files.OpenSourceAsync(path, token);
        Assert.Equal(7, source.Length); await source.ValidateAsync(token);
        if (OperatingSystem.IsLinux())
        {
            await File.WriteAllTextAsync(path, "changed", token);
            Assert.Equal(TransferError.SourceChanged, (await Assert.ThrowsAsync<TransferException>(() => source.ValidateAsync(token))).Error);
        }
        else Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "changed"));
    }
    [Fact]
    public async Task Symlink_sources_and_destination_ancestors_are_rejected_and_commit_symlink_race_is_safe()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux no-follow fixture; Windows reparse qualification requires native VM.");
        var token = TestContext.Current.CancellationToken;
        var outside = Path.Combine(_directory, "outside"); Directory.CreateDirectory(outside);
        var original = Path.Combine(outside, "original"); File.WriteAllText(original, "original");
        var link = Path.Combine(_directory, "link"); File.CreateSymbolicLink(link, original);
        await Assert.ThrowsAsync<TransferException>(() => _files.OpenSourceAsync(link, token));
        var directoryLink = Path.Combine(_directory, "directory-link"); Directory.CreateSymbolicLink(directoryLink, outside);
        await Assert.ThrowsAsync<TransferException>(() => _files.CreateDestinationAsync(directoryLink, "target", token));
        await using (var destination = await _files.CreateDestinationAsync(_directory, "race", token))
        {
            File.CreateSymbolicLink(Path.Combine(_directory, "race"), original);
            Assert.Equal(TransferError.Conflict, (await Assert.ThrowsAsync<TransferException>(() => destination.CommitAsync(token))).Error);
        }
        Assert.Equal("original", File.ReadAllText(original));
    }
    [Theory]
    [InlineData("../escape")] [InlineData("folder/../escape")] [InlineData("folder/")]
    [InlineData("C:stream")] [InlineData("\\escape")] [InlineData("CON.txt")]
    [InlineData("LPT¹")] [InlineData("trailing.")] [InlineData("trailing ")] [InlineData("a\u202Eb")]
    public void Unsafe_remote_leaf_names_are_rejected_without_sanitization(string key) =>
        Assert.Throws<TransferException>(() => TransferNames.FromKey(key));
    [Fact]
    public async Task Source_replacement_is_detected_even_with_same_size_and_time()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Windows denies source delete sharing.");
        var path = Path.Combine(_directory, "source"); File.WriteAllText(path, "a"); var time = File.GetLastWriteTimeUtc(path);
        await using var source = await _files.OpenSourceAsync(path, TestContext.Current.CancellationToken);
        File.Move(path, Path.Combine(_directory, "old")); File.WriteAllText(path, "a"); File.SetLastWriteTimeUtc(path, time);
        await Assert.ThrowsAsync<TransferException>(() => source.ValidateAsync(TestContext.Current.CancellationToken));
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
