using System.Net;
using Seein.Core.Apps;

namespace Seein.Tests;

public sealed class NetworkSamplerPolicyTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void UsesTcpFallbackOnlyAfterSelectedEtwStops(bool selected, bool running, bool expected) =>
        Assert.Equal(expected, NetworkSamplerPolicy.ShouldUseTcpFallback(selected, running));

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.8.9.10", "0.0.0.0", true)]
    [InlineData("::1", "::1", true)]
    [InlineData("::ffff:127.8.9.10", "::", true)]
    [InlineData("0.0.0.0", "127.8.9.10", true)]
    [InlineData("::", "::1", true)]
    [InlineData("192.168.5.2", "192.168.5.3", false)]
    [InlineData("fd00::2", "fe80::3", false)]
    [InlineData("::ffff:192.168.5.2", "::ffff:192.168.5.3", false)]
    public void ExcludesLoopbackOnEitherEndpoint(string source, string destination, bool expected) =>
        Assert.Equal(expected, NetworkSamplerPolicy.IsLoopbackTraffic(IPAddress.Parse(source), IPAddress.Parse(destination)));
}
