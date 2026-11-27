using Seein.Core;
namespace Seein.Tests;

public sealed class WindowsCommandLineTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("C:\\space folder\\server.py", "\"C:\\space folder\\server.py\"")]
    [InlineData("C:\\space folder\\", "\"C:\\space folder\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void PathsAndQuotesRemainSingleWindowsArguments(string argument, string expected) => Assert.Equal(expected, WindowsCommandLine.Quote(argument));
    [Fact] public void NullBytesCannotTruncateACommand() => Assert.Throws<ArgumentException>(() => WindowsCommandLine.Quote("a\0b"));
}
