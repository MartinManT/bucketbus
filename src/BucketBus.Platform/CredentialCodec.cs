using System.Security.Cryptography;
using System.Text.Json;
using BucketBus.Core;

namespace BucketBus.Platform;

internal static class CredentialCodec
{
    // These bytes go only to the OS vault, never to the profile serializer or diagnostics.
    public static byte[] Encode(Credentials credentials) => JsonSerializer.SerializeToUtf8Bytes(
        new[] { credentials.AccessKey, credentials.SecretKey });
    public static Credentials Decode(byte[] bytes)
    {
        try
        {
            var fields = JsonSerializer.Deserialize<string[]>(bytes);
            if (fields is not { Length: 2 }) throw new ArgumentException("Invalid vault entry.");
            return new Credentials(fields[0], fields[1]);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
