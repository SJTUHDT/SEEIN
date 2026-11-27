using System.Buffers.Binary;

namespace Seein.Core.Protocol;

public static class Ds916Protocol
{
    public const int HeaderLength = 60;
    public const int VendorId = 0x33C3;
    public static readonly int[] ProductIds = [0xF101];

    public static byte[] CreateHeader(int jpegLength, bool firstFrame)
    {
        if (jpegLength <= 0) throw new ArgumentOutOfRangeException(nameof(jpegLength));

        var header = new byte[HeaderLength];
        header[0] = firstFrame ? (byte)0x03 : (byte)0x00;
        header[1] = 0x3C;
        header[5] = 0x06;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), checked((uint)(jpegLength + HeaderLength)));
        header[17] = 0x4F;
        header[18] = 0x54;
        header[19] = 0x06;
        header[24] = 0x1B;
        header[33] = 0x1B;
        ReadOnlySpan<byte> marker = [0x89, 0xB3, 0xFF, 0xFF, 0x00];
        marker.CopyTo(header.AsSpan(39));
        ReadOnlySpan<byte> command = [0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0x02, 0x03];
        command.CopyTo(header.AsSpan(44));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56, 4), checked((uint)jpegLength));
        return header;
    }

    public static byte[] CreatePacket(ReadOnlySpan<byte> jpeg, bool firstFrame)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new ArgumentException("Payload must be a JPEG image.", nameof(jpeg));

        var packet = new byte[HeaderLength + jpeg.Length];
        CreateHeader(jpeg.Length, firstFrame).CopyTo(packet, 0);
        jpeg.CopyTo(packet.AsSpan(HeaderLength));
        return packet;
    }
}

