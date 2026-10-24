using System.Globalization;
using System.Xml;
using BucketBus.Core;

namespace BucketBus.S3;

internal sealed class ReadFailure(StorageError error) : Exception("Read-only storage operation failed.")
{ public StorageError Error { get; } = error; }

/// <summary>Last outbound boundary, including any SDK-internal retries/redirects.</summary>
internal sealed class ReadTransport(ConnectionProfile profile, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly Uri _endpoint = new(profile.Settings.Endpoint);
    private string _host = "";
    private string? _xmlRoot;
    private string? _expectedHeadPath;
    private int _attempts;
    public long? HeadLength { get; private set; }
    public void Begin(string? bucket, string? xmlRoot, string? key)
    {
        _host = bucket is not null && profile.Settings.Addressing == AddressingStyle.VirtualHosted
            ? bucket + "." + _endpoint.Host : _endpoint.Host;
        _xmlRoot = xmlRoot;
        _expectedHeadPath = key is null ? null : (profile.Settings.Addressing == AddressingStyle.Path ? "/" + bucket : "") + "/" + key;
        _attempts = 0;
        HeadLength = null;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != "https" || !string.Equals(uri.Host, _host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _endpoint.Port || uri.UserInfo.Length != 0
            || request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            throw new ReadFailure(StorageError.RedirectRejected);
        if (request.Method == HttpMethod.Head && (_expectedHeadPath is null
            || Uri.UnescapeDataString(uri.AbsolutePath) != _expectedHeadPath))
            throw new ReadFailure(StorageError.ProtocolError);
        if (!request.Headers.TryGetValues("Authorization", out var authorization)
            || !authorization.Single().Contains($"/{profile.Settings.Region}/s3/aws4_request", StringComparison.Ordinal))
            throw new ReadFailure(StorageError.AuthenticationFailed);
        if (++_attempts > 3) throw new ReadFailure(StorageError.RetryLimit);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            if ((int)response.StatusCode is >= 300 and < 400) throw new ReadFailure(StorageError.RedirectRejected);
            if (request.Method == HttpMethod.Head)
            {
                if (response.Content.Headers.TryGetValues("Content-Length", out var values))
                {
                    if (!long.TryParse(values.Single(), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                        throw new ReadFailure(StorageError.ProtocolError);
                    HeadLength = length;
                }
                return response;
            }
            // Bound both success and error bodies before the SDK parses them. This also keeps
            // XML parsing inside the total deadline, including slow response-body reads.
            const int maximum = 8 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new ReadFailure(StorageError.ProtocolError);
            using var buffer = new MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var chunk = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + read > maximum) throw new ReadFailure(StorageError.ProtocolError);
                    buffer.Write(chunk, 0, read);
                }
            }
            var bytes = buffer.ToArray();
            if (response.IsSuccessStatusCode)
            {
                using var xml = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximum });
                if (xml.MoveToContent() != XmlNodeType.Element || xml.LocalName != _xmlRoot
                    || xml.NamespaceURI is not ("" or "http://s3.amazonaws.com/doc/2006-03-01/"))
                    throw new ReadFailure(StorageError.ProtocolError);
                var elements = 0;
                var scalarFields = new HashSet<string>(StringComparer.Ordinal);
                while (xml.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (xml.Depth > 64 || xml.NodeType == XmlNodeType.Element && ++elements > 65536)
                        throw new ReadFailure(StorageError.ProtocolError);
                    if (xml.NodeType == XmlNodeType.Element && xml.Depth == 1
                        && xml.LocalName is "Name" or "Prefix" or "Delimiter" or "EncodingType" or "IsTruncated"
                            or "KeyCount" or "MaxKeys" or "ContinuationToken" or "NextContinuationToken"
                        && !scalarFields.Add(xml.LocalName))
                        throw new ReadFailure(StorageError.ProtocolError);
                }
            }
            var content = new ByteArrayContent(bytes);
            foreach (var header in response.Content.Headers) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose();
            response.Content = content;
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}
