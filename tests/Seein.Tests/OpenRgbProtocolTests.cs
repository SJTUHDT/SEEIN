using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Seein.Core.Lighting;
using static Seein.Core.Lighting.OpenRgbProtocol;

namespace Seein.Tests;

public sealed class OpenRgbProtocolTests
{
    internal const string SapphireV5 = "cwIAAAIAAAAiAFNhcHBoaXJlIFJhZGVvbiBSWCA5MDcwIFhUIE5pdHJvKwAJAFNhcHBoaXJlAB4AU2FwcGhpcmUgTml0cm8gR2xvdyBWMyBEZXZpY2UAAQAAAQAAGwBYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWAAHAAUAAAAHAFN0YXRpYwAGAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAA0AUmFpbmJvdyBXYXZlAAAAAAABAAAACgAAAPoAAAAAAAAAAAAAAAAAAAAAAAAAZAAAAAAAAAAAAAAAAAAAAAAABwBSdW53YXkAAQAAAAEAAAAFAAAAMgAAAAAAAAAAAAAAAAAAAAAAAAAFAAAAAAAAAAAAAAAAAAAAAAAPAFNwZWN0cnVtIEN5Y2xlAAIAAAABAAAAHgAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAAAAAAAAAAAAAAAAAAABwBTZXJpYWwAAwAAAAEAAAD/AAAABQAAAAAAAAAAAAAAAAAAAAAAAADIAAAAAAAAAAAAAAAAAAAAAAARAEV4dGVybmFsIENvbnRyb2wA/wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAE9mZgAHAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEACQBHUFUgWm9uZQAAAAAAAQAAAAEAAAABAAAAAAAAAAAAAAABAAgAR1BVIExFRAAAAAAAAQAA/wAAAAABAAAA";

    // Captured from the bundled 1.1 engine. Serial and location fields are redacted in place.
    [Theory]
    [InlineData(5, 0, SapphireV5)]
    [InlineData(5, 1, "jAEAAAAAAAARAE1TSSBNWVNUSUMgTElHSFQABABNU0kAIwBNU0kgTXlzdGljIExpZ2h0IERldmljZSAoNzYxLWJ5dGUpAAoAQVAvTEQgIC8gAA0AWFhYWFhYWFhYWFhYAFgAWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYAAEAAAAAAAcARGlyZWN0AGQAAAAgAAAAAAAAAAAAAAAKAAAACgAAAAAAAAAAAAAAAAAAAAoAAAAAAAAAAQAAAAAABAAEAEpBRgABAAAAAAAAAPAAAAAAAAAAAAAAAD4AAAAIAEpBUkdCIDEAAQAAAAAAAADwAAAAAAAAAAAAAAA+AAAACABKQVJHQiAyAAEAAAAAAAAA8AAAAAAAAAAAAAAAPgAAAAgASkFSR0IgMwABAAAAAAAAAPAAAAAAAAAAAAAAAD4AAAAAAAAAAAABAAAA")]
    [InlineData(6, 0, "ZAIAAAIAAAAiAFNhcHBoaXJlIFJhZGVvbiBSWCA5MDcwIFhUIE5pdHJvKwAJAFNhcHBoaXJlAB4AU2FwcGhpcmUgTml0cm8gR2xvdyBWMyBEZXZpY2UAAQAAAQAAGwBYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWAAHAAUAAAAHAFN0YXRpYwAgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAADQBSYWluYm93IFdhdmUAAQAAAAoAAAD6AAAAAAAAAAAAAAAAAAAAAAAAAGQAAAAAAAAAAAAAAAAAAAAAAAcAUnVud2F5AAEAAAAFAAAAMgAAAAAAAAAAAAAAAAAAAAAAAAAFAAAAAAAAAAAAAAAAAAAAAAAPAFNwZWN0cnVtIEN5Y2xlAAEAAAAeAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAHAFNlcmlhbAABAAAA/wAAAAUAAAAAAAAAAAAAAAAAAAAAAAAAyAAAAAAAAAAAAAAAAAAAAAAAEQBFeHRlcm5hbCBDb250cm9sAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAE9mZgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAJAEdQVSBab25lAAAAAAABAAAAAQAAAAEAAAAAAAAAAAAAAP////8AAAEAAAEACABHUFUgTEVEAAEAAP8AAAAAAQAAAAEAAAEAAAAA")]
    [InlineData(6, 1, "tAEAAAAAAAARAE1TSSBNWVNUSUMgTElHSFQABABNU0kAIwBNU0kgTXlzdGljIExpZ2h0IERldmljZSAoNzYxLWJ5dGUpAAoAQVAvTEQgIC8gAA0AWFhYWFhYWFhYWFhYAFgAWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYAAEAAAAAAAcARGlyZWN0ACAAAAAAAAAAAAAAAAoAAAAKAAAAAAAAAAAAAAAAAAAACgAAAAAAAAABAAAAAAAEAAQASkFGAAEAAAAAAAAA8AAAAAAAAAAAAAAAPgAAAP////8AAAEAAAgASkFSR0IgMQABAAAAAAAAAPAAAAAAAAAAAAAAAD4AAAD/////AAABAAAIAEpBUkdCIDIAAQAAAAAAAADwAAAAAAAAAAAAAAA+AAAA/////wAAAQAACABKQVJHQiAzAAEAAAAAAAAA8AAAAAAAAAAAAAAAPgAAAP////8AAAEAAAAAAAAAAAEAAAABAAABAAAAAA==")]
    public void BundledEngineControllerPayloadParses(int version, int index, string fixture)
    {
        var bytes = Convert.FromBase64String(fixture);
        var layout = ParseController(index, bytes, version);
        Assert.Equal(index, layout.Index);
        Assert.Matches(index == 0 ? "Sapphire.*9070 XT" : "MSI MYSTIC LIGHT", layout.Name);
        Assert.NotEmpty(layout.Zones);
        Assert.NotEmpty(layout.Modes);
        Assert.InRange(layout.ActiveModeIndex, 0, layout.Modes.Count - 1);
        if (index == 0)
        {
            var gpu = new RgbDeviceInfo(index, layout.Name, "GPU", layout.Vendor, layout.Zones, layout.Modes,
                layout.ActiveMode?.Name, layout.SupportsDirect, layout.LedCount);
            Assert.False(gpu.SupportsDirect);
            Assert.Equal("Static", gpu.SolidColorMode?.Name);
            Assert.True(gpu.CanSetSolidColor);
            Assert.Contains(gpu.Modes, mode => mode.Name == "Rainbow Wave");
            Assert.Contains(gpu.Modes, mode => mode.Name == "Runway");
            Assert.Null(RgbEffects.NativeAnimation(gpu, RgbEffects.Wave));
            Assert.Null(RgbEffects.NativeAnimation(gpu, RgbEffects.Marquee));
            Assert.Null(RgbEffects.NativeAnimation(gpu, RgbEffects.Breathing));
            var zone = Assert.Single(gpu.Zones);
            Assert.Equal((1, 1, 1), (zone.LedCount, zone.LedsMin, zone.LedsMax));
        }
        Assert.Throws<InvalidDataException>(() => ParseController(index, bytes.AsSpan(0, bytes.Length - 1), version));
    }

    [Fact]
    public void ControllerRetainsMatrixHolesSegmentsAndLedAliases()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0);
        writer.Write(5);
        foreach (var value in new[] { "Matrix", "", "", "", "", "" }) WriteString(value);
        writer.Write((ushort)0);
        writer.Write(-1);
        writer.Write((ushort)1);
        WriteString("Keys");
        writer.Write((int)RgbZoneType.Matrix);
        writer.Write(2);
        writer.Write(2);
        writer.Write(2);
        writer.Write((ushort)24);
        writer.Write(2);
        writer.Write(2);
        writer.Write(0u);
        writer.Write(uint.MaxValue);
        writer.Write(uint.MaxValue);
        writer.Write(1u);
        writer.Write((ushort)1);
        WriteString("Right");
        writer.Write((int)RgbZoneType.Single);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(0u);
        writer.Write((ushort)2);
        WriteString("A"); writer.Write(0u);
        WriteString("B"); writer.Write(1u);
        writer.Write((ushort)2);
        writer.Write(0x00030201u);
        writer.Write(0x00060504u);
        writer.Write((ushort)2);
        WriteString("Alpha");
        WriteString("Beta");
        writer.Write(8u);
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, bytes.Length);

        var layout = ParseController(3, bytes);
        var zone = Assert.Single(layout.Zones);
        Assert.Equal((2, 2), (zone.MatrixWidth, zone.MatrixHeight));
        Assert.Equal(new[] { 0u, uint.MaxValue, uint.MaxValue, 1u }, zone.Matrix);
        Assert.Equal(new RgbSegment("Right", RgbZoneType.Single, 1, 1), Assert.Single(zone.Segments!));
        Assert.Equal(new[] { "Alpha", "Beta" }, layout.LedAliases);
        Assert.Equal(8u, layout.Flags);
        Assert.Equal(new RgbColor(4, 5, 6), layout.Colors[1]);

        void WriteString(string value)
        {
            var encoded = BuildClientName(value);
            writer.Write((ushort)encoded.Length);
            writer.Write(encoded);
        }
    }

    [Fact]
    public void ColorPacketsMatchOpenRgbFieldLengthsAndOffsets()
    {
        Assert.Equal(Convert.FromHexString("0E00000002000102030004050600"),
            BuildUpdateLeds([new(1, 2, 3), new(4, 5, 6)]));
        Assert.Equal(Convert.FromHexString("0E00000004000000010009080700"),
            BuildUpdateZoneLeds(4, [new(9, 8, 7)]));
        Assert.Equal(Convert.FromHexString("070000000A141E00"),
            BuildUpdateSingleLed(7, new(10, 20, 30)));
        Assert.Equal(Convert.FromHexString("534545494E00"), BuildClientName("SEEIN"));
    }

    [Fact]
    public void ModePacketPreservesDirectionColorModeAndColors()
    {
        var mode = new RgbMode("X", 23, 32, 1, 2, 3, 4, 5, 6, 7, 8, 9, 1, [new(10, 11, 12)], 3);
        var v5 = BuildUpdateMode(mode, 5);
        var v6 = BuildUpdateMode(mode, 6);
        Assert.Equal(Convert.FromHexString(
            "420000000300000002005800170000002000000001000000020000000300000004000000" +
            "05000000060000000700000008000000090000000100000001000A0B0C00"), v5);
        Assert.Equal(Convert.FromHexString(
            "3E00000003000000020058002000000001000000020000000300000004000000" +
            "05000000060000000700000008000000090000000100000001000A0B0C00"), v6);
    }

    [Fact]
    public async Task V5WaitsForMatchedResponseAndRejectsStaleAddress()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var provider = new OpenRgbProvider(port, 5);
        var server = ServeAsync();
        var devices = await provider.RefreshAsync(token: token);
        Assert.Equal(2, devices.Count);
        Assert.Equal(devices[0].Info.Name, devices[1].Info.Name);
        Assert.NotEqual(devices[0].Info.Address, devices[1].Info.Address);
        var staleAddress = devices[0].Info.Address;
        devices = await provider.RefreshAsync(rescan: true, token: token);
        Assert.Equal(2, devices.Count);
        Assert.False((await provider.ApplySingleLedAsync(staleAddress, 0, new(1, 2, 3), token)).Success);
        await server;

        async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var stream = client.GetStream();
            Assert.Equal((0, RequestProtocol), await ReadRequestAsync(stream, Int32Payload(5), token));
            await ReplyAsync(stream, 0, RequestProtocol, Int32Payload(6), token);
            Assert.Equal((0, SetClientName), await ReadRequestAsync(stream, "SEEIN\0"u8.ToArray(), token));
            await EnumerateAsync();
            Assert.Equal((0, Rescan), await ReadRequestAsync(stream, [], token));
            await ReplyAsync(stream, 0, DeviceListUpdated, [], token);
            await EnumerateAsync();

            async Task EnumerateAsync()
            {
                Assert.Equal((0, ControllerCount), await ReadRequestAsync(stream, [], token));
                await ReplyAsync(stream, 0, ControllerCount, Int32Payload(2), token);
                for (var index = 0; index < 2; index++)
                {
                    Assert.Equal((index, ControllerData), await ReadRequestAsync(stream, Int32Payload(5), token));
                    await ReplyAsync(stream, 99, ControllerData, [7], token);
                    await ReplyAsync(stream, index, ControllerData,
                        Convert.FromBase64String(SapphireV5), token);
                }
            }
        }
    }

    private static async Task<(int Device, int Packet)> ReadRequestAsync(NetworkStream stream, byte[] expected, CancellationToken token)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, token);
        var (device, packet, length) = ParseHeader(header);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        Assert.Equal(expected, body);
        return (device, packet);
    }

    private static async Task ReplyAsync(NetworkStream stream, int device, int packet, byte[] data, CancellationToken token)
    {
        await stream.WriteAsync(BuildHeader(device, packet, data.Length), token);
        await stream.WriteAsync(data, token);
    }
}
