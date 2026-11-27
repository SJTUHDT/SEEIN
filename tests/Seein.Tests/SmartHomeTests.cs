using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seein.SmartHome;

namespace Seein.Tests;

public sealed class SmartHomeTests
{
    private const string Token = "00112233445566778899aabbccddeeff";

    [Fact]
    public void MiioEncryptionMatchesAnIndependentPyCryptodomeFixture()
    {
        const string json = "{\"id\":7,\"method\":\"get_properties\",\"params\":[]}";
        var expected = Convert.FromHexString("2131005000000000000030390001093224cadca04ccca69f996db3893d41a17a7fe3a69e29c435227b660672ab46b6d94e9e43ba7253d4255da49ec16789601c2e44992a24ae6bfa9b86faec7e7ecc8d");
        var token = MiioPacket.ParseToken(Token);
        Assert.Equal(expected, MiioPacket.Encode(12345, 67890, json, token));
        Assert.Equal(json, MiioPacket.Decode(expected, token));
        expected[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => MiioPacket.Decode(expected, token));
    }

    [Fact]
    public void CloudSigningMatchesThePinnedIndependentPythonImplementation()
    {
        var fields = CloudCrypto.EncryptFields("/v2/homeroom/gethome", "{\"fg\":true}", Convert.FromBase64String("zONY8F9bTmx4KLLHeKz5YkjMJ5NxkIOQ7sovLsucOqA="), "security", "nonce");
        Assert.Equal("YbQQOw1NbBzjO9M=", fields["data"]);
        Assert.Equal("dNI0aH4WXhniG8lPAu4Lzynp9ujeL37NVytpLg==", fields["rc4_hash__"]);
        Assert.Equal("8z9jMmh5hAFoIFIcac2a0OBCHgo=", fields["signature"]);
    }

    [Theory]
    [InlineData("https://account.xiaomi.com/longPolling/loginUrl")]
    [InlineData("https://ak.lp.account.xiaomi.com/lp/s")]
    [InlineData("https://sts.api.io.mi.com/sts")]
    public void XiaomiAuthorizationAcceptsItsActualHttpsEndpoints(string address) => XiaomiCloudLogin.RequireXiaomiAddress(new(address));

    [Theory]
    [InlineData("http://account.xiaomi.com/login")]
    [InlineData("https://account.xiaomi.com.evil.example/login")]
    [InlineData("https://evil.example/account.xiaomi.com")]
    [InlineData("https://user:password@account.xiaomi.com/login")]
    public void XiaomiAuthorizationRejectsForeignAndInsecureRedirects(string address) => Assert.Throws<InvalidOperationException>(() => XiaomiCloudLogin.RequireXiaomiAddress(new(address)));

    [Theory]
    [InlineData("http://8.8.8.8/")]
    [InlineData("file:///C:/Windows/")]
    [InlineData("http://192.168.5.1/?password=secret")]
    [InlineData("http://admin:secret@192.168.5.1/")]
    [InlineData("http://example.com/")]
    public void RouterAddressStaysWithinTheLocalDeviceBoundary(string address) => Assert.Throws<ArgumentException>(() => LocalDeviceAddress.Router(address));

    [Fact]
    public void RouterNavigationDoesNotFollowOffDeviceLinks()
    {
        var router = LocalDeviceAddress.Router("192.168.5.1");
        Assert.True(LocalDeviceAddress.SameOrigin(router, "http://192.168.5.1/#/home"));
        Assert.False(LocalDeviceAddress.SameOrigin(router, "http://192.168.5.1:8080/"));
        Assert.False(LocalDeviceAddress.SameOrigin(router, "http://example.com/"));
    }

    [Fact]
    public void LampAuthorizationUsesWindowsUserEncryptionAndDoesNotExposeTheTokenInLabels()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Seein-SmartHome-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SmartHomeStore(directory);
            var authorization = new LampAuthorization("挂灯", XiaomiLampClient.Model, "192.168.5.10", Token);
            store.SaveAuthorization(authorization);
            Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(directory, "SmartHome", "lamp.authorization")));
            Assert.Equal(Token, store.LoadAuthorization()!.Token);
            Assert.DoesNotContain(Token, authorization.ToString());
            store.ForgetAuthorization();
            Assert.Null(store.LoadAuthorization());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NativeLampClientReadsAndWritesAgainstALocalUdpDevice()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var token = MiioPacket.ParseToken(Token);
        bool on = true;
        int brightness = 40, temperature = 3500;
        var service = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var packet = await server.ReceiveAsync(stop.Token);
                    if (packet.Buffer.SequenceEqual(MiioPacket.Hello()))
                    {
                        var hello = MiioPacket.Encode(12345, 67890, "{}", token)[..32];
                        hello[2] = 0; hello[3] = 32;
                        await server.SendAsync(hello, packet.RemoteEndPoint, stop.Token);
                        continue;
                    }
                    using var message = JsonDocument.Parse(MiioPacket.Decode(packet.Buffer, token));
                    var root = message.RootElement;
                    var method = root.GetProperty("method").GetString();
                    object result;
                    if (method == "miIO.info") result = new { model = XiaomiLampClient.Model };
                    else if (method == "get_properties") result = new object[] { new { siid = 2, piid = 1, code = 0, value = on }, new { siid = 2, piid = 2, code = 0, value = brightness }, new { siid = 2, piid = 3, code = 0, value = temperature } };
                    else
                    {
                        var acknowledgments = new List<object>();
                        foreach (var property in root.GetProperty("params").EnumerateArray())
                        {
                            var id = property.GetProperty("piid").GetInt32();
                            Assert.Equal(2, property.GetProperty("siid").GetInt32());
                            if (id == 1) on = property.GetProperty("value").GetBoolean();
                            if (id == 2) brightness = property.GetProperty("value").GetInt32();
                            if (id == 3) temperature = property.GetProperty("value").GetInt32();
                            acknowledgments.Add(new { siid = 2, piid = id, code = 0 });
                        }
                        result = acknowledgments;
                    }
                    var reply = JsonSerializer.Serialize(new { id = root.GetProperty("id").GetInt32(), result });
                    await server.SendAsync(MiioPacket.Encode(12345, 67890, reply, token), packet.RemoteEndPoint, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            using var client = new XiaomiLampClient(endpoint, Token);
            await client.VerifyModelAsync(stop.Token);
            Assert.Equal(new LampState(true, 40, 3500), await client.ReadAsync(stop.Token));
            await client.SetPowerAsync(false, stop.Token);
            await client.SetLightAsync(65, 4500, stop.Token);
            Assert.Equal(new LampState(false, 65, 4500), await client.ReadAsync(stop.Token));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetLightAsync(0, 4500, stop.Token));
        }
        finally { stop.Cancel(); await service; }
    }
}
