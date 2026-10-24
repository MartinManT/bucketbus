using System.Net;
using BucketBus.Core;

namespace BucketBus.S3.Tests;

public sealed class ListingProtocolTests
{
    [Theory]
    [InlineData(AddressingStyle.Path, "storage.example.test", "/test-bucket")]
    [InlineData(AddressingStyle.VirtualHosted, "test-bucket.storage.example.test", "/")]
    public async Task Explicit_endpoint_region_addressing_and_session_credentials_reach_real_sdk_signer(AddressingStyle style, string host, string path)
    {
        await using var fixture = new SdkFixture((request, _) =>
        {
            Assert.Equal(host, request.RequestUri!.Host); Assert.Equal(9443, request.RequestUri.Port);
            Assert.Equal(path, request.RequestUri.AbsolutePath.TrimEnd('/') is "" ? "/" : request.RequestUri.AbsolutePath.TrimEnd('/'));
            var authorization = request.Headers.GetValues("Authorization").Single();
            Assert.True(authorization.Contains("Credential=SYNTHETICKEY/", StringComparison.Ordinal));
            Assert.True(authorization.Contains("/test-region-1/s3/aws4_request", StringComparison.Ordinal));
            Assert.True(request.Headers.GetValues("x-amz-security-token").Single() == "synthetic-token");
            var query = SdkFixture.Query(request.RequestUri);
            Assert.Equal("2", query["list-type"]); Assert.Equal("1000", query["max-keys"]);
            Assert.Equal("url", query["encoding-type"]); Assert.Equal("/", query["delimiter"]);
            return Task.FromResult(SdkFixture.Xml(SdkFixture.Objects()));
        }, style, temporary: true);
        Assert.True((await fixture.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(1, fixture.Transport.Attempts);
    }

    [Fact]
    public async Task Keys_are_decoded_exactly_once_and_objects_remain_distinct_from_prefixes()
    {
        string[] keys = ["é", "e\u0301", "a b", "a+b", "a%2Fb", "a//b", "/leading", "dir/", "a\tb", "../dot", "back\\slash"];
        var body = string.Concat(keys.Select(k => SdkFixture.Item(k)))
            + "<CommonPrefixes>" + SdkFixture.Element("Prefix", "dir%2F") + "</CommonPrefixes>";
        await using var fixture = new SdkFixture((_, _) => Task.FromResult(SdkFixture.Xml(SdkFixture.Objects(body))));
        var result = await fixture.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StorageError.None, result.Error);
        Assert.Equal(keys, result.Value!.Entries.OfType<ObjectEntry>().Select(o => o.Key));
        Assert.Equal("dir/", Assert.Single(result.Value.Entries.OfType<PrefixEntry>()).Key);
        Assert.Equal(1, fixture.Transport.Attempts); // Listing performs no per-object HEAD calls.
    }
    [Fact]
    public async Task Unknown_size_and_time_are_not_invented_zeroes_and_unencoded_percent_keys_are_preserved()
    {
        var body = SdkFixture.Objects(SdkFixture.Item("unknown") + SdkFixture.Item("zero", "<Size>0</Size><LastModified>2026-01-01T00:00:00Z</LastModified>"));
        await using var fixture = new SdkFixture((_, _) => Task.FromResult(SdkFixture.Xml(body)));
        var entries = (await fixture.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).Value!.Entries.Cast<ObjectEntry>().ToArray();
        Assert.Null(entries[0].Size); Assert.Null(entries[0].Modified); Assert.Equal(0, entries[1].Size); Assert.NotNull(entries[1].Modified);
        await using var raw = new SdkFixture((_, _) => Task.FromResult(SdkFixture.Xml(SdkFixture.Objects("<Contents><Key>a%2Fb+</Key></Contents>").Replace("<EncodingType>url</EncodingType>", ""))));
        Assert.Equal("a%2Fb+", Assert.Single((await raw.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).Value!.Entries).Key);
    }
    [Fact]
    public async Task Bucket_discovery_denial_does_not_prevent_direct_bucket_listing()
    {
        await using var fixture = new SdkFixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/"
            ? SdkFixture.Xml(SdkFixture.Error("AccessDenied"), HttpStatusCode.Forbidden) : SdkFixture.Xml(SdkFixture.Objects())));
        var denied = await fixture.Session.ListBuckets().NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StorageError.AccessDenied, denied.Error);
        Assert.DoesNotContain("synthetic-sensitive", denied.ToString());
        Assert.True((await fixture.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(2, fixture.Transport.Attempts);
    }
    [Fact]
    public async Task Opaque_tokens_and_exact_prefix_pass_through_sdk_without_decoding()
    {
        const string token = "opaque/+%2F= token"; const string prefix = "e\u0301/+%2F/";
        var count = 0;
        await using var fixture = new SdkFixture((request, _) =>
        {
            var query = SdkFixture.Query(request.RequestUri!); Assert.Equal(prefix, query["prefix"]);
            if (++count == 2) Assert.Equal(token, query["continuation-token"]);
            return Task.FromResult(SdkFixture.Xml(SdkFixture.Objects(truncated: count == 1, next: count == 1 ? token : null, prefix: prefix)));
        });
        var listing = fixture.Session.ListObjects("test-bucket", prefix);
        Assert.False((await listing.NextAsync(TestContext.Current.CancellationToken)).Value!.IsComplete);
        Assert.True((await listing.NextAsync(TestContext.Current.CancellationToken)).Value!.IsComplete);
        Assert.True((await listing.NextAsync(TestContext.Current.CancellationToken)).Value!.IsComplete);
        Assert.Equal(2, fixture.Transport.Attempts);
    }
    [Fact]
    public async Task Repeated_token_cycle_and_page_budget_stop_traversal()
    {
        var i = 0;
        await using var cycle = new SdkFixture((_, _) => Task.FromResult(SdkFixture.Xml(SdkFixture.Objects(truncated: true, next: ++i % 2 == 1 ? "A" : "B"))));
        var listing = cycle.Session.ListObjects("test-bucket");
        Assert.True((await listing.NextAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await listing.NextAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(StorageError.ProtocolError, (await listing.NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Equal(StorageError.ProtocolError, (await listing.NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Equal(3, cycle.Transport.Attempts);
        await using var bounded = new SdkFixture((_, _) => Task.FromResult(SdkFixture.Xml(SdkFixture.Objects(truncated: true, next: Guid.NewGuid().ToString()))), pageLimit: 2);
        listing = bounded.Session.ListObjects("test-bucket");
        await listing.NextAsync(TestContext.Current.CancellationToken); await listing.NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StorageError.PaginationLimit, (await listing.NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Equal(2, bounded.Transport.Attempts);
    }
    [Fact]
    public async Task ListBuckets_uses_independent_pagination_and_nullable_creation_dates()
    {
        var i = 0;
        await using var fixture = new SdkFixture((request, _) =>
        {
            Assert.Equal("1000", SdkFixture.Query(request.RequestUri!)["max-buckets"]);
            if (++i == 2) Assert.Equal("opaque+%token", SdkFixture.Query(request.RequestUri!)["continuation-token"]);
            return Task.FromResult(SdkFixture.Xml("<ListAllMyBucketsResult><Buckets><Bucket><Name>test-bucket</Name></Bucket></Buckets>"
                + (i == 1 ? "<ContinuationToken>opaque+%token</ContinuationToken>" : "") + "</ListAllMyBucketsResult>"));
        });
        var listing = fixture.Session.ListBuckets();
        var first = await listing.NextAsync(TestContext.Current.CancellationToken);
        Assert.Null(Assert.Single(first.Value!.Entries).Created); Assert.False(first.Value.IsComplete);
        Assert.True((await listing.NextAsync(TestContext.Current.CancellationToken)).Value!.IsComplete);
    }
    [Theory]
    [InlineData("<ListBucketResult><Name>wrong</Name><Name>test-bucket</Name><IsTruncated>false</IsTruncated></ListBucketResult>")]
    [InlineData("<ListBucketResult><Name>test-bucket</Name><IsTruncated>true</IsTruncated><NextContinuationToken> </NextContinuationToken></ListBucketResult>")]
    [InlineData("<ListBucketResult><Name>test-bucket</Name><IsTruncated>false</IsTruncated><NextContinuationToken>unexpected</NextContinuationToken></ListBucketResult>")]
    [InlineData("<Unexpected />")]
    [InlineData("<ListBucketResult>")]
    [InlineData("<ListBucketResult><Name>test-bucket</Name></ListBucketResult>")]
    [InlineData("<ListBucketResult><IsTruncated>false</IsTruncated></ListBucketResult>")]
    [InlineData("<ListBucketResult><Name>test-bucket</Name><IsTruncated>true</IsTruncated></ListBucketResult>")]
    [InlineData("<ListBucketResult><Name>test-bucket</Name><IsTruncated>false</IsTruncated><Contents><Size>1</Size></Contents></ListBucketResult>")]
    [InlineData("<ListBucketResult><Name>test-bucket</Name><IsTruncated>false</IsTruncated><EncodingType>url</EncodingType><Contents><Key>bad%ZZ</Key></Contents></ListBucketResult>")]
    public async Task Malformed_or_incomplete_success_responses_fail_closed(string xml)
    {
        await using var fixture = new SdkFixture((_, _) => Task.FromResult(SdkFixture.Xml(xml)));
        var result = await fixture.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StorageError.ProtocolError, result.Error); Assert.Null(result.Value);
    }
    [Fact]
    public async Task Oversized_pages_and_bodies_are_rejected_without_publishing_partial_results()
    {
        await using var largePage = new SdkFixture((_, _) => Task.FromResult(SdkFixture.Xml(SdkFixture.Objects(
            string.Concat(Enumerable.Range(0, 1001).Select(i => SdkFixture.Item("key-" + i)))))));
        Assert.Equal(StorageError.ProtocolError, (await largePage.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).Error);
        await using var largeBody = new SdkFixture((_, _) =>
        {
            var response = SdkFixture.Xml(SdkFixture.Objects());
            response.Content.Headers.ContentLength = 8 * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        Assert.Equal(StorageError.ProtocolError, (await largeBody.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).Error);
    }
}
