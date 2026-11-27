using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Seein.SmartHome;

public sealed record XiaomiQrCode(byte[] Image, Uri PollingAddress);

// QR/cloud wire compatibility adapted from Xiaomi-cloud-tokens-extractor (MIT),
// c4db715dace9806e905153c2977608873e8ab7c9. No Python runtime is required.
public sealed class XiaomiCloudLogin : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private string? _security, _userId, _serviceToken;
    private const string Agent = "SEEIN APP/com.xiaomi.mihome APPV/10.5.201";

    public XiaomiCloudLogin()
    {
        _http = new(new HttpClientHandler { CookieContainer = _cookies, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);
    }

    public async Task<XiaomiQrCode> BeginAsync(CancellationToken token)
    {
        var query = Query(new Dictionary<string, string>
        {
            ["_qrsize"] = "480", ["qs"] = "%3Fsid%3Dxiaomiio%26_json%3Dtrue", ["callback"] = "https://sts.api.io.mi.com/sts",
            ["_hasLogo"] = "false", ["sid"] = "xiaomiio", ["serviceParam"] = "", ["_locale"] = "zh_CN", ["_dc"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        });
        using var response = Parse(await GetAsync(new Uri("https://account.xiaomi.com/longPolling/loginUrl?" + query), token));
        var root = response.RootElement;
        if (!root.TryGetProperty("qr", out var qr) || !root.TryGetProperty("lp", out var lp)) throw new InvalidOperationException("暂时无法获取小米登录二维码，请稍后重试。");
        var imageAddress = new Uri(qr.GetString()!);
        var poll = new Uri(lp.GetString()!);
        RequireXiaomiAddress(poll);
        var bytes = await GetBytesAsync(imageAddress, token);
        return new(bytes, poll);
    }

    public async Task<IReadOnlyList<LampAuthorization>> CompleteAsync(XiaomiQrCode qr, string region, CancellationToken token)
    {
        if (!new[] { "cn", "de", "us", "ru", "tw", "sg", "in", "i2" }.Contains(region)) throw new ArgumentException("请选择米家账号所在地区。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        JsonDocument? login = null;
        try
        {
            while (login is null)
            {
                try { login = Parse(await GetAsync(qr.PollingAddress, deadline.Token)); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            }
            var data = login.RootElement;
            if (!data.TryGetProperty("ssecurity", out var security) || !data.TryGetProperty("location", out var location))
                throw new InvalidOperationException("二维码已失效或授权未完成，请重新扫码。");
            _security = security.GetString();
            _userId = data.GetProperty("userId").ToString();
            var finish = new Uri(location.GetString()!);
            await GetAsync(finish, deadline.Token);
            _serviceToken = _cookies.GetCookies(new Uri("https://api.io.mi.com/"))["serviceToken"]?.Value
                ?? _cookies.GetCookies(finish)["serviceToken"]?.Value;
            if (string.IsNullOrWhiteSpace(_serviceToken)) throw new InvalidOperationException("未取得小米设备授权，请重新扫码。");
            return await FindLampsAsync(region, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("小米二维码已过期，请重新扫码连接。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("小米授权服务暂时无法连接，请稍后重试。"); }
        finally { login?.Dispose(); }
    }

    private async Task<IReadOnlyList<LampAuthorization>> FindLampsAsync(string region, CancellationToken token)
    {
        using var homes = await ApiAsync(region, "/v2/homeroom/gethome", "{\"fg\":true,\"fetch_share\":true,\"fetch_share_dev\":true,\"limit\":300,\"app_ver\":7}", token);
        var result = new List<LampAuthorization>();
        if (!homes.RootElement.TryGetProperty("result", out var root) || !root.TryGetProperty("homelist", out var list))
            throw new InvalidOperationException("无法读取米家家庭，请确认账号地区并重试。");
        foreach (var home in list.EnumerateArray())
        {
            var parameters = JsonSerializer.Serialize(new { home_owner = long.Parse(_userId!, CultureInfo.InvariantCulture), home_id = long.Parse(home.GetProperty("id").ToString(), CultureInfo.InvariantCulture), limit = 200, get_split_device = true, support_smart_home = true });
            using var devices = await ApiAsync(region, "/v2/home/home_device_list", parameters, token);
            if (!devices.RootElement.TryGetProperty("result", out var items) || !items.TryGetProperty("device_info", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
            foreach (var device in entries.EnumerateArray())
            {
                if (!device.TryGetProperty("model", out var model) || model.GetString() != XiaomiLampClient.Model) continue;
                if (!device.TryGetProperty("token", out var credential) || credential.GetString() is not { Length: 32 } deviceToken) continue;
                MiioPacket.ParseToken(deviceToken);
                result.Add(new(device.TryGetProperty("name", out var name) ? name.GetString() ?? "米家显示器挂灯2" : "米家显示器挂灯2", model.GetString()!,
                    device.TryGetProperty("localip", out var ip) ? ip.GetString() ?? "" : "", deviceToken));
            }
        }
        return result;
    }

    private async Task<JsonDocument> ApiAsync(string region, string route, string data, CancellationToken token)
    {
        var host = region == "cn" ? "api.io.mi.com" : region + ".api.io.mi.com";
        var nonceBytes = RandomNumberGenerator.GetBytes(12);
        BinaryPrimitives.WriteUInt32BigEndian(nonceBytes.AsSpan(8), (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60000));
        var nonce = Convert.ToBase64String(nonceBytes);
        var signed = SHA256.HashData(Convert.FromBase64String(_security!).Concat(nonceBytes).ToArray());
        var fields = CloudCrypto.EncryptFields(route, data, signed, _security!, nonce);
        var uri = new Uri("https://" + host + "/app" + route + "?" + Query(fields));
        RequireXiaomiAddress(uri);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded") };
        request.Headers.Add("Cookie", "userId=" + _userId + "; serviceToken=" + _serviceToken + "; yetAnotherServiceToken=" + _serviceToken + "; locale=zh_CN; channel=MI_APP_STORE");
        request.Headers.Add("Accept-Encoding", "identity");
        request.Headers.Add("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
        request.Headers.Add("MIOT-ENCRYPT-ALGORITHM", "ENCRYPT-RC4");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("小米设备列表获取失败，请确认账号授权后重试。");
        var encrypted = Encoding.UTF8.GetString(await ReadBytesAsync(response, timeout.Token));
        return JsonDocument.Parse(CloudCrypto.Rc4Drop1024(signed, Convert.FromBase64String(encrypted)));
    }

    public static void RequireXiaomiAddress(Uri uri)
    {
        var host = uri.IdnHost;
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Port != 443 || !(host == "account.xiaomi.com" || host.EndsWith(".account.xiaomi.com", StringComparison.Ordinal) || host == "api.io.mi.com" || host.EndsWith(".api.io.mi.com", StringComparison.Ordinal)))
            throw new InvalidOperationException("小米授权返回了不受支持的地址。");
    }

    private async Task<string> GetAsync(Uri uri, CancellationToken token) => Encoding.UTF8.GetString(await GetBytesAsync(uri, token));
    private async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken token)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            RequireXiaomiAddress(uri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next) { uri = new Uri(uri, next); continue; }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("小米授权服务暂时不可用，请重新扫码。");
            return await ReadBytesAsync(response, timeout.Token);
        }
        throw new InvalidOperationException("小米授权跳转次数过多，请重试。");
    }

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var output = new MemoryStream();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("小米授权响应过大。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static JsonDocument Parse(string text) => JsonDocument.Parse(text.Replace("&&&START&&&", "", StringComparison.Ordinal));
    private static string Query(IEnumerable<KeyValuePair<string, string>> values) => string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    public void Dispose() { _security = _userId = _serviceToken = null; _http.Dispose(); }
}

public static class CloudCrypto
{
    public static Dictionary<string, string> EncryptFields(string route, string data, byte[] key, string security, string nonce)
    {
        var signed = Convert.ToBase64String(key);
        var fields = new Dictionary<string, string> { ["data"] = data };
        fields["rc4_hash__"] = Signature(route, fields, signed);
        foreach (var name in fields.Keys.ToArray()) fields[name] = Convert.ToBase64String(Rc4Drop1024(key, Encoding.UTF8.GetBytes(fields[name])));
        fields["signature"] = Signature(route, fields, signed);
        fields["ssecurity"] = security;
        fields["_nonce"] = nonce;
        return fields;
    }

    private static string Signature(string route, IEnumerable<KeyValuePair<string, string>> fields, string signed) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes("POST&" + route + "&" + string.Join("&", fields.Select(p => p.Key + "=" + p.Value)) + "&" + signed)));

    public static byte[] Rc4Drop1024(byte[] key, byte[] input)
    {
        if (key.Length == 0) throw new ArgumentException("授权密钥无效。");
        var state = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        int j = 0;
        for (var n = 0; n < 256; n++) { j = (j + state[n] + key[n % key.Length]) & 255; (state[n], state[j]) = (state[j], state[n]); }
        var result = new byte[input.Length];
        int i = 0; j = 0;
        for (var n = -1024; n < input.Length; n++)
        {
            i = (i + 1) & 255; j = (j + state[i]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
            var value = state[(state[i] + state[j]) & 255];
            if (n >= 0) result[n] = (byte)(input[n] ^ value);
        }
        return result;
    }
}
