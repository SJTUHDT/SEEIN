using HidSharp;
using Microsoft.Win32;
using Seein.Core.Lighting;

namespace Seein.Service;

internal interface IMsiMysticLightBoard
{
    bool Matches(RgbDeviceInfo info);
    RgbWriteResult Apply(RgbDeviceInfo info, RgbEffectSettings effect, IReadOnlyList<RgbColor>? jargb2Colors = null, int? ringZone = null);
    RgbWriteResult UpdateRing(RgbDeviceInfo info, int zone, IReadOnlyList<RgbColor> frame, bool verify);
    RgbWriteResult EnterDirect(RgbDeviceInfo info);
    RgbWriteResult UpdateAudio(byte master, byte left, byte right);
}

/// <summary>MS-7E61's 18-area Mystic Light feature report. Offsets include the report ID.</summary>
internal sealed class MsiMysticLightBoard : IMsiMysticLightBoard
{
    private const byte SettingsReport = 0x50;
    private const int ReportLength = 290;
    private const int AreaSize = 16;
    private const int Jargb2 = 1;
    private const int SelectAll = 17;
    private const byte Wave = 0x01;
    private const byte Advanced = 0x09;
    private static readonly int[] GroupAreas = [0, 1, 2, 3, 9]; // JARGB1/2/3, JAF and JRGB1.
    private static readonly RgbColor[] DirectPalette = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 255)];
    private static readonly Lazy<bool> LocalModelMatches = new(CheckBoardModel);
    private readonly object _gate = new();
    private HidDevice? _device;
    private byte[]? _ringExpected;
    private string? _ringDevice;
    private int? _ringZone;

    public bool Matches(RgbDeviceInfo info) => info.Type == "主板" &&
        string.Equals(info.Vendor, "MSI", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(info.Name, "MSI MYSTIC LIGHT", StringComparison.OrdinalIgnoreCase) &&
        LocalModelMatches.Value;

    public static bool SupportsEffect(string id) => MsiMysticLightEffects.TryGetMode(id, out _);

    public RgbWriteResult Apply(RgbDeviceInfo info, RgbEffectSettings effect, IReadOnlyList<RgbColor>? jargb2Colors = null, int? ringZone = null)
    {
        if(!Matches(info))return RgbWriteResult.Reject("设备与 MS-7E61 主板不匹配");
        _ringExpected=null;byte[]? expected=null;
        var result=UpdateSettings(current=>expected=BuildGroupReport(current,effect,jargb2Colors,info.Zones,ringZone));
        if(result.Success&&ringZone is not null){_ringExpected=expected;_ringDevice=info.DeviceKey;_ringZone=ringZone;}
        return result;
    }

    public RgbWriteResult UpdateRing(RgbDeviceInfo info,int zone,IReadOnlyList<RgbColor> frame,bool verify)
    {
        if(!Matches(info)||_ringExpected is null||_ringDevice!=info.DeviceKey||_ringZone!=zone)
            return RgbWriteResult.Reject("冷排闭环模式尚未确认，已停止发送逐灯帧");
        return WithDevice(stream=>
        {
            var packet=BuildRingPacket(info.Zones,zone,frame);stream.SetFeature(packet,0,packet.Length);
            if(verify&&!_ringExpected.AsSpan(0,ReportLength-1).SequenceEqual(ReadSettings(stream,true).AsSpan(0,ReportLength-1)))
            { _ringExpected=null;return RgbWriteResult.Reject("主板灯效配置发生变化，已暂停冷排输出，等待恢复"); }
            return RgbWriteResult.Sent;
        });
    }

    public RgbWriteResult EnterDirect(RgbDeviceInfo info)
    {
        _ringExpected=null;
        return Matches(info)?UpdateSettings(current=>BuildDirectReport(current,info.Zones)):RgbWriteResult.Reject("设备与 MS-7E61 主板不匹配");
    }

    public RgbWriteResult UpdateAudio(byte master, byte left, byte right) =>
        WithDevice(stream =>
        {
            var packet = BuildAudioPacket(master, left, right);
            stream.Write(packet, 0, packet.Length);
            return RgbWriteResult.Sent;
        });

    internal static byte[] BuildAudioPacket(byte master, byte left, byte right)
    {
        var packet = new byte[64];
        packet[0] = 0x01;
        packet[1] = 0xC0;
        packet[3] = Math.Min(master, (byte)100);
        packet[4] = Math.Min(left, (byte)100);
        packet[5] = Math.Min(right, (byte)100);
        return packet;
    }

    private RgbWriteResult UpdateSettings(Func<byte[], byte[]> edit) => WithDevice(stream =>
    {
        var requested = edit(ReadSettings(stream));
        stream.SetFeature(requested, 0, requested.Length);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var actual = ReadSettings(stream,true);
            // Firmware owns the final save flag. Compare all configuration bytes.
            if (requested.AsSpan(0, ReportLength - 1).SequenceEqual(actual.AsSpan(0, ReportLength - 1)))
                return RgbWriteResult.ReadBack;
        }
        return RgbWriteResult.Reject("MSI 主板灯效配置回读不一致，请检查其他灯控程序是否同时写入");
    });

    private RgbWriteResult WithDevice(Func<HidStream, RgbWriteResult> action)
    {
        if (!LocalModelMatches.Value) return RgbWriteResult.Reject("当前主板不支持 MS-7E61 灯效协议");
        lock (_gate)
        {
            try
            {
                if (_device is null)
                {
                    var candidates = DeviceList.Local.GetHidDevices(0x0DB0, 0x0076).Take(2).ToArray();
                    if (candidates.Length != 1 || candidates[0].GetMaxFeatureReportLength() != 761 ||
                        candidates[0].GetMaxOutputReportLength() != 64)
                        return RgbWriteResult.Reject("未找到唯一匹配的 MSI 主板灯效控制器");
                    _device = candidates[0];
                }
                using var stream = _device.Open();
                stream.WriteTimeout = 500;
                return action(stream);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or
                UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                _device = null;
                return RgbWriteResult.Reject($"MSI 主板灯效操作失败（{ex.Message}）");
            }
        }
    }

    private static bool CheckBoardModel()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return string.Equals(bios?.GetValue("BaseBoardProduct") as string,
                "MAG B850M MORTAR WIFI (MS-7E61)", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(bios?.GetValue("BaseBoardManufacturer") as string,
                    "Micro-Star International Co., Ltd.", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private static byte[] ReadSettings(HidStream stream,bool afterWrite=false) => MsiSettingsReportReader.Read(()=>
    {
        var data = new byte[ReportLength];
        data[0] = SettingsReport;
        stream.GetFeature(data, 0, data.Length);
        return data;
    },()=>Thread.Sleep(30),afterWrite);

    internal static byte[] BuildGroupReport(byte[] current, RgbEffectSettings input, IReadOnlyList<RgbColor>? jargb2Colors,
        IReadOnlyList<RgbZoneInfo> zones,int? ringZone=null)
    {
        var effect = RgbEffects.NormalizeGroupEffect(input);
        if (effect is null || !MsiMysticLightEffects.TryGetMode(effect.EffectId, out var mode))
            throw new ArgumentException("这个灯效需要逐灯播放");
        var rainbow = MsiMysticLightEffects.UsesRainbowPalette(effect);
        if (!rainbow && jargb2Colors is not null && jargb2Colors.Count != effect.Colors.Count)
            throw new ArgumentException("显卡校色与整机色槽数不一致");

        var result = CopySettings(current);
        var brightness = effect.Brightness > 0
            ? Math.Max(1, (int)Math.Round(effect.Brightness / 20, MidpointRounding.AwayFromZero)) : 0;
        var speed = MsiWaveTiming.NativeSpeedLevel(effect.Speed);
        var options = (byte)((effect.Direction == "backward" ? 0x40 : 0) | (rainbow ? 0 : 0x20) | brightness << 2 | speed);
        var palette = ActiveColors(effect.EffectId, effect.Colors);
        // The Nitro+ follows the SELECT ALL waveform on JARGB2. Give the group the
        // calibrated palette and drive the other physical outputs with the original.
        var calibrateGpu = !rainbow && jargb2Colors is { Count: > 0 } && !jargb2Colors.SequenceEqual(effect.Colors);
        var groupPalette = calibrateGpu ? ActiveColors(effect.EffectId, jargb2Colors!) : palette;
        var colorCount = rainbow ? 4 : palette.Count;
        var group = Area(result, SelectAll);
        WriteArea(group, mode, groupPalette, colorCount, (byte)(options | 0x80));
        group[15] = 0; // SELECT ALL has no strip length.

        foreach (var index in GroupAreas)
        {
            var area = Area(result, index);
            if (calibrateGpu && index != Jargb2)
                WriteArea(area, mode, palette, colorCount, options);
            else
                // Mystic Light's Default record follows SELECT ALL. An old Advanced record can ignore the group.
                WriteArea(area, Wave, [], 4, 0x94);
        }
        WritePhysicalLengths(result, zones);
        if(ringZone is int ring)
        {
            if(input.EffectId!=RgbEffects.Wave||!IsRingZone(zones,ring))throw new ArgumentException("冷排闭环分区与灯效不匹配");
            WriteArea(Area(result,2),Advanced,DirectPalette,4,0x15);
        }
        return result;
    }

    private static IReadOnlyList<RgbColor> ActiveColors(string id, IReadOnlyList<RgbColor> colors)
    {
        if (id != RgbEffects.Breathing) return colors;
        if (colors.Count == 0 || colors.Any(color => color != colors[0]))
            throw new ArgumentException("四色呼吸需要逐灯播放");
        return [colors[0]];
    }

    internal static bool IsRingZone(IReadOnlyList<RgbZoneInfo> zones,int index) =>
        zones.Count(z=>z.Index==index)==1&&zones.Count(z=>z.Name=="JARGB 3")==1&&
        zones.Any(z=>z.Index==index&&z.Name=="JARGB 3"&&z.Type==RgbZoneType.Linear&&z.LedCount==68);

    internal static byte[] BuildRingPacket(IReadOnlyList<RgbZoneInfo> zones,int index,IReadOnlyList<RgbColor> frame)
    {
        if(!IsRingZone(zones,index)||frame.Count!=68)throw new ArgumentException("冷排逐灯帧必须匹配 JARGB 3 的 68 个周圈地址");
        var packet=new byte[727];packet[0]=0x51;packet[1]=9;packet[2]=4;packet[3]=2;packet[6]=68;
        for(int i=0;i<frame.Count;i++){packet[7+3*i]=frame[i].R;packet[8+3*i]=frame[i].G;packet[9+3*i]=frame[i].B;}
        return packet;
    }

    internal static byte[] BuildDirectReport(byte[] current, IReadOnlyList<RgbZoneInfo> zones)
    {
        var result = CopySettings(current);
        foreach (var zone in zones)
        {
            var index = zone.Name switch { "JARGB 1" => 0, "JARGB 2" => 1, "JARGB 3" => 2, "JAF" => 3, _ => -1 };
            if (index < 0) continue;
            var area = Area(result, index);
            WriteArea(area, Advanced, DirectPalette, 4, 0x15);
        }
        WritePhysicalLengths(result, zones);
        // OpenRGB's 761 controller has an empty SetDirectMode. Restore its SELECT ALL format explicitly.
        var group = Area(result, SelectAll);
        WriteArea(group, Advanced, DirectPalette, 4, 0x95);
        group[15] = 30;
        return result;
    }

    private static void WritePhysicalLengths(byte[] report, IReadOnlyList<RgbZoneInfo> zones)
    {
        var seen = 0;
        foreach (var zone in zones)
        {
            // SDK zone indices start with JAF; firmware area indices start with JARGB1.
            var index = zone.Name switch { "JARGB 1" => 0, "JARGB 2" => 1, "JARGB 3" => 2, "JAF" => 3, _ => -1 };
            if (index < 0) continue;
            if ((seen & (1 << index)) != 0 || zone.LedCount is < 0 or > 240)
                throw new ArgumentException("MSI 主板灯带分区信息无效");
            seen |= 1 << index;
            Area(report, index)[15] = (byte)zone.LedCount;
        }
        if (seen != 0x0F) throw new ArgumentException("MSI 主板缺少预期的 JAF 或 JARGB 分区");
    }

    private static byte[] CopySettings(byte[] current)
    {
        if (current.Length != ReportLength || current[0] != SettingsReport)
            throw new InvalidDataException("MSI 主板返回的 0x50 配置格式无效");
        var result = (byte[])current.Clone();
        result[^1] = 0; // Apply to RAM. SEEIN owns login restoration, so slider changes never write flash.
        return result;
    }

    private static Span<byte> Area(byte[] data, int index) => data.AsSpan(1 + index * AreaSize, AreaSize);

    private static void WriteArea(Span<byte> area, byte mode, IReadOnlyList<RgbColor> palette, int count, byte options)
    {
        area[0] = mode;
        area.Slice(1, 12).Clear();
        for (var i = 0; i < palette.Count; i++)
        {
            var color = palette[i];
            area[1 + 3 * i] = color.R;
            area[2 + 3 * i] = color.G;
            area[3 + 3 * i] = color.B;
        }
        area[13] = (byte)Math.Max(0, count - 1);
        area[14] = options;
    }
}
