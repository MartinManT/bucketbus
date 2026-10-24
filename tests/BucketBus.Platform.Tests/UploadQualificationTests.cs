using System.Text.Json;
using BucketBus.Core;

namespace BucketBus.Platform.Tests;

public sealed class UploadQualificationTests
{
    [Fact]
    public void Evidence_is_exact_target_bucket_bound_expiring_and_fail_closed()
    {
        var path = Path.GetTempFileName();
        try
        {
            var settings = new ProfileSettings("Fixture", "https://storage.example.test", "us-east-1", AddressingStyle.Path);
            var evidence = new UploadQualificationEvidence(1, settings.TargetFingerprint(), "bucket", DateTimeOffset.UtcNow, true, true);
            var qualification = new UploadQualification(path);
            Assert.False(qualification.Allows(settings, "bucket"));
            void Write(UploadQualificationEvidence item) => File.WriteAllText(path, JsonSerializer.Serialize(new[] { item }));
            Write(evidence); Assert.True(qualification.Allows(settings, "bucket"));
            Assert.False(qualification.Allows(settings, "other"));
            Assert.False(qualification.Allows(settings with { Endpoint = "https://other.example.test" }, "bucket"));
            Assert.False(qualification.Allows(settings with { Region = "another" }, "bucket"));
            Write(evidence with { ConditionalCollisionRejected = false }); Assert.False(qualification.Allows(settings, "bucket"));
            Write(evidence with { RoundTripSha256Verified = false }); Assert.False(qualification.Allows(settings, "bucket"));
            Write(evidence with { VerifiedUtc = DateTimeOffset.UtcNow.AddDays(-31) }); Assert.False(qualification.Allows(settings, "bucket"));
            Write(evidence with { Schema = 2 }); Assert.False(qualification.Allows(settings, "bucket"));
            Write(evidence with { VerifiedUtc = DateTimeOffset.UtcNow.AddDays(1) }); Assert.False(qualification.Allows(settings, "bucket"));
        }
        finally { File.Delete(path); }
    }
}
