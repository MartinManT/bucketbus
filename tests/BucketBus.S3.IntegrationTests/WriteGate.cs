using System.Text.Json;
using System.Text.Json.Serialization;
using BucketBus.Core;

namespace BucketBus.S3.IntegrationTests;

internal sealed record WriteConfig(LiveConfig Connection, string Prefix, string ManifestFile, string QualificationFile);
internal static class WriteGate
{
    public static WriteConfig? Load(string? optIn, string? configPath, string privateRoot, Func<string, string> read,
        string? approvedEndpoint, string? approvedScope)
    {
        if (optIn != "1") return null;
        if (string.IsNullOrWhiteSpace(approvedEndpoint) || string.IsNullOrWhiteSpace(approvedScope))
            throw new InvalidOperationException("Independent endpoint and write-scope approvals required.");
        var approved = new ProfileSettings("Approval", approvedEndpoint, "unused", AddressingStyle.Path).Validate().Endpoint;
        if (configPath is null) throw new InvalidOperationException("Explicit write configuration required.");
        var config = JsonSerializer.Deserialize<WriteConfig>(read(LiveGate.PrivatePath(configPath, privateRoot)),
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidOperationException();
        var target = config.Connection.Validate();
        if (target.Addressing != AddressingStyle.Path || target.Endpoint != approved || config.Connection.Bucket + "/" + config.Prefix != approvedScope
            || config.Prefix.Length is < 1 or > 120 || !config.Prefix.EndsWith('/')
            || config.Prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '/' or '_'))
            || config.Connection.DiscoverBuckets || config.Connection.HeadKeys.Length != 0)
            throw new InvalidOperationException("Write target/scope mismatch.");
        LiveGate.PrivatePath(config.ManifestFile, privateRoot); LiveGate.PrivatePath(config.QualificationFile, privateRoot);
        LiveGate.PrivatePath(config.Connection.EvidenceFile, privateRoot);
        return config;
    }
}
