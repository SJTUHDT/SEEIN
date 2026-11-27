using Seein.Core.Themes;

namespace Seein.Tests;

public sealed class ThemeArchiveLimitsTests
{
    [Fact]
    public void TotalExpandedLimitAcceptsBoundaryAndRejectsExcess()
    {
        Assert.True(ThemeArchiveLimits.TryAddEntryLength(ThemeArchiveLimits.MaxExpandedBytes - 1, 1, out var total));
        Assert.Equal(ThemeArchiveLimits.MaxExpandedBytes, total);
        Assert.False(ThemeArchiveLimits.TryAddEntryLength(total, 1, out _));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void TotalExpandedLimitRejectsInvalidOrOverflowingLengths(long current, long next) =>
        Assert.False(ThemeArchiveLimits.TryAddEntryLength(current, next, out _));
}
