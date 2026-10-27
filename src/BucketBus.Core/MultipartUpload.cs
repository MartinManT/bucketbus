using System.Text.Json.Serialization;

namespace BucketBus.Core;

public sealed record MultipartPlan(long PartSize, int PartCount)
{
    public const long Threshold = 128L * 1024 * 1024;
    public const long MinimumPart = 5L * 1024 * 1024;
    public const long MaximumPart = 5L * 1024 * 1024 * 1024;
    // Conservative application ceiling for initial S3-compatible support, not a claim about every provider.
    public const long MaximumObject = 5L * 1024 * 1024 * 1024 * 1024;
    public static MultipartPlan Create(long length)
    {
        if (length <= 0 || length > MaximumObject) throw new TransferException(TransferError.RemoteRejected);
        var required = checked((length - 1) / 10000 + 1);
        var size = Math.Max(16L * 1024 * 1024, checked((required + 1024 * 1024 - 1) / (1024 * 1024) * (1024 * 1024)));
        return new(size, checked((int)((length - 1) / size + 1)));
    }
    public long Offset(int number) => checked((number - 1L) * PartSize);
    public long Length(int number, long total)
    {
        if (number < 1 || number > PartCount) throw new JournalException();
        return Math.Min(PartSize, checked(total - Offset(number)));
    }
    public void Validate(long total)
    {
        if (total <= 0 || total > MaximumObject || PartSize < MinimumPart || PartSize > MaximumPart
            || PartCount is < 1 or > 10000 || (total - 1) / PartSize + 1 != PartCount) throw new JournalException();
    }
}
public enum MultipartPhase { Creating, Uploading, SendingPart, Completing, Verifying, CompletionRejected, Aborting, Aborted, Verified }
public sealed record UploadedPart(int Number, long Size, string ETag, string Sha256);
public sealed record MultipartUpload(MultipartPlan Plan, MultipartPhase Phase, string? UploadId = null,
    UploadedPart[]? Parts = null, int PendingPart = 0)
{
    [JsonIgnore]
    public UploadedPart[] Confirmed => Parts ?? [];
    [JsonIgnore]
    public bool CompletionUncertain => Phase is MultipartPhase.Completing or MultipartPhase.Verifying or MultipartPhase.Verified;
    [JsonIgnore]
    public bool CanResume => UploadId is not null && Phase == MultipartPhase.Uploading && PendingPart == 0;
    [JsonIgnore]
    public bool CanAbort => UploadId is not null && Phase is MultipartPhase.Uploading or MultipartPhase.SendingPart or MultipartPhase.CompletionRejected or MultipartPhase.Aborting;
    [JsonIgnore]
    public long AcknowledgedBytes => Confirmed.Sum(p => p.Size);
    public void Validate(long total)
    {
        if (Plan is null) throw new JournalException();
        Plan.Validate(total);
        if (!Enum.IsDefined(Phase) || UploadId is { Length: 0 or > 4096 } || UploadId?.Any(char.IsControl) == true
            || Phase == MultipartPhase.Creating && (UploadId is not null || Confirmed.Length != 0 || PendingPart != 0)
            || Phase == MultipartPhase.Uploading && PendingPart != 0
            || Phase != MultipartPhase.Creating && UploadId is null || PendingPart < 0 || PendingPart > Plan.PartCount
            || Phase == MultipartPhase.SendingPart && PendingPart != Confirmed.Length + 1
            || Confirmed.Length > Plan.PartCount || Phase is MultipartPhase.Completing or MultipartPhase.Verifying or MultipartPhase.Verified && Confirmed.Length != Plan.PartCount)
            throw new JournalException();
        for (var i = 0; i < Confirmed.Length; i++)
        {
            var part = Confirmed[i];
            if (part is null || part.Number != i + 1 || part.Size != Plan.Length(part.Number, total)
                || string.IsNullOrEmpty(part.ETag) || part.ETag.Length > 1024 || part.ETag.Any(char.IsControl)) throw new JournalException();
            try { if (Convert.FromBase64String(part.Sha256).Length != 32) throw new JournalException(); }
            catch { throw new JournalException(); }
        }
    }
}
