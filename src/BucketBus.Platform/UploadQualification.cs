using System.Text.Json;
using BucketBus.Core;

namespace BucketBus.Platform;

// Capability evidence, not credentials or a security bypass. No automatic endpoint probes.
public sealed record UploadQualificationEvidence(int Schema, string Target, string Bucket, DateTimeOffset VerifiedUtc,
    bool ConditionalCollisionRejected, bool RoundTripSha256Verified);
public sealed class UploadQualification : IUploadQualification
{
    public static string DefaultPath => Path.Combine(JsonProfileStore.DefaultDirectory, "upload-qualification.json");
    private readonly string _path;
    public UploadQualification() : this(DefaultPath) { }
    public UploadQualification(string path) => _path = path;
    public bool Allows(ProfileSettings settings, string bucket)
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 32768) return false;
            var records = JsonSerializer.Deserialize<UploadQualificationEvidence[]>(File.ReadAllText(_path));
            var now = DateTimeOffset.UtcNow;
            return records?.Any(e => e.Schema == 1 && e.Target == settings.TargetFingerprint() && e.Bucket == bucket
                && e.ConditionalCollisionRejected && e.RoundTripSha256Verified && e.VerifiedUtc <= now && e.VerifiedUtc > now.AddDays(-30)) == true;
        }
        catch { return false; }
    }
}
