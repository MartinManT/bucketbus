using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BucketBus.Core;

public enum AddressingStyle { VirtualHosted, Path }
public enum CredentialStorage { Session, Secure }

public sealed record ProfileSettings([property: JsonRequired] string Name, [property: JsonRequired] string Endpoint,
    [property: JsonRequired] string Region, [property: JsonRequired] AddressingStyle Addressing,
    string? Bucket = null, string? Prefix = null)
{
    public ProfileSettings Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || Name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            || string.IsNullOrWhiteSpace(Region) || Region.Length > 100
            || Region.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || !Enum.IsDefined(Addressing)
            || Bucket is { Length: > 255 } || Bucket?.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\') == true
            || Prefix is { Length: > 1024 })
            throw new ArgumentException("Invalid profile fields.");
        if (Prefix is not null && new UTF8Encoding(false, true).GetByteCount(Prefix) > 1024)
            throw new ArgumentException("Prefix exceeds the S3 key byte limit.");
        // Inspect raw text before Uri normalization, which otherwise hides backslashes,
        // whitespace, dot segments, empty queries and embedded user information.
        if (string.IsNullOrEmpty(Endpoint) || Endpoint.Length > 2048
            || Endpoint.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || Endpoint.IndexOfAny(['\\', '@', '?', '#', '%']) >= 0
            || !Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || uri.Port is < 1 or > 65535
            || Endpoint[8..].TrimEnd('/').Contains('/') || uri.AbsolutePath != "/")
            throw new ArgumentException("Use an HTTPS endpoint containing only a host and optional port.");
        return this with { Endpoint = uri.GetLeftPart(UriPartial.Authority), Bucket = string.IsNullOrEmpty(Bucket) ? null : Bucket };
    }

    public string TargetFingerprint() => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{Validate().Endpoint}\n{Region}\n{Addressing}")));
}

public sealed record CredentialReference([property: JsonRequired] Guid ProfileId, [property: JsonRequired] Guid BindingId,
    [property: JsonRequired] CredentialStorage Storage, [property: JsonRequired] string Target)
{
    [JsonIgnore] public string VaultKey => $"BucketBus/v1/{ProfileId:N}/{BindingId:N}/{Target}";
    [JsonIgnore] public bool IsValid => ProfileId != Guid.Empty && BindingId != Guid.Empty && Enum.IsDefined(Storage)
        && Target is { Length: 64 } && Target.All(c => char.IsAsciiHexDigit(c) && !char.IsLower(c));
    public override string ToString() => "BucketBus credential reference";
}

public sealed record ConnectionProfile([property: JsonRequired] Guid Id, [property: JsonRequired] long Revision,
    [property: JsonRequired] ProfileSettings Settings, CredentialReference? Credential)
{
    public ConnectionProfile Validate()
    {
        if (Id == Guid.Empty || Revision < 1 || Settings is null)
            throw new ArgumentException("Invalid profile identity or revision.");
        var settings = Settings.Validate();
        if (Credential is not null && (!Credential.IsValid || Credential.ProfileId != Id
            || Credential.Target != settings.TargetFingerprint()))
            throw new ArgumentException("Credential binding does not match this profile target.");
        return this with { Settings = settings };
    }
}

public sealed record ProfileSnapshot(long Revision, IReadOnlyList<ConnectionProfile> Profiles);
public enum ProfileStatus { Success, Conflict, Invalid, UnsupportedSchema, Unavailable, Cancelled, Failure }
public sealed record ProfileReadResult(ProfileStatus Status, ProfileSnapshot? Snapshot = null);
public interface IProfileStore
{
    Task<ProfileReadResult> ReadAsync(CancellationToken cancellationToken = default);
    Task<ProfileStatus> WriteAsync(ProfileSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default);
}
