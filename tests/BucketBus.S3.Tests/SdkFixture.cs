using System.Net;
using System.Text;
using System.Xml.Linq;
using BucketBus.Core;

namespace BucketBus.S3.Tests;

internal sealed class SdkFixture : IAsyncDisposable
{
    public ScriptTransport Transport { get; }
    public IReadOnlyStorageSession Session { get; }
    public SdkFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        AddressingStyle style = AddressingStyle.Path, TimeSpan? timeout = null, int pageLimit = 4096,
        Func<CancellationToken, Task<bool>>? current = null, bool temporary = false)
    {
        Transport = new(respond);
        var settings = new ProfileSettings("Offline fixture", "https://storage.example.test:9443", "test-region-1", style, "test-bucket");
        var id = Guid.NewGuid();
        var profile = new ConnectionProfile(id, 1, settings, new(id, Guid.NewGuid(),
            temporary ? CredentialStorage.Session : CredentialStorage.Secure, settings.TargetFingerprint()));
        Session = S3ReadOnlySessionFactory.Create(profile, new("SYNTHETICKEY", "synthetic-secret", temporary ? "synthetic-token" : null),
            current ?? (_ => Task.FromResult(true)), Transport, timeout ?? TimeSpan.FromSeconds(30), pageLimit);
    }
    public ValueTask DisposeAsync() => Session.DisposeAsync();
    public static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
    public static string Objects(string contents = "", bool truncated = false, string? next = null, string? prefix = null) =>
        $"<ListBucketResult><Name>test-bucket</Name><EncodingType>url</EncodingType><IsTruncated>{truncated.ToString().ToLowerInvariant()}</IsTruncated>"
        + (prefix is null ? "" : Element("Prefix", Uri.EscapeDataString(prefix))) + contents
        + (next is null ? "" : Element("NextContinuationToken", next)) + "</ListBucketResult>";
    public static string Item(string key, string extra = "") => "<Contents>" + Element("Key", Uri.EscapeDataString(key)) + extra + "</Contents>";
    public static string Element(string name, string value) => new XElement(name, value).ToString(SaveOptions.DisableFormatting);
    public static string Error(string code) => $"<Error><Code>{code}</Code><Message>synthetic-sensitive-server-detail</Message></Error>";
    public static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 1 ? "" : Uri.UnescapeDataString(p[1]));
}

internal sealed class ScriptTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Attempts { get; private set; }
    public bool Disposed { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Attempts++; return respond(request, cancellationToken); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
