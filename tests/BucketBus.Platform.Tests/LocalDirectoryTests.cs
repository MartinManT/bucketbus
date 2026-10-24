using BucketBus.Core;

namespace BucketBus.Platform.Tests;

public sealed class LocalDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bucketbus-local-" + Guid.NewGuid().ToString("N"));
    public LocalDirectoryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    [Fact]
    public async Task Enumeration_is_single_level_read_only_and_preserves_unicode_and_hidden_names()
    {
        var names = new[] { "é.txt", "e\u0301.txt", ".hidden", "文件.txt" };
        foreach (var name in names) await File.WriteAllTextAsync(Path.Combine(_root, name), "fixture", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(_root, "folder"));
        await File.WriteAllTextAsync(Path.Combine(_root, "folder", "not-recursive"), "nested", TestContext.Current.CancellationToken);
        var before = Directory.GetFiles(_root).ToDictionary(p => p, File.ReadAllBytes);
        var listing = await new LocalDirectoryBrowser().ReadAsync(_root, TestContext.Current.CancellationToken);
        Assert.Equal(LocalBrowseError.None, listing.Error); Assert.False(listing.IsPartial);
        Assert.Equal(5, listing.Entries.Count);
        Assert.All(names, name => Assert.Contains(listing.Entries, e => e.Name == name && e.FullPath == Path.Combine(_root, name)
            && e.Kind == LocalEntryKind.File && e.Size == 7 && e.Modified is not null));
        var directory = Assert.Single(listing.Entries, e => e.Name == "folder");
        Assert.True(directory.CanEnter); Assert.Equal(LocalEntryKind.Directory, directory.Kind); Assert.Null(directory.Size);
        Assert.DoesNotContain(listing.Entries, e => e.Name == "not-recursive");
        foreach (var file in before) Assert.Equal(file.Value, await File.ReadAllBytesAsync(file.Key, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Empty_missing_file_as_directory_and_invalid_path_are_distinct()
    {
        var browser = new LocalDirectoryBrowser();
        var empty = await browser.ReadAsync(_root, TestContext.Current.CancellationToken);
        Assert.Empty(empty.Entries); Assert.Equal(LocalBrowseError.None, empty.Error);
        Assert.Equal(LocalBrowseError.NotFound, (await browser.ReadAsync(Path.Combine(_root, "gone"), TestContext.Current.CancellationToken)).Error);
        var file = Path.Combine(_root, "ordinary-file"); await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);
        Assert.NotEqual(LocalBrowseError.None, (await browser.ReadAsync(file, TestContext.Current.CancellationToken)).Error);
        foreach (var invalid in new[] { "", "relative/path", Path.Combine(_root, ".."), "\0" })
            Assert.Equal(LocalBrowseError.InvalidPath, (await browser.ReadAsync(invalid, TestContext.Current.CancellationToken)).Error);
    }
    [Fact]
    public async Task Linux_links_are_labelled_and_only_explicit_navigation_follows_directory_link()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Native link fixture requires Linux; Windows symlink privileges remain a manual gate.");
        var target = Path.Combine(_root, "target"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "child"), "x", TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(Path.Combine(_root, "directory-link"), target);
        File.CreateSymbolicLink(Path.Combine(_root, "file-link"), Path.Combine(target, "child"));
        File.CreateSymbolicLink(Path.Combine(_root, "broken-link"), Path.Combine(_root, "missing"));
        var browser = new LocalDirectoryBrowser();
        var result = await browser.ReadAsync(_root, TestContext.Current.CancellationToken);
        foreach (var name in new[] { "directory-link", "file-link", "broken-link" })
            Assert.Equal(LocalEntryKind.SymbolicLink, Assert.Single(result.Entries, e => e.Name == name).Kind);
        var link = Assert.Single(result.Entries, e => e.Name == "directory-link");
        Assert.True(link.CanEnter); Assert.Null(link.Size);
        Assert.Equal("child", Assert.Single((await browser.ReadAsync(link.FullPath, TestContext.Current.CancellationToken)).Entries).Name);
        Assert.False(Assert.Single(result.Entries, e => e.Name == "broken-link").CanEnter);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Access_denial_and_disconnection_do_not_turn_into_empty_success(bool partlyRead)
    {
        var file = Path.Combine(_root, "visible"); await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);
        IEnumerable<FileSystemInfo> Failing(string _)
        {
            if (partlyRead) yield return new FileInfo(file);
            throw new UnauthorizedAccessException("synthetic private path");
        }
        var result = await new LocalDirectoryBrowser(Failing).ReadAsync(_root, TestContext.Current.CancellationToken);
        Assert.Equal(LocalBrowseError.AccessDenied, result.Error); Assert.Equal(partlyRead, result.IsPartial);
        Assert.Equal(partlyRead ? 1 : 0, result.Entries.Count);
    }
    [Fact]
    public async Task Disappearing_entry_keeps_its_exact_identity_with_unavailable_metadata()
    {
        var browser = new LocalDirectoryBrowser(_ => [new FileInfo(Path.Combine(_root, "gone"))]);
        var entry = Assert.Single((await browser.ReadAsync(_root, TestContext.Current.CancellationToken)).Entries);
        Assert.Equal("gone", entry.Name); Assert.Equal(LocalEntryKind.Unavailable, entry.Kind); Assert.False(entry.MetadataAvailable);
    }
    [Fact]
    public async Task Large_listing_stops_after_one_bounded_subset_without_materializing_the_directory()
    {
        var file = Path.Combine(_root, "entry"); await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);
        var yielded = 0;
        IEnumerable<FileSystemInfo> Many(string _)
        {
            for (var i = 0; i < 1_000_000; i++) { yielded++; yield return new FileInfo(file); }
        }
        var result = await new LocalDirectoryBrowser(Many).ReadAsync(_root, TestContext.Current.CancellationToken);
        Assert.Equal(LocalBrowsingLimits.MaximumEntries, result.Entries.Count); Assert.True(result.IsPartial);
        Assert.Equal(LocalBrowsingLimits.MaximumEntries + 1, yielded);
    }
    [Fact]
    public async Task Cancelled_slow_os_call_returns_promptly_without_starting_unbounded_workers()
    {
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var workers = 0;
        IEnumerable<FileSystemInfo> Slow(string _)
        {
            Interlocked.Increment(ref workers); started.Set(); release.Wait(TimeSpan.FromSeconds(5)); yield break;
        }
        var browser = new LocalDirectoryBrowser(Slow);
        using var first = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var pending = browser.ReadAsync(_root, first.Token);
            Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken));
            first.Cancel();
            Assert.Equal(LocalBrowseError.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)).Error);
            using var queued = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var second = browser.ReadAsync(_root, queued.Token); queued.Cancel();
            Assert.Equal(LocalBrowseError.Cancelled, (await second).Error); Assert.Equal(1, workers);
        }
        finally { release.Set(); }
        Assert.Equal(LocalBrowseError.None, (await browser.ReadAsync(_root, TestContext.Current.CancellationToken)).Error);
    }
}
