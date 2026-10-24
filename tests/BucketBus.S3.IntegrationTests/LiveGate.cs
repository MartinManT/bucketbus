using System.Text.Json;
using System.Text.Json.Serialization;
using BucketBus.Core;

namespace BucketBus.S3.IntegrationTests;

internal sealed record LiveConfig(int Schema, string Endpoint, string ApprovedEndpoint, string Region,
    string Addressing, string Bucket, string CredentialProfile, bool DiscoverBuckets, int MaximumPages,
    string[] HeadKeys, string EvidenceFile)
{
    public ProfileSettings Validate()
    {
        if (Schema != 1 || Endpoint != ApprovedEndpoint || string.IsNullOrWhiteSpace(CredentialProfile)
            || CredentialProfile.Length > 120 || CredentialProfile.Any(char.IsControl)
            || MaximumPages is < 1 or > 8 || HeadKeys is null || HeadKeys.Length > 20
            || HeadKeys.Any(k => string.IsNullOrEmpty(k) || System.Text.Encoding.UTF8.GetByteCount(k) > 1024)
            || string.IsNullOrEmpty(Bucket) || Bucket.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_'))
            || !Enum.TryParse<AddressingStyle>(Addressing, false, out var style) || !Enum.IsDefined(style))
            throw new InvalidOperationException("Invalid explicit live configuration.");
        return new ProfileSettings("Live qualification", Endpoint, Region, style, Bucket).Validate();
    }
}
internal static class LiveGate
{
    public static LiveConfig? Load(string? optIn, string? configPath, string internalDirectory, Func<string, string> read, string? operatorApprovedEndpoint)
    {
        if (optIn != "1") return null; // Before configuration, credentials, clients or network.
        // Independent operator input; never derive it from either field in the target JSON.
        if (string.IsNullOrWhiteSpace(operatorApprovedEndpoint))
            throw new InvalidOperationException("Independent operator endpoint approval required.");
        var approved = new ProfileSettings("Operator approval", operatorApprovedEndpoint, "unused", AddressingStyle.Path).Validate().Endpoint;
        if (string.IsNullOrEmpty(configPath)) throw new InvalidOperationException("Explicit live configuration path required.");
        var path = PrivatePath(configPath, internalDirectory);
        var config = JsonSerializer.Deserialize<LiveConfig>(read(path), new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidOperationException("Invalid live configuration.");
        var settings = config.Validate();
        if (!string.Equals(settings.Endpoint, approved, StringComparison.Ordinal))
            throw new InvalidOperationException("Live target does not match independent operator approval.");
        PrivatePath(config.EvidenceFile, internalDirectory);
        return config;
    }
    public static string PrivatePath(string path, string internalDirectory)
    {
        var root = Path.GetFullPath(internalDirectory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || !full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Live configuration and evidence must be under docs/internal.");
        for (var entry = new FileInfo(full) as FileSystemInfo; entry is not null; entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
            if (entry.LinkTarget is not null) throw new InvalidOperationException("Symlinked private paths are not supported.");
        return full;
    }
    public static Credentials ReadNamedCredentials(string name, string file)
    {
        // Deliberately supports only literal credentials in this exact file/section.
        // No environment, SDK chain, config/process/role, container or metadata fallback.
        var selected = false; var found = false; var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                selected = line[1..^1] == name;
                if (selected && found) throw new InvalidOperationException("Duplicate credential section.");
                found |= selected; continue;
            }
            if (!selected) continue;
            var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || pair[0] is not ("aws_access_key_id" or "aws_secret_access_key" or "aws_session_token") || !fields.TryAdd(pair[0], pair[1]))
                throw new InvalidOperationException("Unsupported explicit credential source.");
        }
        if (!found || !fields.TryGetValue("aws_access_key_id", out var key) || !fields.TryGetValue("aws_secret_access_key", out var secret))
            throw new InvalidOperationException("Named literal credentials unavailable.");
        return new(key, secret, fields.GetValueOrDefault("aws_session_token"));
    }
}
