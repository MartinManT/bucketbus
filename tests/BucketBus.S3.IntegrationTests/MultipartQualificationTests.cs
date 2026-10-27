using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BucketBus.Core;
using BucketBus.Platform;
using MultipartUpload = BucketBus.Core.MultipartUpload;

namespace BucketBus.S3.IntegrationTests;

internal static class MultipartGate
{
    // Separate action opt-in and scope approval. Older write-test opt-ins grant no multipart access.
    public static WriteConfig? Load(string? optIn, string? path, string root, Func<string, string> read, string? endpoint, string? scope) =>
        WriteGate.Load(optIn, path, root, read, endpoint, scope);
}

public sealed class MultipartQualificationTests
{
    [Fact, Trait("Category", "LiveS3")]
    public async Task Independently_authorized_multipart_qualification()
    {
        if (Environment.GetEnvironmentVariable("BUCKETBUS_RUN_MULTIPART_TESTS") != "1")
            Assert.Skip("Multipart live tests disabled; zero external I/O.");
        var outcomes = new Dictionary<string, string>(); var jobs = new Dictionary<string, TransferRecord>();
        var finalObjects = new Dictionary<string, (Guid Operation, string Hash)>(); var keys = new HashSet<string>();
        var uploadIds = new Dictionary<string, string>(); var cleanup = new Dictionary<string, string>();
        var local = Path.Combine(Path.GetTempPath(), "bucketbus-multipart-live-" + Guid.NewGuid().ToString("N"));
        WriteConfig? config = null; ConnectionProfile? profile = null; Credentials? credentials = null;
        try
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BucketBus.slnx"))) repo = repo.Parent;
            if (repo is null) throw new InvalidOperationException();
            config = MultipartGate.Load("1", Environment.GetEnvironmentVariable("BUCKETBUS_MULTIPART_CONFIG"), Path.Combine(repo.FullName, "docs/internal"), File.ReadAllText,
                Environment.GetEnvironmentVariable("BUCKETBUS_APPROVED_ENDPOINT"), Environment.GetEnvironmentVariable("BUCKETBUS_APPROVED_MULTIPART_SCOPE"))!;
            // Configuration and independent approval succeed BEFORE credentials or network construction.
            credentials = LiveGate.ReadNamedCredentials(config.Connection.CredentialProfile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws/credentials"));
            var settings = config.Connection.Validate(); var id = Guid.NewGuid();
            profile = new(id, 1, settings, new(id, Guid.NewGuid(), CredentialStorage.Session, settings.TargetFingerprint()));
            Directory.CreateDirectory(local);
            var path = Path.Combine(local, "multipart.bin");
            // One 16 MiB + 17 byte fixture, generated with a bounded buffer; no multi-GiB physical files.
            await using (var file = File.Create(path))
            {
                var buffer = RandomNumberGenerator.GetBytes(65536);
                for (var n = 0; n < 256; n++) await file.WriteAsync(buffer, TestContext.Current.CancellationToken);
                await file.WriteAsync(buffer.AsMemory(0, 17), TestContext.Current.CancellationToken);
            }
            var small = Path.Combine(local, "sentinel"); await File.WriteAllTextAsync(small, "independent sentinel must stay unchanged", TestContext.Current.CancellationToken);
            var files = new TransferFiles(); var run = config.Prefix + Guid.NewGuid().ToString("N") + "/";
            using var total = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); total.CancelAfter(TimeSpan.FromMinutes(15));
            var token = total.Token;
            await Sentinel(run + "unrelated", token);
            foreach (var mode in new[] { "complete", "existing", "concurrent", "resume", "abort" })
            {
                var key = run + mode + "-é-é-+%.bin"; keys.Add(key);
                if (mode == "existing") await Sentinel(key, token);
                await using var source = await files.OpenSourceAsync(path, token);
                var record = new TransferRecord(Guid.NewGuid(), 0, profile.Id, profile.Revision, settings.TargetFingerprint(), settings.Endpoint,
                    config.Connection.Bucket, key, path, true, TransferState.Running, Total: source.Length, Sha256: source.Sha256, SourceIdentity: source.SourceIdentity,
                    CleanupRequired: true, Multipart: new(new(MultipartPlan.MinimumPart, 4), MultipartPhase.Creating));
                jobs[key] = record; Save();
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                async Task Persist(MultipartUpload state)
                {
                    jobs[key] = jobs[key] with { Multipart = state, Revision = jobs[key].Revision + 1 }; if (state.UploadId is { } owned) uploadIds[owned] = key; Save();
                    if (mode == "concurrent" && state.Phase == MultipartPhase.Completing) await Sentinel(key, token);
                    if (mode is "resume" or "abort" && state.Phase == MultipartPhase.Uploading && state.Confirmed.Length == 1) stop.Cancel();
                }
                TransferResult result;
                await using (var session = Session()) result = await session.UploadMultipartAsync(record, source, Persist, null, stop.Token);
                jobs[key] = jobs[key] with { State = result.State, Error = result.Error, CleanupRequired = result.CleanupRequired };
                outcomes[mode] = result.State + ": " + result.Error; Save();
                if (mode == "resume")
                {
                    if (result.State != TransferState.Interrupted || jobs[key].Multipart?.CanResume != true) throw new InvalidOperationException();
                    await using var resumed = Session();
                    result = await resumed.UploadMultipartAsync(jobs[key], source, state => PersistOnly(key, state), null, token, resume: true);
                    jobs[key] = jobs[key] with { State = result.State, Error = result.Error, CleanupRequired = result.CleanupRequired };
                    outcomes[mode] = result.State + ": " + result.Error;
                }
                if (mode is "complete" or "resume")
                {
                    if (result.State != TransferState.Completed) throw new InvalidOperationException();
                    finalObjects[key] = (record.Id, source.Sha256); cleanup[key] = "Owned verified object retained until cleanup";
                }
                else if (mode is "existing" or "concurrent")
                {
                    if (result.State != TransferState.Failed || result.Error != TransferError.Conflict) throw new InvalidOperationException();
                    await VerifySentinel(key, token);
                }
                if (mode is "existing" or "concurrent" or "abort")
                {
                    var owned = jobs[key];
                    await using var aborter = Session(); var aborted = await aborter.AbortMultipartAsync(owned, state => PersistOnly(key, state), token);
                    jobs[key] = jobs[key] with { State = aborted.State, Error = aborted.Error, CleanupRequired = aborted.CleanupRequired };
                    cleanup[key + ":multipart"] = aborted.State + ": " + aborted.Error; Save();
                    if (aborted.State != TransferState.Cancelled || aborted.CleanupRequired) throw new InvalidOperationException();
                }
                Save();
            }
            await VerifySentinel(run + "unrelated", token); outcomes["unrelated"] = "Unchanged generated sentinel; pre-existing unrelated objects never addressed";
            outcomes["qualification"] = "PASSED"; Save();

            async Task Sentinel(string key, CancellationToken ct)
            {
                keys.Add(key); await using var source = await files.OpenSourceAsync(small, ct);
                var operation = Guid.NewGuid(); cleanup[key] = "Sentinel PUT intent; ownership unknown until verified"; Save();
                await using var session = Session(); var result = await session.UploadAsync(config.Connection.Bucket, key, source, null, ct, operation);
                if (result.State != TransferState.Completed) throw new InvalidOperationException();
                finalObjects[key] = (operation, source.Sha256); Save();
            }
            async Task VerifySentinel(string key, CancellationToken ct)
            {
                var owned = finalObjects[key];
                var evidence = new TransferRecord(owned.Operation, 0, profile.Id, profile.Revision, settings.TargetFingerprint(), settings.Endpoint,
                    config.Connection.Bucket, key, small, true, TransferState.OutcomeUnknown,
                    Total: new FileInfo(small).Length, Sha256: owned.Hash);
                await using var session = Session();
                if ((await session.ReconcileUploadAsync(evidence, ct)).State != TransferState.Completed) throw new InvalidOperationException();
            }
        }
        catch { outcomes["failure"] = "FAILED or uncertain; inspect private manifest. No automatic replay."; }
        finally
        {
            if (config is not null && profile is not null && credentials is not null)
            {
                // No implicit abort on test failure: unknown creation/part/completion state remains explicit evidence.
                // Delete only positively owned final fixtures after another complete identity/hash check.
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                using var owner = new LabHttpOwner(new MultipartLabGuard(profile.Settings, config.Connection.Bucket, keys, uploadIds,
                    new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }));
                AWSCredentials auth = credentials.SessionToken is null ? new BasicAWSCredentials(credentials.AccessKey, credentials.SecretKey)
                    : new SessionAWSCredentials(credentials.AccessKey, credentials.SecretKey, credentials.SessionToken);
                using var client = new ExactKeyS3Client(auth, new AmazonS3Config { ServiceURL = profile.Settings.Endpoint, AuthenticationRegion = profile.Settings.Region,
                    ForcePathStyle = true, MaxErrorRetry = 0, MaxStaleConnectionRetries = 0, AllowAutoRedirect = false, DisableClockSkewCorrection = true,
                    DisableLogging = true, LogResponse = false, LogMetrics = false, HttpClientFactory = owner, IgnoreConfiguredEndpointUrls = true,
                    EndpointDiscoveryEnabled = false, DisableS3ExpressSessionAuth = true });
                foreach (var (key, expected) in finalObjects)
                {
                    try
                    {
                        string etag; string? version;
                        using (var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = config.Connection.Bucket, Key = key }, cleanupDeadline.Token))
                        {
                            if (response.Metadata["x-amz-meta-bucketbus-operation"] != expected.Operation.ToString("N")
                                || Convert.ToBase64String(await SHA256.HashDataAsync(response.ResponseStream, cleanupDeadline.Token)) != expected.Hash)
                                throw new InvalidOperationException();
                            etag = response.ETag; version = response.VersionId;
                        }
                        cleanup[key] = "Delete intent; unknown until confirmed"; Save();
                        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = config.Connection.Bucket, Key = key, IfMatch = etag, VersionId = version }, cleanupDeadline.Token);
                        try { using var retained = await client.GetObjectAsync(new GetObjectRequest { BucketName = config.Connection.Bucket, Key = key, VersionId = version }, cleanupDeadline.Token); cleanup[key] = "RETAINED"; }
                        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { cleanup[key] = "CONFIRMED absent"; }
                    }
                    catch { cleanup[key] = "UNKNOWN or ownership mismatch; retained for inspection"; }
                    Save();
                }
            }
            if (config is not null) Save();
            if (Directory.Exists(local)) Directory.Delete(local, true);
        }
        Assert.True(outcomes.GetValueOrDefault("qualification") == "PASSED" && !outcomes.ContainsKey("failure"), "Multipart qualification did not pass; inspect private evidence.");
        Assert.True(finalObjects.Keys.All(k => cleanup.GetValueOrDefault(k) == "CONFIRMED absent"), "Owned object cleanup unresolved; inspect private evidence.");

        IObjectTransferSession Session() => S3TransferFactory.Create(profile!, credentials!,
            new MultipartLabGuard(profile!.Settings, config!.Connection.Bucket, keys, uploadIds, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }), TimeSpan.FromMinutes(5));
        Task PersistOnly(string key, MultipartUpload state)
        { jobs[key] = jobs[key] with { Multipart = state, Revision = jobs[key].Revision + 1 }; if (state.UploadId is { } id) uploadIds[id] = key; Save(); return Task.CompletedTask; }
        void Save()
        {
            if (config is null) return;
            var path = config.ManifestFile + ".multipart.json";
            LiveGate.PrivatePath(path, Path.GetDirectoryName(config.ManifestFile)!);
            LiveGate.PrivatePath(path + ".tmp", Path.GetDirectoryName(config.ManifestFile)!);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new { Schema = 1, ExecutedUtc = DateTimeOffset.UtcNow, Jobs = jobs, Outcomes = outcomes, Cleanup = cleanup, FinalObjects = finalObjects.ToDictionary(x => x.Key, x => new { x.Value.Operation, x.Value.Hash }) }); stream.Flush(true); }
            File.Move(path + ".tmp", path, true);
        }
    }
    private sealed class LabHttpOwner(HttpMessageHandler handler) : HttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler) { Timeout = TimeSpan.FromSeconds(30) };
        public override HttpClient CreateHttpClient(IClientConfig config) => _client;
        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => false;
        public void Dispose() => _client.Dispose();
    }
}

internal sealed class MultipartLabGuard(ProfileSettings settings, string bucket, HashSet<string> keys, Dictionary<string, string> uploadIds, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!; var endpoint = new Uri(settings.Endpoint); var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var prefix = "/" + bucket + "/";
        if (uri.Scheme != "https" || uri.Authority != endpoint.Authority || !path.StartsWith(prefix, StringComparison.Ordinal) || !keys.Contains(path[prefix.Length..]))
            throw new InvalidOperationException("Multipart fixture target rejected.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('=', 2)).ToArray();
        var id = query.FirstOrDefault(e => e[0] == "uploadId");
        if (id is not null && (id.Length != 2 || (!uploadIds.TryGetValue(Uri.UnescapeDataString(id[1]), out var ownedKey) || ownedKey != path[prefix.Length..]))) throw new InvalidOperationException("Unowned upload ID rejected.");
        if (request.Method == HttpMethod.Delete && id is null && !request.Headers.Contains("If-Match")) throw new InvalidOperationException("Unconditional fixture deletion rejected.");
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Put && request.Method != HttpMethod.Post && request.Method != HttpMethod.Delete)
            throw new InvalidOperationException("Fixture method rejected.");
        return base.SendAsync(request, token);
    }
}
