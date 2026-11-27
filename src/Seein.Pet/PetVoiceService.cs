using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Seein.Pet;

public sealed class PetVoiceService(string directory) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const string EdgeToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string EdgeVersion = "1-155.0.4283.45";

    public async Task<byte[]> SynthesizeAsync(string text, string voice, int rate, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 500) throw new InvalidDataException("语音文字长度应为 1–500 字符。");
        if (voice.StartsWith("edge/", StringComparison.Ordinal)) return await EdgeAsync(text, voice[5..], Math.Clamp(rate, -50, 100), token).ConfigureAwait(false);
        if (!voice.StartsWith("deepseek/", StringComparison.Ordinal)) throw new InvalidDataException("所选语音服务不可用。");
        var key = PetConfiguration.ReadObject(Path.Combine(directory, "standalone-ai.json")).Text("deepseekApiKey");
        if (key.Length == 0) throw new InvalidDataException("请配置 DeepSeek 语音密钥。");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/v1/tools/tts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(new JsonObject { ["text"] = text, ["voice_id"] = voice[9..], ["format"] = "wav" }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"语音合成失败，HTTP {(int)response.StatusCode}。");
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    public async Task<string> TranscribeAsync(byte[] audio, string provider, CancellationToken token)
    {
        if (provider != "deepseek") throw new InvalidDataException("当前独立桌宠未连接 DSH 语音识别插件。请配置 DeepSeek 识别服务。");
        if (audio.Length > 8 * 1024 * 1024) throw new InvalidDataException("录音超过 8 MiB。");
        var key = PetConfiguration.ReadObject(Path.Combine(directory, "standalone-ai.json")).Text("deepseekApiKey");
        if (key.Length == 0) throw new InvalidDataException("请配置 DeepSeek 语音密钥。");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/v1/tools/asr");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(audio);
        part.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(part, "file", "speech.wav");
        request.Content = form;
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"语音识别失败，HTTP {(int)response.StatusCode}。");
        var value = JsonNode.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        return value?["text"]?.GetValue<string>() ?? throw new InvalidDataException("语音识别响应无效。");
    }

    private static async Task<byte[]> EdgeAsync(string text, string voice, int rate, CancellationToken token)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(voice, @"\A[a-z]{2}-[A-Z]{2}-[A-Za-z]+Neural\z")) throw new InvalidDataException("音色无效。");
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L;
        var ticks = (seconds - seconds % 300) * 10000000L;
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticks.ToString(CultureInfo.InvariantCulture) + EdgeToken)));
        var id = Guid.NewGuid().ToString("N");
        var url = new Uri($"wss://api.msedgeservices.com/tts/cognitiveservices/websocket/v1?Ocp-Apim-Subscription-Key={EdgeToken}&Sec-MS-GEC={signature}&Sec-MS-GEC-Version={EdgeVersion}&ConnectionId={id}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(28));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        socket.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0");
        socket.Options.SetRequestHeader("Cookie", "MUID=" + Guid.NewGuid().ToString("N").ToUpperInvariant());
        await socket.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
        var stamp = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", CultureInfo.InvariantCulture);
        var config = "X-Timestamp:" + stamp + "\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":false,\"wordBoundaryEnabled\":false},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
        await socket.SendAsync(Encoding.UTF8.GetBytes(config).AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        var ssml = $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"zh-CN\"><voice name=\"{voice}\"><prosody rate=\"{rate:+0;-0;0}%\">{SecurityElement.Escape(text)}</prosody></voice></speak>";
        var request = $"X-RequestId:{id}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{stamp}\r\nPath:ssml\r\n\r\n{ssml}";
        await socket.SendAsync(Encoding.UTF8.GetBytes(request).AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        using var audio = new MemoryStream();
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close) break;
                if (message.Length + received.Count > 2 * 1024 * 1024) throw new InvalidDataException("语音帧过大。");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            var bytes = message.ToArray();
            if (received.MessageType == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(bytes).Contains("Path:turn.end", StringComparison.OrdinalIgnoreCase)) break;
            }
            else if (received.MessageType == WebSocketMessageType.Binary && bytes.Length >= 2)
            {
                var headerLength = (bytes[0] << 8) | bytes[1];
                if (headerLength + 2 > bytes.Length) throw new InvalidDataException("语音帧格式无效。");
                var header = Encoding.UTF8.GetString(bytes, 2, headerLength);
                if (header.Contains("Path:audio", StringComparison.OrdinalIgnoreCase)) audio.Write(bytes, headerLength + 2, bytes.Length - headerLength - 2);
                if (audio.Length > 8 * 1024 * 1024) throw new InvalidDataException("合成语音超过限制。");
            }
        }
        if (audio.Length == 0) throw new InvalidDataException("语音服务未返回音频。");
        return audio.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
