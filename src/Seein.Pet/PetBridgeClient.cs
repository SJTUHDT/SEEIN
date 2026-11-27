using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Seein.Pet;

public sealed record PetReply(string Text, string? Action, string? Meme);

// The pet is a Dafeiyu client. Model, provider, persona and account setup belong to AstrBot.
public sealed class PetBridgeClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _credential;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _conversation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _active;
    private string? _activePet;
    public PetMemory Memory { get; }

    public PetBridgeClient(string directory, string? dafeiyuRoot = null, Uri? localEndpoint = null, HttpMessageHandler? transport = null)
    {
        _endpoint = localEndpoint ?? new Uri("http://127.0.0.1:26199/v1/");
        if (!_endpoint.IsLoopback || _endpoint.Scheme != "http" || _endpoint.UserInfo.Length > 0 || _endpoint.Query.Length > 0 || _endpoint.Fragment.Length > 0)
            throw new InvalidDataException("桌宠只连接本机大肥鱼桥。");
        _credential = Path.Combine(Path.GetFullPath(dafeiyuRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dafeiyu")), "private", "pet-bridge-token.txt");
        _http = transport is null ? new HttpClient(new HttpClientHandler { UseProxy = false }) : new HttpClient(transport, false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        Memory = new(directory);
    }

    private string ReadCredential()
    {
        if (!File.Exists(_credential)) throw new InvalidDataException("找不到大肥鱼桥接凭据，请先在大肥鱼工作台检查桥接服务。");
        var token = File.ReadAllText(_credential).Trim();
        if (token.Length is 0 or > 8192 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new InvalidDataException("大肥鱼桥接凭据无效，请在大肥鱼工作台修复桥接。");
        return token;
    }

    public async Task<bool> CheckAsync(CancellationToken token)
    {
        var response = await SendAsync(HttpMethod.Get, "models", null, token).ConfigureAwait(false);
        return (response["data"] as JsonArray)?.Any(m => m?.Text("id") == "dafeiyu") == true;
    }

    public async Task<PetReply> ChatAsync(PetProfile pet, string text, string? imageDataUrl, CancellationToken token, bool whisper = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64000) throw new InvalidDataException("请输入有效的对话内容。");
        await _conversation.WaitAsync(token).ConfigureAwait(false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            lock (_sync) { _active = operation; _activePet = pet.Id; }
            token = operation.Token;
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var messages = new JsonArray();
            var rounds = (int)Math.Clamp(pet.EntrySettings.Number("chatMemoryRounds", 10), 0, 1000);
            if (!whisper)
                foreach (var item in Memory.Read(pet).TakeLast(rounds * 2).OfType<JsonObject>())
                    if (item.Text("role") is "user" or "assistant") messages.Add(new JsonObject { ["role"] = item.Text("role"), ["content"] = item.Text("content") });
            var memes = pet.EntrySettings["memes"] as JsonObject ?? new();
            var requestText = text;
            if (pet.EntrySettings.Flag(whisper ? "whisperImageEnabled" : "chatImageEnabled") && memes.Count > 0)
            {
                var limit = (int)Math.Clamp(pet.EntrySettings.Number("chatImageLimit", 20), 0, 10000);
                requestText += "\n\n[配图]\n" + string.Join('\n', memes.Take(limit == 0 ? memes.Count : limit).Select(m => m.Key + " " + m.Value));
            }
            var actions = (pet.EntrySettings["animations"]?["categories"] as JsonArray ?? []).OfType<JsonObject>()
                .SelectMany(c => (c["actions"] as JsonArray ?? []).OfType<JsonValue>()).Select(a => a.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            JsonNode content = JsonValue.Create(requestText)!;
            if (!string.IsNullOrEmpty(imageDataUrl))
            {
                if (imageDataUrl.Length > 8 * 1024 * 1024 || !Regex.IsMatch(imageDataUrl, @"\Adata:image/(png|jpeg|gif|webp);base64,[A-Za-z0-9+/=]+\z")) throw new InvalidDataException("图片附件无效或超过限制。");
                content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = requestText }, new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imageDataUrl } });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            var body = new JsonObject { ["model"] = "dafeiyu", ["messages"] = messages, ["stream"] = false };
            var response = await SendAsync(HttpMethod.Post, "chat/completions", body, token).ConfigureAwait(false);
            var reply = response["choices"]?[0]?["message"]?.Text("content");
            if (string.IsNullOrWhiteSpace(reply)) throw new InvalidDataException("大肥鱼桥未返回回复，请检查工作台中的运行状态。");
            var action = Regex.Match(reply, @"\[动作[:：]([^\]\r\n]+)\]");
            var meme = Regex.Match(reply, @"\[图[:：]([^\]\r\n]+)\]");
            var result = new PetReply(Regex.Replace(reply, @"\[(动作|图)[:：][^\]\r\n]+\]", "").Trim(),
                !whisper && pet.EntrySettings.Flag("chatAnimationEnabled") && action.Success && actions.Contains(action.Groups[1].Value) ? action.Groups[1].Value : null,
                pet.EntrySettings.Flag(whisper ? "whisperImageEnabled" : "chatImageEnabled") && meme.Success && memes.ContainsKey(meme.Groups[1].Value) ? meme.Groups[1].Value : null);
            if (!whisper)
            {
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = result.Text, ["ts"] = now };
                if (result.Action is not null) assistant["action"] = result.Action;
                if (result.Meme is not null) assistant["meme"] = result.Meme;
                Memory.Append(pet, new JsonObject { ["role"] = "user", ["content"] = text, ["ts"] = now }, assistant, token);
            }
            return result;
        }
        finally
        {
            lock (_sync) { _active = null; _activePet = null; }
            _conversation.Release();
        }
    }

    public void ClearMemory(PetProfile pet, IReadOnlySet<long>? timestamps = null)
    {
        lock (_sync) { if (_activePet == pet.Id) _active?.Cancel(); Memory.Clear(pet, timestamps); }
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(95));
        using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadCredential());
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"大肥鱼桥接请求失败，HTTP {(int)response.StatusCode}。请检查大肥鱼工作台。", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
        {
            if (data.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("大肥鱼桥接响应过大。");
            data.Write(buffer, 0, count);
        }
        return JsonNode.Parse(data.ToArray()) ?? throw new InvalidDataException("大肥鱼桥接响应为空。");
    }

    public void Dispose()
    {
        lock (_sync) { _lifetime.Cancel(); _active?.Cancel(); }
        _http.Dispose();
        _lifetime.Dispose();
    }
}
