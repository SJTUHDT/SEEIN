using System.Buffers.Binary;
using System.Text;

namespace Seein.Core.Lighting;

/// <summary>Wire layouts for OpenRGB SDK 5 and 6.</summary>
public static class OpenRgbProtocol
{
    public const int MinimumVersion = 5;
    public const int ProtocolVersion = 6;
    public const int ControllerCount = 0;
    public const int ControllerData = 1;
    public const int Ack = 10;
    public const int RequestProtocol = 40;
    public const int SetClientName = 50;
    public const int SetClientFlags = 52;
    public const int DeviceListUpdated = 100;
    public const int ScanStarted = 101;
    public const int ScanComplete = 103;
    public const int Rescan = 140;
    public const int ResizeZone = 1000;
    public const int UpdateLeds = 1050;
    public const int UpdateZoneLeds = 1051;
    public const int UpdateSingleLed = 1052;
    public const int SetCustomMode = 1100;
    public const int UpdateMode = 1101;

    public const int HeaderSize = 16;
    public const int MaxPayload = 4 * 1024 * 1024;

    public static byte[] BuildHeader(int deviceId, int packetId, int length)
    {
        var header = new byte[HeaderSize];
        "ORGB"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), deviceId);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), packetId);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), length);
        return header;
    }

    public static (int DeviceId, int PacketId, int Length) ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize || !header[..4].SequenceEqual("ORGB"u8))
            throw new InvalidDataException("OpenRGB 包头无效");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (length is < 0 or > MaxPayload) throw new InvalidDataException($"OpenRGB 包长度无效 ({length})");
        return (BinaryPrimitives.ReadInt32LittleEndian(header[4..]), BinaryPrimitives.ReadInt32LittleEndian(header[8..]), length);
    }

    public static byte[] Int32Payload(int value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, value);
        return payload;
    }

    public static byte[] BuildClientName(string name) => Encoding.UTF8.GetBytes(name + '\0');

    public static byte[] BuildUpdateLeds(IReadOnlyList<RgbColor> colors) => BuildFrame(colors, null);

    public static byte[] BuildUpdateZoneLeds(int zone, IReadOnlyList<RgbColor> colors) => BuildFrame(colors, zone);

    private static byte[] BuildFrame(IReadOnlyList<RgbColor> colors, int? zone)
        => BuildSized(writer =>
        {
            if (zone is int index) writer.Write(index);
            WriteColors(writer, colors);
        });

    public static byte[] BuildUpdateSingleLed(int led, RgbColor color)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, led);
        payload[4] = color.R;
        payload[5] = color.G;
        payload[6] = color.B;
        return payload;
    }

    public static byte[] BuildUpdateMode(RgbMode mode, int version)
        => BuildSized(writer =>
        {
            writer.Write(mode.Index);
            var name = BuildClientName(mode.Name);
            writer.Write(checked((ushort)name.Length));
            writer.Write(name);
            if (version == 5) writer.Write(mode.Value);
            writer.Write(mode.Flags);
            writer.Write(mode.SpeedMin);
            writer.Write(mode.SpeedMax);
            writer.Write(mode.BrightnessMin);
            writer.Write(mode.BrightnessMax);
            writer.Write(mode.ColorsMin);
            writer.Write(mode.ColorsMax);
            writer.Write(mode.Speed);
            writer.Write(mode.Brightness);
            writer.Write(mode.Direction);
            writer.Write(mode.ColorMode);
            WriteColors(writer, mode.Colors);
        });

    public static byte[] BuildResizeZone(int zone, int size)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, zone);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), size);
        return payload;
    }

    private static byte[] BuildSized(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0);
        write(writer);
        var payload = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(payload, payload.Length);
        return payload;
    }

    private static void WriteColors(BinaryWriter writer, IReadOnlyList<RgbColor> colors)
    {
        writer.Write(checked((ushort)colors.Count));
        foreach (var color in colors)
        {
            writer.Write(color.R);
            writer.Write(color.G);
            writer.Write(color.B);
            writer.Write((byte)0);
        }
    }

    public static RgbDeviceLayout ParseController(int deviceId, ReadOnlySpan<byte> payload, int protocol = 5)
    {
        var reader = new PacketReader(payload);
        if (reader.UInt32() != payload.Length) throw reader.Error("设备数据长度不符");
        var type = reader.Int32();
        var name = reader.String();
        var vendor = reader.String();
        var description = reader.String();
        var version = reader.String();
        var serial = reader.String();
        var location = reader.String();

        var count = reader.UInt16();
        var active = reader.Int32();
        var modes = ReadModes(ref reader, protocol, count);
        var zones = new RgbZoneInfo[reader.UInt16()];
        for (var i = 0; i < zones.Length; i++)
        {
            var zoneName = reader.String();
            var zoneType = (RgbZoneType)reader.Int32();
            var min = reader.Int32();
            var max = reader.Int32();
            var leds = reader.Int32();
            if (min < 0 || max < min || leds is < 0 or > ushort.MaxValue)
                throw reader.Error("分区灯珠数量无效");
            var (width, height, map) = ReadMatrix(ref reader);
            var segments = new RgbSegment[reader.UInt16()];
            for (var j = 0; j < segments.Length; j++)
            {
                var segmentName = reader.String();
                var segmentType = (RgbZoneType)reader.Int32();
                var start = reader.UInt32();
                var length = reader.UInt32();
                var segmentMap = protocol >= 6 ? ReadMatrix(ref reader).Map : null;
                var segmentFlags = protocol >= 6 ? reader.UInt32() : 0;
                segments[j] = new RgbSegment(segmentName, segmentType, start, length, segmentMap, segmentFlags);
            }
            var flags = reader.UInt32();
            var zoneActiveMode = protocol >= 6 ? reader.Int32() : -1;
            var zoneModes = protocol >= 6 ? ReadModes(ref reader, protocol, reader.UInt16()) : null;
            var zoneDisplayName = protocol >= 6 ? reader.String() : null;
            zones[i] = new RgbZoneInfo(i, zoneName, zoneType, leds, min, max, width, height, map, segments,
                flags, zoneActiveMode, zoneModes, zoneDisplayName);
        }

        var ledList = new RgbLedInfo[reader.UInt16()];
        for (var i = 0; i < ledList.Length; i++)
            ledList[i] = new RgbLedInfo(i, reader.String(), protocol == 5 ? reader.UInt32() : 0);
        var colors = reader.Colors();
        var aliases = new string[reader.UInt16()];
        for (var i = 0; i < aliases.Length; i++) aliases[i] = reader.String();
        var controllerFlags = reader.UInt32();
        var displayName = protocol >= 6 ? reader.String() : null;
        if (protocol >= 6) reader.String(checked((int)reader.UInt32()));
        if (reader.Remaining != 0) throw reader.Error("设备数据存在多余字节");
        return new RgbDeviceLayout(deviceId, type, name, vendor, description, version, serial, location, modes,
            zones, ledList, colors, active, aliases, controllerFlags, displayName);
    }

    private static RgbMode[] ReadModes(ref PacketReader reader, int protocol, int count)
    {
        var modes = new RgbMode[count];
        for (var i = 0; i < modes.Length; i++)
        {
            var name = reader.String();
            var value = protocol == 5 ? reader.Int32() : i;
            modes[i] = new RgbMode(name, value, reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.UInt32(),
                reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.UInt32(),
                reader.UInt32(), reader.Colors(), i);
        }
        return modes;
    }

    private static (int Width, int Height, uint[]? Map) ReadMatrix(ref PacketReader reader)
    {
        var length = reader.UInt16();
        if (length == 0) return (0, 0, null);
        var matrix = new PacketReader(reader.Take(length));
        var height = matrix.Int32();
        var width = matrix.Int32();
        if (height <= 0 || width <= 0 || (long)height * width * 4 != matrix.Remaining)
            throw reader.Error("矩阵尺寸与映射长度不符");
        var map = new uint[width * height];
        for (var i = 0; i < map.Length; i++) map[i] = matrix.UInt32();
        return (width, height, map);
    }

    private ref struct PacketReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;
        public int Remaining => _data.Length - _offset;
        public InvalidDataException Error(string message) => new($"OpenRGB {message}（偏移 {_offset}）");

        public ReadOnlySpan<byte> Take(int length)
        {
            if ((uint)length > Remaining) throw Error("数据截断");
            var result = _data.Slice(_offset, length);
            _offset += length;
            return result;
        }

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public string String() => String(UInt16());

        public string String(int length)
        {
            var value = Take(length);
            if (value.IsEmpty || value[^1] != 0) throw Error("字符串缺少结束符");
            return Encoding.UTF8.GetString(value[..^1]);
        }

        public RgbColor[] Colors()
        {
            var count = UInt16();
            var bytes = Take(count * 4);
            var colors = new RgbColor[count];
            for (var i = 0; i < count; i++) colors[i] = new RgbColor(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
            return colors;
        }
    }
}
