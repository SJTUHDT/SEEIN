using Seein.Core.Displays;

namespace Seein.Tests;

public sealed class DisplayShortcutTests
{
    [Theory]
    [InlineData(3u, 0x44u, true)]
    [InlineData(6u, 0x75u, true)]
    [InlineData(1u, 0x31u, true)]
    [InlineData(0u, 0x44u, false)]
    [InlineData(4u, 0x44u, false)]
    [InlineData(11u, 0x44u, false)]
    [InlineData(3u, 0x7Bu, false)]
    [InlineData(3u, 0x10u, false)]
    [InlineData(3u, 0xFFFFu, false)]
    public void RejectsReservedAndUnmodifiedKeys(uint modifiers, uint key, bool expected) =>
        Assert.Equal(expected, new DisplayShortcutSettings("saved-monitor", modifiers, key, true).IsValid);

    [Fact]
    public void RequiresASpecificSavedMonitor() => Assert.False(new DisplayShortcutSettings(Enabled: true).IsValid);
}
