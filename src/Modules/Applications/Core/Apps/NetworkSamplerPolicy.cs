using System.Net;

namespace Seein.Core.Apps;

public static class NetworkSamplerPolicy
{
    public static bool ShouldUseTcpFallback(bool etwWasSelected, bool etwIsRunning) =>
        etwWasSelected && !etwIsRunning;

    internal static bool IsLoopbackTraffic(IPAddress source, IPAddress destination) =>
        IsLoopback(source) || IsLoopback(destination);

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
