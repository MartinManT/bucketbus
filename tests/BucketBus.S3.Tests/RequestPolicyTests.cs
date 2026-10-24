using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BucketBus.Core;

namespace BucketBus.S3.Tests;

public sealed class RequestPolicyTests
{
    [Theory]
    [InlineData("é/e\u0301 a+%2F//b/")]
    [InlineData("../dot/./key")]
    [InlineData("/leading//back\\slash")]
    [InlineData("//two-leading/slashes")]
    [InlineData("/")]
    [InlineData("tab\tline\nend")]
    public async Task Explicit_HEAD_preserves_exact_key_through_request_serialization(string key)
    {
        string? actualPath = null;
        var signatureMatches = false;
        await using var fixture = new SdkFixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            actualPath = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            signatureMatches = SignatureVerifier.Matches(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content.Headers.ContentLength = 0;
            response.Headers.ETag = new("\"opaque-etag\"");
            response.Headers.TryAddWithoutValidation("x-amz-meta-description", "synthetic metadata");
            response.Headers.TryAddWithoutValidation("x-amz-version-id", "opaque-version");
            response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            return Task.FromResult(response);
        });
        var result = await fixture.Session.GetMetadataAsync("test-bucket", key, TestContext.Current.CancellationToken);
        Assert.Equal("/test-bucket/" + key, actualPath);
        Assert.True(signatureMatches, "The outgoing URI and headers must match the SDK signature.");
        Assert.Equal(StorageError.None, result.Error); Assert.Equal(key, result.Value!.Key);
        Assert.Equal(0, result.Value.Size); Assert.NotNull(result.Value.Modified);
        Assert.Equal("\"opaque-etag\"", result.Value.ETag); Assert.Equal(1, fixture.Transport.Attempts);
        Assert.Equal("synthetic metadata", Assert.Single(result.Value.UserMetadata).Value);
        Assert.Equal("opaque-version", result.Value.VersionId);
    }
    [Fact]
    public async Task Virtual_hosted_HEAD_preserves_leading_slashes_and_signature_on_retry()
    {
        var attempts = 0; var exact = true; var signature = true;
        await using var fixture = new SdkFixture((request, _) =>
        {
            exact &= request.RequestUri!.Host == "test-bucket.storage.example.test" && Uri.UnescapeDataString(request.RequestUri.AbsolutePath) == "///key";
            signature &= SignatureVerifier.Matches(request);
            return Task.FromResult(++attempts == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK));
        }, AddressingStyle.VirtualHosted);
        var result = await fixture.Session.GetMetadataAsync("test-bucket", "//key", TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess); Assert.True(exact); Assert.True(signature); Assert.Equal(2, fixture.Transport.Attempts);
    }
    [Fact]
    public async Task Leading_slash_path_style_fix_is_idempotent_across_SDK_retries()
    {
        var attempts = 0; var exact = true; var signature = true;
        await using var fixture = new SdkFixture((request, _) =>
        {
            exact &= Uri.UnescapeDataString(request.RequestUri!.AbsolutePath) == "/test-bucket///key";
            signature &= SignatureVerifier.Matches(request);
            return Task.FromResult(++attempts == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK));
        });
        var result = await fixture.Session.GetMetadataAsync("test-bucket", "//key", TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess); Assert.True(exact); Assert.True(signature); Assert.Equal(2, fixture.Transport.Attempts);
    }
    [Fact]
    public async Task Dotted_virtual_host_buckets_and_ARNs_fail_before_network_without_switching_addressing()
    {
        await using var fixture = new SdkFixture((_, _) => throw new InvalidOperationException("must not send"), AddressingStyle.VirtualHosted);
        foreach (var bucket in new[] { "dotted.bucket", "arn:aws:s3:region:account:accesspoint/name" })
            Assert.Equal(StorageError.InvalidRequest, (await fixture.Session.ListObjects(bucket).NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Equal(0, fixture.Transport.Attempts);
    }
    [Fact]
    public async Task Absent_HEAD_length_and_timestamp_stay_unknown()
    {
        await using var fixture = new SdkFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var result = await fixture.Session.GetMetadataAsync("test-bucket", "key", TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess); Assert.Null(result.Value!.Size); Assert.Null(result.Value.Modified);
    }
    [Theory]
    [InlineData(400, "AuthorizationHeaderMalformed", StorageError.AuthenticationFailed)]
    [InlineData(403, "AccessDenied", StorageError.AccessDenied)]
    [InlineData(404, "NoSuchBucket", StorageError.NotFound)]
    [InlineData(403, "InvalidAccessKeyId", StorageError.AuthenticationFailed)]
    [InlineData(403, "SignatureDoesNotMatch", StorageError.AuthenticationFailed)]
    [InlineData(403, "ExpiredToken", StorageError.AuthenticationFailed)]
    public async Task Errors_are_classified_without_leaking_raw_provider_messages(int status, string code, StorageError expected)
    {
        await using var fixture = new SdkFixture((_, _) => Task.FromResult(SdkFixture.Xml(SdkFixture.Error(code), (HttpStatusCode)status)));
        var result = await fixture.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Error); Assert.Null(result.Value);
        Assert.DoesNotContain("synthetic", result.ToString()); Assert.Equal(1, fixture.Transport.Attempts);
    }
    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(307)] [InlineData(308)]
    public async Task Redirects_are_rejected_before_any_second_authenticated_send(int code)
    {
        await using var fixture = new SdkFixture((request, _) =>
        {
            Assert.Equal("storage.example.test", request.RequestUri!.Host);
            var response = SdkFixture.Xml(SdkFixture.Error("PermanentRedirect"), (HttpStatusCode)code);
            response.Headers.Location = new("https://unrelated.example.test/credential-capture");
            response.Headers.TryAddWithoutValidation("x-amz-bucket-region", "other-region-1");
            return Task.FromResult(response);
        });
        Assert.Equal(StorageError.RedirectRejected, (await fixture.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Equal(1, fixture.Transport.Attempts);
    }
    [Fact]
    public async Task Transport_failures_remain_distinct_from_protocol_errors_and_do_not_escape_attempt_budget()
    {
        await using var fixture = new SdkFixture((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("synthetic-sensitive-detail", new IOException("synthetic-detail"))));
        var result = await fixture.Session.ListBuckets().NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StorageError.TransportFailure, result.Error);
        Assert.InRange(fixture.Transport.Attempts, 1, 3);
        Assert.DoesNotContain("synthetic", result.ToString());
    }
    [Fact]
    public async Task Standard_SDK_retries_are_at_most_two_and_recovery_does_not_add_outer_retries()
    {
        var attempts = 0;
        await using var recover = new SdkFixture((_, _) => Task.FromResult(++attempts < 3
            ? SdkFixture.Xml(SdkFixture.Error("ServiceUnavailable"), HttpStatusCode.ServiceUnavailable)
            : SdkFixture.Xml(SdkFixture.Objects())));
        Assert.True((await recover.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(3, recover.Transport.Attempts);
        await using var exhaust = new SdkFixture((_, _) => Task.FromResult(SdkFixture.Xml(SdkFixture.Error("ServiceUnavailable"), HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(StorageError.ServiceFailure, (await exhaust.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Equal(3, exhaust.Transport.Attempts);
    }
    [Fact]
    public async Task Cancellation_interrupts_an_inflight_request_and_pending_retries()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = new SdkFixture(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return SdkFixture.Xml(""); });
        var read = slow.Session.ListObjects("test-bucket").NextAsync(cancel.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken); cancel.Cancel();
        Assert.Equal(StorageError.Cancelled, (await read.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).Error);
        Assert.Equal(1, slow.Transport.Attempts);

        using var retryCancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var retry = new SdkFixture(async (_, ct) =>
        {
            if (++attempts == 1) { failed.TrySetResult(); return SdkFixture.Xml(SdkFixture.Error("ServiceUnavailable"), HttpStatusCode.ServiceUnavailable); }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); return SdkFixture.Xml("");
        });
        read = retry.Session.ListObjects("test-bucket").NextAsync(retryCancel.Token);
        await failed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(25, TestContext.Current.CancellationToken);
        retryCancel.Cancel();
        Assert.Equal(StorageError.Cancelled, (await read.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).Error);
        Assert.InRange(retry.Transport.Attempts, 1, 2);
    }
    [Fact]
    public async Task Total_deadline_includes_body_reads_and_has_distinct_timeout_status()
    {
        await using var fixture = new SdkFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new StalledStream()) }), timeout: TimeSpan.FromMilliseconds(80));
        var timer = Stopwatch.StartNew();
        var result = await fixture.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StorageError.Timeout, result.Error); Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Transport.Attempts);
    }
    [Fact]
    public async Task Disposal_cancels_inflight_and_all_later_operations_without_another_send()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new SdkFixture(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return SdkFixture.Xml(""); });
        var read = fixture.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fixture.Session.DisposeAsync();
        Assert.Equal(StorageError.SessionExpired, (await read).Error);
        Assert.True(fixture.Transport.Disposed);
        Assert.Equal(StorageError.SessionExpired, (await fixture.Session.GetMetadataAsync("test-bucket", "key", TestContext.Current.CancellationToken)).Error);
        Assert.Equal(1, fixture.Transport.Attempts);
    }
    [Fact]
    public async Task Stale_authority_blocks_requests_and_discards_late_responses()
    {
        await using var stale = new SdkFixture((_, _) => throw new InvalidOperationException("must not send"), current: _ => Task.FromResult(false));
        Assert.Equal(StorageError.SessionExpired, (await stale.Session.ListBuckets().NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.Equal(0, stale.Transport.Attempts);
        var current = true;
        await using var late = new SdkFixture((_, _) => { current = false; return Task.FromResult(SdkFixture.Xml(SdkFixture.Objects())); }, current: _ => Task.FromResult(current));
        Assert.Equal(StorageError.SessionExpired, (await late.Session.ListObjects("test-bucket").NextAsync(TestContext.Current.CancellationToken)).Error);
        Assert.True(late.Session.IsInvalidated);
    }
    [Fact]
    public async Task Actual_TLS_handshake_rejects_untrusted_certificate_before_HTTP_credentials()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var receivedHttp = false;
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                    using var tls = new SslStream(socket.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop.Token);
                    receivedHttp |= await tls.ReadAsync(new byte[1], stop.Token) != 0;
                }
                catch (Exception e) when (e is AuthenticationException or IOException or OperationCanceledException) { }
            }
        }, stop.Token);
        var settings = new ProfileSettings("TLS fixture", $"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}", "test-region-1", AddressingStyle.Path);
        var id = Guid.NewGuid();
        var profile = new ConnectionProfile(id, 1, settings, new(id, Guid.NewGuid(), CredentialStorage.Session, settings.TargetFingerprint()));
        await using var session = S3ReadOnlySessionFactory.Create(profile, new("SYNTHETICKEY", "synthetic-secret"), _ => Task.FromResult(true),
            new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, TimeSpan.FromSeconds(10));
        try
        {
            var result = await session.ListBuckets().NextAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StorageError.TlsFailure, result.Error); Assert.False(receivedHttp);
        }
        finally { stop.Cancel(); listener.Stop(); await server; }
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
