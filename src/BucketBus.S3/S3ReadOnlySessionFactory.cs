using Amazon.Runtime;
using Amazon;
using Amazon.S3;
using BucketBus.Core;

namespace BucketBus.S3;

public sealed class S3ReadOnlySessionFactory : IReadOnlyStorageSessionFactory
{
    static S3ReadOnlySessionFactory()
    {
        // SDK utility loggers are process-wide and can bypass per-client DisableLogging.
        // BucketBus owns SDK diagnostics in this single process; never enable raw SDK logs.
        AWSConfigs.LoggingConfig.LogTo = LoggingOptions.None;
        AWSConfigs.LoggingConfig.LogResponses = ResponseLoggingOption.Never;
        AWSConfigs.LoggingConfig.LogMetrics = false;
    }
    public IReadOnlyStorageSession Create(ConnectionProfile profile, Credentials credentials, Func<CancellationToken, Task<bool>> isCurrent) =>
        Create(profile, credentials, isCurrent, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, TimeSpan.FromSeconds(30));

    internal static S3ReadOnlySession Create(ConnectionProfile profile, Credentials credentials,
        Func<CancellationToken, Task<bool>> isCurrent, HttpMessageHandler transport, TimeSpan deadline, int pageLimit = 4096)
    {
        profile = profile.Validate();
        if (profile.Credential is null) { transport.Dispose(); throw new ArgumentException("An explicit credential binding is required."); }
        var guard = new ReadTransport(profile, transport);
        var clients = new OwnedHttpClientFactory(guard);
        var configuration = new AmazonS3Config
        {
            ServiceURL = profile.Settings.Endpoint, AuthenticationRegion = profile.Settings.Region,
            ForcePathStyle = profile.Settings.Addressing == AddressingStyle.Path,
            RetryMode = RequestRetryMode.Standard, MaxErrorRetry = 2, MaxStaleConnectionRetries = 0,
            AllowAutoRedirect = false, DisableClockSkewCorrection = true,
            DisableLogging = true, LogMetrics = false, LogResponse = false,
            DisableMultiregionAccessPoints = true, DisableS3ExpressSessionAuth = true,
            UseArnRegion = false, UseAccelerateEndpoint = false, UseDualstackEndpoint = false,
            UseFIPSEndpoint = false, IgnoreConfiguredEndpointUrls = true, EndpointDiscoveryEnabled = false,
            HttpClientFactory = clients
        };
        AWSCredentials explicitCredentials = credentials.SessionToken is null
            ? new BasicAWSCredentials(credentials.AccessKey, credentials.SecretKey)
            : new SessionAWSCredentials(credentials.AccessKey, credentials.SecretKey, credentials.SessionToken);
        try { return new(profile, new ExactKeyS3Client(explicitCredentials, configuration), clients, guard, isCurrent, deadline, pageLimit); }
        catch { clients.Dispose(); throw; }
    }
    private sealed class OwnedHttpClientFactory(ReadTransport guard) : HttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(guard) { Timeout = Timeout.InfiniteTimeSpan };
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => _client;
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
        public void Dispose() => _client.Dispose();
    }
}
