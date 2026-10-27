using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using BucketBus.Core;

namespace BucketBus.S3;

public sealed class S3TransferFactory : IObjectTransferFactory
{
    public IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials) =>
        Create(profile, credentials, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, TimeSpan.FromHours(2));
    internal static IObjectTransferSession Create(ConnectionProfile profile, Credentials credentials,
        HttpMessageHandler handler, TimeSpan deadline)
    {
        profile = profile.Validate();
        if (profile.Credential is null) { handler.Dispose(); throw new ArgumentException("Explicit credential binding required."); }
        // Same process-wide SDK diagnostic prohibition as read-only sessions.
        AWSConfigs.LoggingConfig.LogTo = LoggingOptions.None;
        AWSConfigs.LoggingConfig.LogResponses = ResponseLoggingOption.Never;
        AWSConfigs.LoggingConfig.LogMetrics = false;
        var guard = new TransferTransport(profile, handler);
        var owner = new ClientOwner(guard);
        var config = new AmazonS3Config
        {
            ServiceURL = profile.Settings.Endpoint, AuthenticationRegion = profile.Settings.Region,
            ForcePathStyle = profile.Settings.Addressing == AddressingStyle.Path,
            RetryMode = RequestRetryMode.Standard, MaxErrorRetry = 0, MaxStaleConnectionRetries = 0,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_SUPPORTED,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_SUPPORTED,
            AllowAutoRedirect = false, DisableClockSkewCorrection = true,
            DisableLogging = true, LogMetrics = false, LogResponse = false,
            DisableMultiregionAccessPoints = true, DisableS3ExpressSessionAuth = true,
            UseArnRegion = false, UseAccelerateEndpoint = false, UseDualstackEndpoint = false,
            UseFIPSEndpoint = false, IgnoreConfiguredEndpointUrls = true, EndpointDiscoveryEnabled = false,
            HttpClientFactory = owner
        };
        AWSCredentials explicitCredentials = credentials.SessionToken is null
            ? new BasicAWSCredentials(credentials.AccessKey, credentials.SecretKey)
            : new SessionAWSCredentials(credentials.AccessKey, credentials.SecretKey, credentials.SessionToken);
        try { return new S3TransferSession(new ExactKeyS3Client(explicitCredentials, config), guard, owner, deadline); }
        catch { owner.Dispose(); throw; }
    }
    private sealed class ClientOwner(HttpMessageHandler guard) : HttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(guard) { Timeout = Timeout.InfiniteTimeSpan };
        public override HttpClient CreateHttpClient(IClientConfig config) => _client;
        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => false;
        public void Dispose() => _client.Dispose();
    }
}

internal sealed class TransferTransport(ConnectionProfile profile, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly Uri _endpoint = new(profile.Settings.Endpoint);
    private string _host = "", _path = "";
    private HttpMethod _method = HttpMethod.Get;
    private int _attempts;
    private Dictionary<string, string> _query = [];
    private bool _conditional, _bucketListing;
    // Fixed allowlisted diagnostics only; never retain request values or provider text.
    public string? FailureReason { get; private set; }
    private void Reject(string reason)
    {
        FailureReason = reason;
        throw new TransferException(TransferError.RemoteRejected);
    }
    public bool PutSent { get; private set; }
    public long? Length { get; private set; }
    public void Begin(string bucket, string key, HttpMethod method)
    {
        if (string.IsNullOrEmpty(bucket) || bucket.Length > 255 || bucket.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_'))
            || string.IsNullOrEmpty(key) || new System.Text.UTF8Encoding(false, true).GetByteCount(key) > 1024)
            throw new TransferException(TransferError.InvalidName);
        if (profile.Settings.Addressing == AddressingStyle.VirtualHosted
            && (!System.Text.RegularExpressions.Regex.IsMatch(bucket, "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                || _endpoint.HostNameType != UriHostNameType.Dns))
            throw new TransferException(TransferError.InvalidName);
        _host = profile.Settings.Addressing == AddressingStyle.VirtualHosted ? bucket + "." + _endpoint.Host : _endpoint.Host;
        _path = (profile.Settings.Addressing == AddressingStyle.Path ? "/" + bucket : "") + "/" + key;
        _method = method; _attempts = 0; Length = null; FailureReason = null; _bucketListing = false; _query = []; _conditional = method == HttpMethod.Put;
    }
    public void BeginMultipart(string bucket, string key, HttpMethod method, string? uploadId = null, int? part = null, int? marker = null, bool completing = false)
    {
        Begin(bucket, key, method); _conditional = completing;
        if (uploadId is null) _query.Add("uploads", "");
        else _query.Add("uploadId", uploadId);
        if (part is { } number) _query.Add("partNumber", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (marker is { } value)
        { _query.Add("part-number-marker", value.ToString(System.Globalization.CultureInfo.InvariantCulture)); _query.Add("max-parts", "1000"); }
    }
    public void BeginMultipartListing(string bucket, string exactKey, string? keyMarker, string? uploadMarker)
    {
        Begin(bucket, exactKey, HttpMethod.Get);
        _bucketListing = true;
        _path = profile.Settings.Addressing == AddressingStyle.Path ? "/" + bucket + "/" : "/";
        _query = new(StringComparer.Ordinal) { ["uploads"] = "", ["prefix"] = exactKey, ["max-uploads"] = "1000" };
        if (keyMarker is not null) _query.Add("key-marker", keyMarker);
        if (uploadMarker is not null) _query.Add("upload-id-marker", uploadMarker);
    }
    private bool QueryMatches(Uri uri)
    {
        if (uri.Query.Length == 0) return _query.Count == 0;
        var entries = uri.Query[1..].Split('&');
        if (entries.Length != _query.Count) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var pair = entry.Split('=', 2); var name = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length == 1 ? "" : Uri.UnescapeDataString(pair[1]);
            if (!seen.Add(name) || !_query.TryGetValue(name, out var expected) || value != expected) return false;
        }
        return true;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri;
        var reason = uri is null || uri.Scheme != "https" || !string.Equals(uri.Host, _host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _endpoint.Port || uri.UserInfo.Length != 0 ? "Request endpoint or authority did not match the saved profile."
            : request.Method != _method ? "Request method did not match the requested operation."
            : Uri.UnescapeDataString(uri.AbsolutePath) != _path && !(_bucketListing && uri.AbsolutePath == _path.TrimEnd('/')) ? "Request path did not match the recorded bucket and key."
            : !QueryMatches(uri) ? "Multipart request parameters did not match the recorded upload."
            : ++_attempts != 1 ? "An unexpected repeat request was blocked."
            : !request.Headers.TryGetValues("Authorization", out var auth) || auth.Count() != 1
                || !auth.Single().Contains($"/{profile.Settings.Region}/s3/aws4_request", StringComparison.Ordinal)
                ? "Request signing region or authorization was invalid." : null;
        if (reason is not null) Reject(reason);
        if (_conditional && (!request.Headers.TryGetValues("If-None-Match", out var condition) || condition.Count() != 1 || condition.Single() != "*"))
            Reject("The required create-only condition was missing or invalid.");
        token.ThrowIfCancellationRequested();
        if (_method == HttpMethod.Put && _query.Count == 0) PutSent = true;
        var response = await base.SendAsync(request, token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400) { response.Dispose(); Reject("Provider returned a redirect; authenticated redirects are blocked."); }
        Length = response.Content.Headers.ContentLength;
        // Successful GET stays streamed. Bound PUT/error bodies before SDK XML parsing.
        if (_method == HttpMethod.Get && _query.Count == 0 && response.IsSuccessStatusCode) return response;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var chunk = new byte[4096]; int n;
            while ((n = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
            { if (buffer.Length + n > (_query.Count > 0 ? 2 * 1024 * 1024 : 65536)) Reject("Provider response exceeded the allowed size."); buffer.Write(chunk, 0, n); }
            var content = new ByteArrayContent(buffer.ToArray());
            foreach (var h in response.Content.Headers) content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            response.Content.Dispose(); response.Content = content; return response;
        }
        catch { response.Dispose(); throw; }
    }
}
