using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Seein.SmartHome;

public sealed record LampState(bool IsOn, int Brightness, int ColorTemperature);

public sealed class XiaomiLampClient : IDisposable
{
    public const string Model = "xiaomi.light.bar2";
    private readonly IPEndPoint _endpoint;
    private readonly byte[] _token;
    private readonly SemaphoreSlim _gate = new(1);
    private int _requestId = RandomNumberGenerator.GetInt32(1000, 1000000);

    public XiaomiLampClient(string address, string token) : this(new IPEndPoint(LocalDeviceAddress.Parse(address), 54321), token) { }
    public XiaomiLampClient(IPEndPoint endpoint, string token) { _endpoint = endpoint; _token = MiioPacket.ParseToken(token); }

    public async Task VerifyModelAsync(CancellationToken token)
    {
        var info = await CallAsync("miIO.info", Array.Empty<object>(), token);
        if (!info.TryGetProperty("model", out var model) || model.GetString() != Model)
            throw new InvalidOperationException("这个地址不是米家显示器挂灯2，请检查设备 IP。");
    }

    public async Task<LampState> ReadAsync(CancellationToken token)
    {
        var response = await CallAsync("get_properties", new[] { Property(1), Property(2), Property(3) }, token);
        JsonElement Value(int property)
        {
            var item = response.EnumerateArray().FirstOrDefault(p => p.GetProperty("siid").GetInt32() == 2 && p.GetProperty("piid").GetInt32() == property);
            if (item.ValueKind != JsonValueKind.Object || item.GetProperty("code").GetInt32() != 0 || !item.TryGetProperty("value", out var value))
                throw new InvalidOperationException("挂灯未返回完整状态，请重试。");
            return value;
        }
        return new(Value(1).GetBoolean(), Value(2).GetInt32(), Value(3).GetInt32());
    }

    public Task SetPowerAsync(bool on, CancellationToken token) => SetAsync(new[] { new { did = "seein", siid = 2, piid = 1, value = on } }, token);
    public Task SetLightAsync(int brightness, int temperature, CancellationToken token)
    {
        if (brightness is < 1 or > 100 || temperature is < 2700 or > 6500) throw new ArgumentOutOfRangeException(nameof(brightness), "亮度或色温超出范围。");
        return SetAsync(new[] { new { did = "seein", siid = 2, piid = 2, value = brightness }, new { did = "seein", siid = 2, piid = 3, value = temperature } }, token);
    }

    private static object Property(int id) => new { did = "seein", siid = 2, piid = id };
    private async Task SetAsync(object properties, CancellationToken token)
    {
        var result = await CallAsync("set_properties", properties, token);
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0 || result.EnumerateArray().Any(p => p.GetProperty("code").GetInt32() != 0))
            throw new InvalidOperationException("挂灯未确认设置，请刷新后重试。");
    }

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var udp = new UdpClient(_endpoint.AddressFamily);
            udp.Connect(_endpoint);
            await udp.SendAsync(MiioPacket.Hello(), deadline.Token);
            var hello = await udp.ReceiveAsync(deadline.Token);
            var header = MiioPacket.Header(hello.Buffer);
            var id = ++_requestId;
            var message = JsonSerializer.Serialize(new { id, method, @params = parameters });
            await udp.SendAsync(MiioPacket.Encode(header.Device, header.Timestamp, message, _token), deadline.Token);
            while (true)
            {
                var reply = await udp.ReceiveAsync(deadline.Token);
                using var document = JsonDocument.Parse(MiioPacket.Decode(reply.Buffer, _token));
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var returned) || returned.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("挂灯拒绝了命令，可能需要重新授权或更新兼容性。");
                return root.GetProperty("result").Clone();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("挂灯没有响应，请检查电源、IP 和网络连接。"); }
        finally { _gate.Release(); }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_token);
}
