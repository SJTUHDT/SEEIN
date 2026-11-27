using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Seein.SmartHome;

// Standard miIO framing: 16-byte header, MD5 authentication, AES-128-CBC payload.
public static class MiioPacket
{
    public static byte[] Hello()
    {
        var packet = Enumerable.Repeat((byte)255, 32).ToArray();
        packet[0] = 0x21; packet[1] = 0x31; packet[2] = 0; packet[3] = 32;
        return packet;
    }

    public static byte[] ParseToken(string token)
    {
        if (token.Length != 32 || token.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("设备授权无效，请重新扫码连接。");
        return Convert.FromHexString(token);
    }

    public static (uint Device, uint Timestamp) Header(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 32 || packet[0] != 0x21 || packet[1] != 0x31 || BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) != packet.Length)
            throw new InvalidDataException("设备返回了无效报文。");
        return (BinaryPrimitives.ReadUInt32BigEndian(packet[8..]), BinaryPrimitives.ReadUInt32BigEndian(packet[12..]));
    }

    public static byte[] Encode(uint device, uint timestamp, string json, byte[] token)
    {
        using var aes = Cipher(token);
        var payload = aes.EncryptCbc(Encoding.UTF8.GetBytes(json), aes.IV);
        if (payload.Length > 60000) throw new ArgumentException("设备命令过大。");
        var packet = new byte[32 + payload.Length];
        packet[0] = 0x21; packet[1] = 0x31;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), device);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), timestamp);
        payload.CopyTo(packet, 32);
        Checksum(packet, token).CopyTo(packet, 16);
        return packet;
    }

    public static string Decode(byte[] packet, byte[] token)
    {
        Header(packet);
        if (!CryptographicOperations.FixedTimeEquals(packet.AsSpan(16, 16), Checksum(packet, token)))
            throw new CryptographicException("设备授权校验失败，请重新扫码连接。");
        using var aes = Cipher(token);
        return Encoding.UTF8.GetString(aes.DecryptCbc(packet.AsSpan(32), aes.IV)).TrimEnd('\0');
    }

    private static byte[] Checksum(byte[] packet, byte[] token)
    {
        var authenticated = (byte[])packet.Clone();
        token.CopyTo(authenticated, 16);
        return MD5.HashData(authenticated);
    }

    private static Aes Cipher(byte[] token)
    {
        if (token.Length != 16) throw new ArgumentException("设备授权无效。");
        var aes = Aes.Create();
        aes.Key = MD5.HashData(token);
        aes.IV = MD5.HashData(aes.Key.Concat(token).ToArray());
        return aes;
    }
}
