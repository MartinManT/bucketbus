using BucketBus.Core;

namespace BucketBus.Core.Tests;

public sealed class ByteSizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")] [InlineData(36, "36 B")] [InlineData(1023, "1,023 B")]
    [InlineData(1024, "1 KiB")] [InlineData(1536, "1.5 KiB")]
    [InlineData(170283, "166.3 KiB")] [InlineData(3150938283, "2.93 GiB")]
    [InlineData(1048575, "1 MiB")] [InlineData(1048576, "1 MiB")]
    [InlineData(1099511627776, "1 TiB")] [InlineData(1125899906842624, "1 PiB")]
    [InlineData(long.MaxValue, "8192 PiB")]
    public void Iec_boundaries_rounding_and_large_values(long bytes, string expected) => Assert.Equal(expected, ByteSizeFormatter.Format(bytes));
    [Fact]
    public void Exact_counts_and_unknown_values_remain_distinct()
    {
        Assert.Equal("3,150,938,283 B", ByteSizeFormatter.Format(3150938283, FileSizeDisplay.ExactBytes));
        Assert.Equal("9,223,372,036,854,775,807 B", ByteSizeFormatter.Exact(long.MaxValue));
        Assert.Equal("Unknown", ByteSizeFormatter.Format(null)); Assert.Equal("Unknown", ByteSizeFormatter.Format(-1));
    }
}
