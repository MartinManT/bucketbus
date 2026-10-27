using BucketBus.Core;

namespace BucketBus.Core.Tests;

public sealed class MultipartPlanTests
{
    [Theory]
    [InlineData(1)] [InlineData(128L * 1024 * 1024)] [InlineData(5L * 1024 * 1024 * 1024)]
    [InlineData(5L * 1024 * 1024 * 1024 + 1)] [InlineData(160L * 1024 * 1024 * 1024)]
    [InlineData(MultipartPlan.MaximumObject)]
    public void Plan_covers_exact_long_size_with_bounded_parts(long size)
    {
        var plan = MultipartPlan.Create(size); plan.Validate(size);
        Assert.InRange(plan.PartCount, 1, 10000); Assert.InRange(plan.PartSize, MultipartPlan.MinimumPart, MultipartPlan.MaximumPart);
        long total = 0;
        for (var part = 1; part <= plan.PartCount; part++)
        {
            Assert.Equal(total, plan.Offset(part)); var length = plan.Length(part, size);
            Assert.InRange(length, part == plan.PartCount ? 1 : MultipartPlan.MinimumPart, MultipartPlan.MaximumPart); total = checked(total + length);
        }
        Assert.Equal(size, total);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(long.MaxValue)] [InlineData(MultipartPlan.MaximumObject + 1)]
    public void Unsupported_lengths_fail_before_initiation(long length) => Assert.Throws<TransferException>(() => MultipartPlan.Create(length));
    [Fact]
    public void Invalid_plan_or_part_number_is_rejected()
    {
        Assert.Throws<JournalException>(() => new MultipartPlan(MultipartPlan.MinimumPart - 1, 2).Validate(100));
        Assert.Throws<JournalException>(() => new MultipartPlan(MultipartPlan.MaximumPart + 1, 1).Validate(100));
        Assert.Throws<JournalException>(() => new MultipartPlan(MultipartPlan.MinimumPart, 10001).Validate(MultipartPlan.MinimumPart * 10001));
        Assert.Throws<JournalException>(() => MultipartPlan.Create(1).Length(0, 1));
    }
}
