using System.Text.Json;
using BucketBus.Core;

namespace BucketBus.S3.IntegrationTests;

public sealed class ReadOnlyQualificationTests
{
    [Fact, Trait("Category", "LiveS3")]
    public async Task Explicitly_opted_in_read_only_qualification()
    {
        if (Environment.GetEnvironmentVariable("BUCKETBUS_RUN_LIVE_TESTS") != "1")
            Assert.Skip("Live qualification SKIPPED: explicit opt-in required; no configuration, credentials or network accessed.");
        var outcomes = new Dictionary<string, string>();
        LiveConfig? config = null;
        try
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BucketBus.slnx"))) repository = repository.Parent;
            if (repository is null) throw new InvalidOperationException();
            config = LiveGate.Load("1", Environment.GetEnvironmentVariable("BUCKETBUS_LIVE_CONFIG"), Path.Combine(repository.FullName, "docs/internal"), File.ReadAllText,
                Environment.GetEnvironmentVariable("BUCKETBUS_APPROVED_ENDPOINT"))!;
            var settings = config.Validate();
            var credentials = LiveGate.ReadNamedCredentials(config.CredentialProfile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws/credentials"));
            var id = Guid.NewGuid();
            var profile = new ConnectionProfile(id, 1, settings, new(id, Guid.NewGuid(), CredentialStorage.Session, settings.TargetFingerprint()));
            var handler = new CountingReadHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
            await using var session = S3ReadOnlySessionFactory.Create(profile, credentials, _ => Task.FromResult(true), handler, TimeSpan.FromSeconds(30));
            using var total = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            total.CancelAfter(TimeSpan.FromMinutes(6));
            // Direct bucket is deliberately tested before optional account discovery.
            var listing = session.ListObjects(config.Bucket);
            var count = 0; var complete = false;
            for (var page = 0; page < config.MaximumPages; page++)
            {
                var result = await listing.NextAsync(total.Token);
                if (!result.IsSuccess) { outcomes["DirectObjects"] = "FAILED: " + result.Error; break; }
                count++; complete = result.Value!.IsComplete;
                if (complete) break;
            }
            outcomes.TryAdd("DirectObjects", "PASSED");
            outcomes["Pagination"] = count > 1 ? "PASSED: multiple pages consumed" : "NOT TESTED: insufficient pages";
            outcomes["Enumeration"] = complete ? "PASSED: end reached" : "BOUNDED: configured page cap or failure";
            outcomes["ListBuckets"] = "SKIPPED: not selected";
            if (config.DiscoverBuckets)
            {
                var result = await session.ListBuckets().NextAsync(total.Token);
                outcomes["ListBuckets"] = result.IsSuccess ? "PASSED" : result.Error == StorageError.AccessDenied ? "BLOCKED: identity denied discovery" : "FAILED: " + result.Error;
            }
            for (var i = 0; i < config.HeadKeys.Length; i++)
            {
                var result = await session.GetMetadataAsync(config.Bucket, config.HeadKeys[i], total.Token);
                outcomes["MetadataCase" + i] = result.IsSuccess && result.Value!.Key == config.HeadKeys[i] ? "PASSED" : "FAILED: " + result.Error;
            }
            outcomes["LeadingSlashHead"] = config.HeadKeys.Any(k => k.StartsWith('/')) ? "See configured metadata cases" : "NOT TESTED: no suitable key configured";
            var before = handler.Sends;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var cancelledResult = await session.ListObjects(config.Bucket).NextAsync(cancelled.Token);
            outcomes["PreCancelledRequest"] = cancelledResult.Error == StorageError.Cancelled && before == handler.Sends ? "PASSED: zero sends" : "FAILED";
            outcomes["RetryTimeoutNetworkFaults"] = "NOT TESTED live: deterministic offline evidence only";
            outcomes["RestrictedIdentity"] = "NOT TESTED: no separately selected restricted identity";
            outcomes["AwsS3"] = "NOT TESTED: Ceph results do not qualify AWS";
            outcomes["PhysicalSends"] = handler.Sends.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception) { outcomes["Run"] = "FAILED: configuration, credentials or execution unavailable; raw details suppressed"; }
        if (config is not null)
        {
            try { await File.WriteAllTextAsync(config.EvidenceFile, JsonSerializer.Serialize(new { ExecutedUtc = DateTimeOffset.UtcNow, Outcomes = outcomes }, new JsonSerializerOptions { WriteIndented = true }), TestContext.Current.CancellationToken); }
            catch (Exception) { outcomes["Evidence"] = "FAILED: private evidence could not be written"; }
        }
        Assert.True(outcomes.Count != 0 && !outcomes.Values.Any(v => v.StartsWith("FAILED", StringComparison.Ordinal)), "Live qualification FAILED; inspect private evidence/configuration. Raw provider details suppressed.");
    }
}
internal sealed class CountingReadHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public int Sends { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) throw new InvalidOperationException("Mutation blocked.");
        Sends++;
        return base.SendAsync(request, cancellationToken);
    }
}
