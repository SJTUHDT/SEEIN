using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace Seein.Core.Protocol;

public interface ISerialDeviceLocator
{
    string? FindPort();
}

public sealed partial class WindowsUsbSerialLocator : ISerialDeviceLocator
{
    private readonly int _vendorId;
    private readonly HashSet<int> _productIds;

    public WindowsUsbSerialLocator(int vendorId = Ds916Protocol.VendorId, IEnumerable<int>? productIds = null)
    {
        _vendorId = vendorId;
        _productIds = new HashSet<int>(productIds ?? Ds916Protocol.ProductIds);
    }

    public string? FindPort()
    {
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return null;

        foreach (var deviceName in usb.GetSubKeyNames())
        {
            var match = UsbIdRegex().Match(deviceName);
            if (!match.Success || Convert.ToInt32(match.Groups[1].Value, 16) != _vendorId ||
                !_productIds.Contains(Convert.ToInt32(match.Groups[2].Value, 16))) continue;

            using var device = usb.OpenSubKey(deviceName);
            if (device is null) continue;
            foreach (var instanceName in device.GetSubKeyNames())
            {
                using var parameters = device.OpenSubKey($@"{instanceName}\Device Parameters");
                var port = parameters?.GetValue("PortName") as string;
                if (!string.IsNullOrWhiteSpace(port) && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    return port;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbIdRegex();
}

