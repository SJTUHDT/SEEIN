using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Seein.Core.Displays;

namespace Seein.Tray;

public static class DisplayNativeHost
{
    public static void Run()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        while (input.ReadLine() is { } line)
        {
            DisplayRequest? request = null;
            DisplayResponse response;
            try
            {
                request = JsonSerializer.Deserialize<DisplayRequest>(line, DisplayControlClient.Json);
                response = request is null ? new(null, false, Error: "请求为空。") : Execute(request);
            }
            catch (Exception ex) { response = new(request?.RequestId, false, Error: ex.Message); }
            output.WriteLine(JsonSerializer.Serialize(response, DisplayControlClient.Json));
        }
    }

    private static DisplayResponse Execute(DisplayRequest request)
    {
        using var monitors = Enumerate();
        if (request.Operation == "list") return new(request.RequestId, true, Targets: monitors.Items.Select(m => m.Target).ToArray());
        var monitor = monitors.Items.SingleOrDefault(m => m.Target.Id == request.TargetId);
        if (monitor is null) return new(request.RequestId, false, Error: "显示器已断开或身份发生变化，请刷新设备。");
        if (request.Operation == "restore-dp1")
        {
            // An explicit recovery key must not depend on a capability query on the other input.
            // It can only address the saved monitor, and can only request standard DP 1 (0x0F).
            if (!SetVCPFeature(monitor.Handle, (byte)DisplayFeature.Input, 0x0F))
                return new(request.RequestId, false, Error: "DP1切换失败：显示器未接受DDC/CI指令。请检查与主机的连接。");
            Thread.Sleep(100);
            var confirmed = GetVCPFeatureAndVCPFeatureReply(monitor.Handle, (byte)DisplayFeature.Input, out _, out var input, out _) && input == 0x0F;
            return new(request.RequestId, confirmed, Sent: true, Error: confirmed ? null : "DP1指令已发送，等待显示器切换；当前无法回读确认。");
        }
        if (!GetCapabilitiesStringLength(monitor.Handle, out var length) || length is 0 or > 65536)
            return new(request.RequestId, false, Error: "无法读取DDC/CI能力。请在显示器菜单中启用DDC/CI，并检查连接。");
        var buffer = new StringBuilder((int)length);
        if (!CapabilitiesRequestAndCapabilitiesReply(monitor.Handle, buffer, length))
            return new(request.RequestId, false, Error: "显示器未回应DDC/CI能力查询，请稍后刷新。");
        var capabilities = DisplayCapabilities.Parse(buffer.ToString());
        if (request.Operation == "read")
            return new(request.RequestId, true, Settings: DisplayFeatures.Labels.Select(p => ReadSetting(monitor.Handle, p.Key, capabilities)).ToArray());
        if (request.Operation != "set" || request.Feature is not { } feature || request.Value is not { } value ||
            !DisplayFeatures.Labels.ContainsKey(feature))
            return new(request.RequestId, false, Error: "不支持的显示器操作。");
        var before = ReadSetting(monitor.Handle, feature, capabilities);
        if (!DisplayFeatures.IsValidValue(before, value))
            return new(request.RequestId, false, Error: "显示器不支持此设置或数值超出支持范围。", Settings: [before]);
        if (!SetVCPFeature(monitor.Handle, (byte)feature, value))
            return new(request.RequestId, false, Error: "显示器未接受设置，请刷新后重试。");
        Thread.Sleep(80);
        var after = ReadSetting(monitor.Handle, feature, capabilities);
        if (!after.Supported || after.Current != value)
            return new(request.RequestId, false, Settings: [after], Sent: true, Error:
                DisplayFeatures.RequiresExplicitAction(feature) ? "已发送，等待重新连接或确认显示器状态。" : "已发送，但回读未确认，请刷新查看实际值。");
        return new(request.RequestId, true, Settings: [after], Sent: true);
    }

    private static DisplaySetting ReadSetting(nint handle, DisplayFeature feature, IReadOnlyDictionary<byte, uint[]> caps)
    {
        var label = DisplayFeatures.Labels[feature];
        if (!caps.TryGetValue((byte)feature, out var values)) return new(feature, label, false, Error: "显示器未声明支持");
        if (!GetVCPFeatureAndVCPFeatureReply(handle, (byte)feature, out _, out var current, out var maximum))
            return new(feature, label, false, Error: "当前无法读取，可能受HDR或显示模式限制");
        if (DisplayFeatures.IsChoice(feature))
        {
            // Do not invent enum values when firmware omits them. Current value remains readable.
            var choices = values.ToDictionary(v => v, v => DisplayFeatures.OptionLabel(feature, v));
            if (choices.Count == 0) return new(feature, label, false, current, Options: choices, Error: $"当前：{DisplayFeatures.OptionLabel(feature, current)}；未提供可写选项");
            return new(feature, label, true, current, Options: choices);
        }
        return maximum == 0 || current > maximum
            ? new(feature, label, false, current, maximum, Error: "显示器返回的范围无效")
            : new(feature, label, true, current, maximum);
    }

    private sealed record Entry(DisplayTarget Target, nint Handle);
    private sealed class MonitorCollection : IDisposable
    {
        public List<Entry> Items { get; } = [];
        public void Dispose() { foreach (var item in Items) DestroyPhysicalMonitor(item.Handle); }
    }

    private static MonitorCollection Enumerate()
    {
        var collection = new MonitorCollection();
        MonitorEnum callback = (nint handle, nint hdc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(handle, ref info) || !GetNumberOfPhysicalMonitorsFromHMONITOR(handle, out var count) || count > 32) return true;
            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(handle, count, physical)) return true;
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            var identified = EnumDisplayDevices(info.DeviceName, 0, ref device, 1);
            var identity = identified && !string.IsNullOrEmpty(device.DeviceId) ? device.DeviceId : info.DeviceName;
            var model = identified ? ReadEdidName(device.DeviceId) : null;
            for (var i = 0; i < physical.Length; i++)
            {
                var name = model ?? physical[i].Description ?? "显示器";
                collection.Items.Add(new(new(identity + "|" + i, name, info.DeviceName), physical[i].Handle));
            }
            return true;
        };
        try
        {
            if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new IOException("无法枚举实体显示器。");
            return collection;
        }
        catch { collection.Dispose(); throw; }
    }

    private static string? ReadEdidName(string id)
    {
        try
        {
            var pieces = id.Split('#');
            if (pieces.Length < 3) return null;
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + pieces[1] + "\\" + pieces[2] + @"\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 126) return null;
            for (var offset = 54; offset + 18 <= 126; offset += 18)
                if (edid[offset] == 0 && edid[offset + 1] == 0 && edid[offset + 3] == 0xFC)
                    return Encoding.ASCII.GetString(edid, offset + 5, 13).Trim('\0', '\n', '\r', ' ');
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PhysicalMonitor
    { public nint Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    private delegate bool MonitorEnum(nint monitor, nint hdc, ref Rect rect, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice info, uint flags);
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyPhysicalMonitor(nint monitor);
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCapabilitiesStringLength(nint monitor, out uint length);
    [DllImport("dxva2.dll", CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CapabilitiesRequestAndCapabilitiesReply(nint monitor, StringBuilder buffer, uint length);
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVCPFeatureAndVCPFeatureReply(nint monitor, byte code, out uint type, out uint value, out uint maximum);
    [DllImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetVCPFeature(nint monitor, byte code, uint value);
}
