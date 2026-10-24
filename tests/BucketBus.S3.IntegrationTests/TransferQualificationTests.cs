using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BucketBus.Core;
using BucketBus.Platform;

namespace BucketBus.S3.IntegrationTests;

public sealed class TransferQualificationTests
{
    [Fact, Trait("Category", "LiveS3")]
    public async Task Explicitly_opted_in_single_file_round_trip_and_collision()
    {
        if (Environment.GetEnvironmentVariable("BUCKETBUS_RUN_WRITE_TESTS") != "1")
            Assert.Skip("Live writes SKIPPED: no explicit write opt-in; zero external I/O.");
        var outcomes = new Dictionary<string, string>();
        var owned = new Dictionary<string, string>(); // manifest key -> exact expected full-content SHA-256
        var cleanup = new Dictionary<string, string>();
        var operationIds = new Dictionary<string, string>();
        WriteConfig? config = null;
        AmazonS3Client? fixtures = null;
        FixtureOwner? fixtureOwner = null;
        var local = Path.Combine(Path.GetTempPath(), "bucketbus-live-transfer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BucketBus.slnx"))) repo = repo.Parent;
            if (repo is null) throw new InvalidOperationException();
            config = WriteGate.Load("1", Environment.GetEnvironmentVariable("BUCKETBUS_WRITE_CONFIG"), Path.Combine(repo.FullName, "docs/internal"),
                File.ReadAllText, Environment.GetEnvironmentVariable("BUCKETBUS_APPROVED_ENDPOINT"), Environment.GetEnvironmentVariable("BUCKETBUS_APPROVED_WRITE_SCOPE"))!;
            var settings = config.Connection.Validate();
            var credentials = LiveGate.ReadNamedCredentials(config.Connection.CredentialProfile,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws/credentials"));
            var id = Guid.NewGuid(); var profile = new ConnectionProfile(id, 1, settings, new(id, Guid.NewGuid(), CredentialStorage.Session, settings.TargetFingerprint()));
            Directory.CreateDirectory(local);
            var files = new TransferFiles();
            var bucket = config.Connection.Bucket;
            var run = config.Prefix + Guid.NewGuid().ToString("N") + "/";
            // The qualification-only grant is bounded to the independently approved target and this bucket.
            var grant = new Qualification(settings.TargetFingerprint(), bucket);
            await using (var initializeLogging = S3TransferFactory.Create(profile, credentials, grant, new NeverSend(), TimeSpan.FromSeconds(1))) { }
            var fixtureGuard = new FixtureGuard(settings, bucket, owned, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
            fixtureOwner = new FixtureOwner(fixtureGuard);
            AWSCredentials explicitCredentials = credentials.SessionToken is null ? new BasicAWSCredentials(credentials.AccessKey, credentials.SecretKey)
                : new SessionAWSCredentials(credentials.AccessKey, credentials.SecretKey, credentials.SessionToken);
            fixtures = new ExactKeyS3Client(explicitCredentials, new AmazonS3Config
            {
                ServiceURL = settings.Endpoint, AuthenticationRegion = settings.Region, ForcePathStyle = true,
                MaxErrorRetry = 0, MaxStaleConnectionRetries = 0, AllowAutoRedirect = false, DisableClockSkewCorrection = true,
                DisableLogging = true, LogResponse = false, LogMetrics = false, HttpClientFactory = fixtureOwner,
                IgnoreConfiguredEndpointUrls = true, EndpointDiscoveryEnabled = false, DisableS3ExpressSessionAuth = true
            });
            using var total = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); total.CancelAfter(TimeSpan.FromMinutes(5));
            var token = total.Token;
            try { await fixtures.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }, token); outcomes["Bucket"] = "Existing dedicated bucket"; }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                outcomes["Bucket"] = "Create requested (unknown until acknowledged)"; Save();
                var created = await fixtures.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = false }, token);
                if (created.HttpStatusCode != HttpStatusCode.OK) throw new InvalidOperationException();
                outcomes["Bucket"] = "Created dedicated bucket; retained for subsequent sessions"; Save();
            }
            foreach (var data in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("BucketBus live round trip: é é 漢字 + %\n"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024) })
            {
                var index = owned.Count; var name = $"fixture-{index}-é +%.bin"; var key = run + name;
                var sourcePath = Path.Combine(local, name); await File.WriteAllBytesAsync(sourcePath, data, token);
                owned.Add(key, Convert.ToBase64String(SHA256.HashData(data))); cleanup[key] = "Not yet inspected"; Save();
                await using var source = await files.OpenSourceAsync(sourcePath, token);
                await using (var session = NewSession())
                {
                    var upload = await session.UploadAsync(bucket, key, source, null, token);
                    outcomes["Upload" + index] = upload.State + ": " + upload.Error; Save();
                    if (upload.State != TransferState.Completed) throw new InvalidOperationException();
                }
                var downloadDirectory = Path.Combine(local, "download-" + index); Directory.CreateDirectory(downloadDirectory);
                await using (var destination = await files.CreateDestinationAsync(downloadDirectory, name, token))
                await using (var session = NewSession())
                {
                    var download = await session.DownloadAsync(bucket, key, destination.Content, null, token);
                    outcomes["Download" + index] = download.State + ": " + download.Error;
                    if (download.State != TransferState.Completed) throw new InvalidOperationException();
                    await destination.CommitAsync(token);
                }
                var downloadedBytes = await File.ReadAllBytesAsync(Path.Combine(downloadDirectory, name), token);
                if (!data.SequenceEqual(downloadedBytes)) throw new InvalidOperationException();
                outcomes["RoundTrip" + index] = "PASSED: exact bytes and local SHA-256";
                // Different replacement bytes prove a provider cannot silently ignore the condition.
                var replacementPath = Path.Combine(local, "replacement"); await File.WriteAllTextAsync(replacementPath, "must never replace existing fixture", token);
                await using var replacement = await files.OpenSourceAsync(replacementPath, token);
                await using (var session = NewSession())
                {
                    var collision = await session.UploadAsync(bucket, key, replacement, null, token);
                    outcomes["Collision" + index] = collision.State + ": " + collision.Error; Save();
                    if (collision.State != TransferState.Failed || collision.Error != TransferError.Conflict) throw new InvalidOperationException();
                }
                using (var read = await fixtures.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token))
                    if (Convert.ToBase64String(await SHA256.HashDataAsync(read.ResponseStream, token)) != owned[key]) throw new InvalidOperationException();
                outcomes["UnchangedAfterCollision" + index] = "PASSED";
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await using (var session = NewSession())
                    if ((await session.DownloadAsync(bucket, key, Stream.Null, null, cancelled.Token)).State != TransferState.Cancelled) throw new InvalidOperationException();
                outcomes["PreCancellation" + index] = "PASSED"; Save();
            }
            // In-flight download cancellation using the actual response, without network disruption.
            await using (var session = NewSession())
            {
                using var cancel = new CancellationTokenSource();
                using var output = new CancelAfterWrite(cancel);
                var result = await session.DownloadAsync(bucket, owned.Keys.Last(), output, null, cancel.Token);
                outcomes["DownloadCancellation"] = result.State.ToString();
                if (result.State != TransferState.Cancelled) throw new InvalidOperationException();
            }
            await using (var session = NewSession())
            {
                // This nonexistent key is generated inside the approved scope and never mutated.
                var result = await session.DownloadAsync(bucket, run + "absent", Stream.Null, null, token);
                outcomes["MissingObject"] = result.State.ToString();
                if (result.State != TransferState.Failed) throw new InvalidOperationException();
            }
            var evidence = new[] { new UploadQualificationEvidence(1, settings.TargetFingerprint(), bucket, DateTimeOffset.UtcNow, true, true) };
            await File.WriteAllTextAsync(config.QualificationFile, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), token);
            outcomes["Qualification"] = "PASSED: configured target/bucket only";

            IObjectTransferSession NewSession() => S3TransferFactory.Create(profile, credentials, grant,
                new TransferFixtureGuard(settings, bucket, run, operationIds, Save,
                    new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }), TimeSpan.FromMinutes(2));
        }
        catch (Exception) { outcomes["Run"] = "FAILED: inspect allowlisted private outcomes; raw details suppressed"; }
        finally
        {
            if (config is not null && fixtures is not null)
            {
                foreach (var (key, hash) in owned)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        string etag; string? version;
                        using (var response = await fixtures.GetObjectAsync(new GetObjectRequest { BucketName = config.Connection.Bucket, Key = key }, timeout.Token))
                        {
                            if (!operationIds.TryGetValue(key, out var expectedOperation) || response.Metadata["x-amz-meta-bucketbus-operation"] != expectedOperation
                                || Convert.ToBase64String(await SHA256.HashDataAsync(response.ResponseStream, timeout.Token)) != hash)
                            { cleanup[key] = "RETAINED: identity/content mismatch"; continue; }
                            etag = response.ETag; version = response.VersionId;
                        }
                        cleanup[key] = "Delete requested; outcome unknown until confirmed"; Save();
                        await fixtures.DeleteObjectAsync(new DeleteObjectRequest { BucketName = config.Connection.Bucket, Key = key, IfMatch = etag, VersionId = version }, timeout.Token);
                        try
                        {
                            using var exists = await fixtures.GetObjectAsync(new GetObjectRequest { BucketName = config.Connection.Bucket, Key = key, VersionId = version }, timeout.Token);
                            cleanup[key] = "RETAINED: object still present";
                        }
                        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { cleanup[key] = "CONFIRMED absent"; }
                    }
                    catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { cleanup[key] = "CONFIRMED absent"; }
                    catch { cleanup[key] = "UNKNOWN: cleanup needs review; no retry"; }
                }
                fixtures.Dispose(); fixtureOwner?.Dispose();
            }
            if (config is not null) Save();
            if (Directory.Exists(local)) Directory.Delete(local, true);
        }
        Assert.True(outcomes.GetValueOrDefault("Qualification")?.StartsWith("PASSED") == true && !outcomes.ContainsKey("Run"),
            "Live transfer qualification did not pass. Review private evidence; no endpoint or raw provider data emitted.");
        Assert.True(cleanup.Values.All(v => v == "CONFIRMED absent"), "Owned fixture cleanup requires review; see private manifest.");
        void Save()
        {
            if (config is null) return;
            File.WriteAllText(config.ManifestFile, JsonSerializer.Serialize(new { ExecutedUtc = DateTimeOffset.UtcNow, config.Connection.Bucket, Owned = owned, OperationIds = operationIds, Cleanup = cleanup, Outcomes = outcomes }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private sealed class TransferFixtureGuard(ProfileSettings settings, string bucket, string run,
        Dictionary<string, string> operations, Action save, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!; var path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (uri.Scheme != "https" || uri.Authority != new Uri(settings.Endpoint).Authority
                || !path.StartsWith("/" + bucket + "/" + run, StringComparison.Ordinal)
                || request.Method != HttpMethod.Get && request.Method != HttpMethod.Put)
                throw new InvalidOperationException("Transfer fixture scope rejected.");
            if (request.Method == HttpMethod.Put)
            {
                var key = path[(bucket.Length + 2)..];
                var operation = request.Headers.GetValues("x-amz-meta-bucketbus-operation").Single();
                if (operations.TryAdd(key, operation)) save(); // durable private ownership record before the first send
            }
            return base.SendAsync(request, token);
        }
    }
    private sealed class Qualification(string target, string bucket) : IUploadQualification
    { public bool Allows(ProfileSettings settings, string b) => settings.TargetFingerprint() == target && b == bucket; }
    private sealed class NeverSend : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => throw new InvalidOperationException(); }
    private sealed class CancelAfterWrite(CancellationTokenSource cancel) : MemoryStream
    { public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) { cancel.Cancel(); return ValueTask.CompletedTask; } }
    private sealed class FixtureOwner(HttpMessageHandler guard) : HttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(guard) { Timeout = TimeSpan.FromSeconds(30) };
        public override HttpClient CreateHttpClient(IClientConfig c) => _client;
        public override bool UseSDKHttpClientCaching(IClientConfig c) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig c) => false;
        public void Dispose() => _client.Dispose();
    }
    private sealed class FixtureGuard(ProfileSettings settings, string bucket, Dictionary<string, string> owned, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken token)
        {
            var endpoint = new Uri(settings.Endpoint); var uri = r.RequestUri!; var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var bucketPath = "/" + bucket;
            var bucketOperation = path.TrimEnd('/') == bucketPath && r.Method is { } m && (m == HttpMethod.Head || m == HttpMethod.Put);
            var objectOperation = path.StartsWith(bucketPath + "/", StringComparison.Ordinal) && owned.ContainsKey(path[(bucketPath.Length + 1)..])
                && (r.Method == HttpMethod.Get || r.Method == HttpMethod.Delete);
            if (uri.Scheme != "https" || uri.Authority != endpoint.Authority || (!bucketOperation && !objectOperation))
                throw new InvalidOperationException("Fixture scope rejected.");
            return base.SendAsync(r, token);
        }
    }
}
