using Seein.Core.Protocol;
using System.Buffers.Binary;

namespace Seein.Tests;

public sealed class Ds916ProtocolTests
{
    [Fact]
    public void FirstFrameHeader_HasGoldenFields()
    {
        var header = Ds916Protocol.CreateHeader(0x123456, true);
        Assert.Equal(60, header.Length);
        Assert.Equal(0x03, header[0]);
        Assert.Equal(0x3C, header[1]);
        Assert.Equal(0x06, header[5]);
        Assert.Equal(0x123456u + 60u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4)));
        Assert.Equal(new byte[] { 0x4F, 0x54, 0x06 }, header[17..20]);
        Assert.Equal(0x1B, header[24]);
        Assert.Equal(0x1B, header[33]);
        Assert.Equal(new byte[] { 0x89, 0xB3, 0xFF, 0xFF, 0x00 }, header[39..44]);
        Assert.Equal(new byte[] { 0, 0, 0, 9, 0, 0, 1, 0, 3, 0, 2, 3 }, header[44..56]);
        Assert.Equal(0x123456u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(56, 4)));
    }

    [Fact]
    public void LaterFrame_UsesZeroType()
    {
        Assert.Equal(0, Ds916Protocol.CreateHeader(100, false)[0]);
    }

    [Fact]
    public void Packet_RejectsNonJpeg()
    {
        Assert.Throws<ArgumentException>(() => Ds916Protocol.CreatePacket([1, 2, 3, 4], true));
    }
}

