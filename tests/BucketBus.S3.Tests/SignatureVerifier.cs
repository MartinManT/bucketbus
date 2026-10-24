using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BucketBus.S3.Tests;

// Independent synthetic-fixture verification of the actual outgoing URI and signed
// headers. Never used for application signing, and never reports credential material.
internal static class SignatureVerifier
{
    public static bool Matches(HttpRequestMessage request)
    {
        var authorization = request.Headers.GetValues("Authorization").Single();
        var algorithm = authorization[..authorization.IndexOf(' ')];
        var fields = authorization[(authorization.IndexOf(' ') + 1)..].Split(',')
            .Select(s => s.Trim().Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var credential = fields["Credential"].Split('/');
        var scope = string.Join('/', credential.Skip(1));
        string Header(string name) => name == "host" ? request.Headers.Host ?? request.RequestUri!.Authority
            : request.Headers.TryGetValues(name, out var values) ? string.Join(',', values)
            : string.Join(',', request.Content!.Headers.GetValues(name));
        var canonicalHeaders = string.Concat(fields["SignedHeaders"].Split(';')
            .Select(name => name + ":" + Regex.Replace(Header(name).Trim(), "\\s+", " ") + "\n"));
        var canonicalQuery = string.Join('&', request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        var canonical = $"{request.Method}\n{request.RequestUri.AbsolutePath}\n{canonicalQuery}\n{canonicalHeaders}\n{fields["SignedHeaders"]}\n{Header("x-amz-content-sha256")}";
        var stringToSign = $"{algorithm}\n{Header("x-amz-date")}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Encoding.UTF8.GetBytes("AWS4synthetic-secret");
        foreach (var part in credential.Skip(1)) key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
        return expected == fields["Signature"];
    }
}
