using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Seein.Pet;

namespace Seein.PetHost;

internal sealed class PetEngine : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<PetWindow> _windows = [];
    private readonly List<PetChatWindow> _chats = [];
    private readonly Dictionary<string, long> _lastWhisper = [];
    private readonly HashSet<string> _generating = [];
    private long _lastTick = Environment.TickCount64;
    private long _lastSchedule;
    private bool _hidden;
    private bool _rendering;
    private long _lastRender;
    private TimeSpan? _lastRenderingTime;
    private readonly List<double> _motionIntervals = [];
    public event Action? SettingsRequested;
    private readonly System.Windows.Media.MediaPlayer _speech = new();
    private readonly SemaphoreSlim _voiceQueue = new(1, 1);
    private CancellationTokenSource _speechLifetime = new();
    public PetConfiguration Configuration { get; }
    public PetBridgeClient Bridge { get; }
    public PetMemory Memory { get; }
    public PetVoiceService Voice { get; }
    public string Home { get; }
    public string Assets { get; }
    public CancellationToken Lifetime => _lifetime.Token;
    public PetWindow? WindowFor(string id) => _windows.FirstOrDefault(w => w.Profile.Id == id);

    public PetEngine(string home, string assets, Dispatcher dispatcher)
    {
        Home = home;
        Assets = assets;
        Configuration = new(home, assets);
        Bridge = new(Configuration.DirectoryPath, Environment.GetEnvironmentVariable("SEEIN_DAFEIYU_ROOT"));
        Voice = new(Configuration.DirectoryPath);
        Memory = Bridge.Memory;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += Tick;
    }

    public void Reload()
    {
        var pets = Configuration.ReadPets();
        var replacement = new List<PetWindow>();
        try
        {
            foreach (var profile in pets.Where(p => p.Display is "desktop" or "both")) replacement.Add(new(profile, this));
        }
        catch { foreach (var item in replacement) item.Close(); throw; }
        foreach (var window in _windows) _ = window.ShutdownAsync();
        _windows.Clear();
        _windows.AddRange(replacement);
        foreach (var chat in _chats.ToArray())
        {
            var pet = _windows.FirstOrDefault(p => p.Profile.Id == chat.PetId);
            if (pet is null) chat.Close(); else chat.Rebind(pet);
        }
        foreach (var window in _windows) if (!_hidden) window.Resume();
        _timer.Start();
    }

    private void Tick(object? sender, EventArgs args)
    {
        var now = Environment.TickCount64;
        var delta = (now - _lastTick) / 1000.0;
        _lastTick = now;
        if (!_rendering) AdvanceMotion(delta);
        _timer.Interval = _windows.Any(w => w.IsVisible) ? TimeSpan.FromMilliseconds(33) : TimeSpan.FromSeconds(1);
        if (now - _lastSchedule < 1000) return;
        _lastSchedule = now;
        Schedule(now);
    }

    private void AdvanceMotion(double delta)
    {
        foreach (var window in _windows) window.Tick(delta);
        for (var first = 0; first < _windows.Count; first++)
            for (var second = first + 1; second < _windows.Count; second++)
            {
                var a = _windows[first];
                var b = _windows[second];
                if (!a.IsVisible || !b.IsVisible || a.IsDragging || b.IsDragging
                    || a.Profile.EntrySettings["physics"]?.Flag("petCollision", true) == false || b.Profile.EntrySettings["physics"]?.Flag("petCollision", true) == false) continue;
                PetMotion.Collide(a.Motion, a.Width, b.Motion, b.Width, .995);
            }
    }

    public void StartMotionClock()
    {
        if (_rendering || _lifetime.IsCancellationRequested) return;
        _rendering = true;
        _lastRender = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastRenderingTime = null;
        _motionIntervals.Clear();
        System.Windows.Media.CompositionTarget.Rendering += RenderMotion;
    }

    private void RenderMotion(object? sender, EventArgs args)
    {
        if (args is System.Windows.Media.RenderingEventArgs rendering)
        {
            if (_lastRenderingTime == rendering.RenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastRender, now);
        if (elapsed.TotalMilliseconds < 1) return;
        _lastRender = now;
        if (_motionIntervals.Count < 6000) _motionIntervals.Add(elapsed.TotalMilliseconds);
        AdvanceMotion(elapsed.TotalSeconds);
        if (!_windows.Any(w => w.IsVisible && w.IsMoving)) StopMotionClock();
    }

    private void StopMotionClock()
    {
        if (!_rendering) return;
        System.Windows.Media.CompositionTarget.Rendering -= RenderMotion;
        _rendering = false;
        _lastTick = Environment.TickCount64;
        if (Environment.GetEnvironmentVariable("SEEIN_PET_TRACE_MOTION") == "1" && _motionIntervals.Count > 0)
        {
            var frames = _motionIntervals.Order().ToArray();
            var report = new JsonObject { ["frames"] = frames.Length, ["meanMs"] = frames.Average(), ["p95Ms"] = frames[(int)((frames.Length - 1) * .95)], ["maxMs"] = frames[^1] };
            PetConfiguration.WriteObject(Path.Combine(Home, "native-logs", "motion-diagnostics.json"), report);
        }
    }

    private void Schedule(long now)
    {
        var main = Configuration.ReadEntries()["main"]!;
        var legacy = PetConfiguration.ReadObject(Path.Combine(Configuration.DirectoryPath, "standalone-ai.json"));
        if (!main.Flag("automaticWhispers", legacy.Flag("automaticWhispers"))) return;
        foreach (var window in _windows)
        {
            if (!window.IsVisible || !window.Profile.Settings.Flag("whisperEnabled") || _generating.Contains(window.Profile.Id)) continue;
            var interval = window.IsSleeping ? window.Profile.EntrySettings["eventsRefreshSec"]?.Number("sleepTalk", 60) ?? 60
                : window.Profile.EntrySettings["eventsRefreshSec"]?.Number("whisper", legacy.Number("whisperIntervalSec", 300)) ?? 300;
            if (!_lastWhisper.TryGetValue(window.Profile.Id, out var last)) _lastWhisper[window.Profile.Id] = now;
            else if (now - last >= interval * 1000) { _lastWhisper[window.Profile.Id] = now; _ = WhisperAsync(window); }
        }
    }

    public async Task WhisperAsync(PetWindow window, string? image = null)
    {
        if (!_generating.Add(window.Profile.Id)) return;
        try
        {
            if (!window.IsSleeping && image is null && window.Profile.EntrySettings.Flag("whisperImageEnabled"))
            {
                var names = PetPictureWindow.Names(this, window.Profile).ToArray();
                if (names.Length > 0) image = names[Random.Shared.Next(names.Length)];
            }
            var prompt = window.IsSleeping ? "你在睡觉，说一句简短梦话。" : "随便说一句日常碎碎念，简短自然。";
            if (image is not null) prompt += "这次配图是“" + image + "”，说一句与图片相配的话。";
            var reply = await Bridge.ChatAsync(window.Profile, prompt, null, Lifetime, true);
            window.Say(reply.Text);
            var picture = window.IsSleeping ? null : image ?? reply.Meme;
            if (!string.IsNullOrEmpty(picture)) ShowPicture(window, picture);
            if (!window.IsSleeping && window.Profile.EntrySettings.Flag("voiceWhisperEnabled")) _ = SpeakAsync(window, reply.Text, true);
            if (!window.IsSleeping) window.PlayEvent("whisper");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_lifetime.IsCancellationRequested) window.Say(ex is OperationCanceledException ? "对话请求超时。" : ex.Message); }
        finally { _generating.Remove(window.Profile.Id); }
    }

    public void ShowPicture(PetWindow pet, string name)
    {
        var path = PetPictureWindow.Find(this, pet.Profile, name);
        if (path is null) { pet.Say("找不到配图。" ); return; }
        try { new PetPictureWindow(path, pet).Show(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { pet.Say(ex.Message); }
    }

    public async Task SpeakAsync(PetWindow window, string text, bool force = false)
    {
        if ((!force && !window.Profile.EntrySettings.Flag("voiceEnabled")) || _lifetime.IsCancellationRequested) return;
        try
        {
            using var speech = CancellationTokenSource.CreateLinkedTokenSource(Lifetime, _speechLifetime.Token);
            var token = speech.Token;
            await _voiceQueue.WaitAsync(token);
            try
            {
                var voice = window.Profile.EntrySettings.Text("voiceName", "edge/zh-CN-XiaoyiNeural");
                text = text[..Math.Min(500, text.Length)];
                var folder = Path.Combine(Home, "native-voice-cache");
                Directory.CreateDirectory(folder);
                var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(voice + "\0" + text))) + (voice.StartsWith("edge/") ? ".mp3" : ".wav");
                var path = Path.Combine(folder, name);
                if (!File.Exists(path))
                {
                    var bytes = await Voice.SynthesizeAsync(text, voice, 0, token);
                    token.ThrowIfCancellationRequested();
                    File.WriteAllBytes(path, bytes);
                }
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler ended = (_, _) => finished.TrySetResult();
                EventHandler<System.Windows.Media.ExceptionEventArgs> failed = (_, e) => finished.TrySetException(e.ErrorException);
                _speech.MediaEnded += ended;
                _speech.MediaFailed += failed;
                try
                {
                    _speech.Volume = Math.Clamp(window.Profile.EntrySettings.Number("voiceVolume", 1), 0, 1);
                    _speech.Open(new Uri(path));
                    _speech.Play();
                    await finished.Task.WaitAsync(TimeSpan.FromSeconds(120), token);
                }
                finally { _speech.Stop(); _speech.Close(); _speech.MediaEnded -= ended; _speech.MediaFailed -= failed; }
                long keptBytes = 0;
                var kept = 0;
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    keptBytes += file.Length;
                    if (++kept > 64 || keptBytes > 16 * 1024 * 1024) file.Delete();
                }
            }
            finally { _voiceQueue.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_lifetime.IsCancellationRequested && ex is not OperationCanceledException) window.Say("语音播放失败。" + ex.Message);
        }
    }

    public void StopSpeech()
    {
        var previous = _speechLifetime;
        _speechLifetime = new();
        previous.Cancel();
        previous.Dispose();
        _speech.Stop();
    }

    public void OpenSettings()
    {
        SettingsRequested?.Invoke();
    }

    public void OpenChat(PetWindow window)
    {
        var existing = _chats.FirstOrDefault(c => c.PetId == window.Profile.Id);
        if (existing is not null) { existing.Activate(); return; }
        var chat = new PetChatWindow(window, this);
        _chats.Add(chat);
        chat.Closed += (_, _) => _chats.Remove(chat);
        chat.Show();
    }

    public void OpenCaptureChat(PetWindow window)
    {
        OpenChat(window);
        var chat = _chats.First(c => c.PetId == window.Profile.Id);
        _ = chat.CaptureAsync();
    }

    public IReadOnlyList<PetWindow> PauseForCapture()
    {
        var visible = _windows.Where(w => w.IsVisible).ToArray();
        foreach (var window in visible) window.Pause();
        return visible;
    }

    public void ResumeAfterCapture(IReadOnlyList<PetWindow> windows)
    {
        foreach (var window in windows.Where(w => _windows.Contains(w))) window.Resume();
    }

    public void HidePet(PetWindow window)
    {
        Configuration.UpdatePet(window.Profile.Id, p => p["display"] = "none");
        window.Pause();
    }

    public async Task<JsonNode?> ExecuteAsync(JsonObject message, CancellationToken token)
    {
        switch (message.Text("op"))
        {
            case "status": return new JsonObject { ["version"] = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(PetEngine).Assembly)?.InformationalVersion.Split('+')[0], ["protocolVersion"] = 1, ["engine"] = "native", ["locale"] = "zh-CN", ["windows"] = _windows.Count, ["hidden"] = _hidden };
            case "bridgeStatus": return new JsonObject { ["connected"] = await Bridge.CheckAsync(token) };
            case "capabilities": return new JsonObject { ["protocolVersion"] = 1, ["engine"] = "native", ["settings"] = "embedded", ["ownedProcess"] = true };
            case "validateAssets": return await Task.Run(() => ValidateAssets(token, (int)message.Number("pixelWidth")), token);
            case "hide": _hidden = true; foreach (var w in _windows) w.Pause(); return new JsonObject { ["hidden"] = true };
            case "show": _hidden = false; foreach (var w in _windows) w.Resume(); return new JsonObject { ["hidden"] = false };
            case "reload": Reload(); return new JsonObject { ["windows"] = _windows.Count, ["hidden"] = _hidden };
            case "home": foreach (var w in _windows) w.PlaceHome(); return new JsonObject { ["windows"] = _windows.Count };
            case "settings": OpenSettings(); return new JsonObject { ["ok"] = true };
            case "modelSettings": OpenSettings(); return new JsonObject { ["ok"] = true };
            case "stop": await ShutdownAsync(); return new JsonObject { ["stopping"] = true };
            case "request": return await RequestAsync(message, token);
            default: throw new InvalidDataException("未知桌宠命令。");
        }
    }

    private JsonObject ValidateAssets(CancellationToken token, int pixelWidth)
    {
        var count = 0;
        var transparent = 0;
        var totalFrames = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Assets, "webm"), "*.webm"))
        {
            token.ThrowIfCancellationRequested();
            using var decoder = new AnimationDecoder(path, pixelWidth);
            AnimationFrame? frame;
            var hasAlpha = false;
            while ((frame = decoder.Read(token)) is not null)
            {
                totalFrames++;
                for (var index = 3; index < frame.Pixels.Length; index += 4)
                    if (frame.Pixels[index] < 255) { hasAlpha = true; break; }
            }
            if (hasAlpha) transparent++;
            count++;
        }
        return new JsonObject { ["files"] = count, ["transparentFiles"] = transparent, ["decodedFrames"] = totalFrames };
    }

    private async Task<JsonObject> RequestAsync(JsonObject message, CancellationToken token)
    {
        var path = message.Text("path");
        if (path.Length > 4096 || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('\\')) throw new InvalidDataException("桌宠请求路径无效。");
        var uri = new Uri("https://seein-pet.invalid" + path);
        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "");
        var method = message.Text("method");
        JsonObject input = new();
        if (message.Text("body") is { Length: > 0 } encoded)
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > 12 * 1024 * 1024) throw new InvalidDataException("桌宠请求过大。");
            input = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("桌宠请求格式无效。");
        }
        JsonNode output;
        switch (method + " " + uri.AbsolutePath)
        {
            case "GET /dsh-pet-7340/config": output = Configuration.ReadEntries(); break;
            case "POST /dsh-pet-7340/config":
            case "PUT /dsh-pet-7340/config": Configuration.SaveMain(input); Reload(); output = Configuration.ReadEntries(); break;
            case "POST /dsh-pet-7340/config-entry": Configuration.SaveEntry(input.Text("entry"), input["patch"]!.AsObject()); Reload(); output = Configuration.ReadEntries(); break;
            case "POST /dsh-pet-7340/pet-update": Configuration.PatchPet(input.Text("id"), input["patch"]!.AsObject()); Reload(); output = Configuration.ReadEntries(); break;
            case "POST /dsh-pet-7340/pet-add": Configuration.AddPet(); Reload(); output = Configuration.ReadEntries(); break;
            case "POST /dsh-pet-7340/pet-remove": Configuration.RemovePet(input.Text("id")); Reload(); output = Configuration.ReadEntries(); break;
            case "POST /dsh-pet-7340/pet-action":
                var target = WindowFor(input.Text("id")) ?? throw new InvalidOperationException("请先开启这只宠物的桌面显示。");
                switch (input.Text("action"))
                {
                    case "chat": OpenChat(target); break;
                    case "sleep": target.ToggleSleep(); break;
                    case "home": target.PlaceHome(); break;
                    default: throw new InvalidDataException("未知宠物操作。");
                }
                output = new JsonObject { ["ok"] = true }; break;
            case "POST /dsh-pet-7340/pet-display":
                var display = input.Text("display");
                if (display is not ("none" or "web" or "desktop" or "both")) throw new InvalidDataException("显示方式无效。");
                Configuration.UpdatePet(input.Text("id"), p => p["display"] = display); Reload(); output = new JsonObject { ["ok"] = true }; break;
            case "GET /dsh-pet-7340/chat":
            case "GET /dsh-pet-7340/chat/history": output = new JsonObject { ["ok"] = true, ["messages"] = Memory.Read(FindPet(parameters.GetValueOrDefault("pet"))) }; break;
            case "POST /dsh-pet-7340/chat":
                var pet = FindPet(input.Text("pet", input.Text("id")));
                var reply = await Bridge.ChatAsync(pet, input.Text("text"), null, token);
                output = new JsonObject { ["ok"] = true, ["text"] = reply.Text, ["action"] = reply.Action, ["image"] = reply.Meme }; break;
            default: throw new InvalidDataException("桌宠使用大肥鱼桥；此功能未提供。");
        }
        return new JsonObject { ["status"] = 200, ["mediaType"] = "application/json", ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(output.ToJsonString())) };
    }

    private PetProfile FindPet(string? id) => Configuration.ReadPets().FirstOrDefault(p => id is null or "" || p.Id == id)
        ?? throw new InvalidDataException("找不到宠物。");

    private async Task ShutdownAsync()
    {
        StopMotionClock();
        _timer.Stop();
        _lifetime.Cancel();
        foreach (var window in _windows) await window.ShutdownAsync();
        foreach (var chat in _chats.ToArray()) chat.Close();
    }

    public void Dispose()
    {
        StopMotionClock();
        _timer.Stop();
        _lifetime.Cancel();
        Bridge.Dispose();
        Voice.Dispose();
        _speech.Close();
        _speechLifetime.Cancel();
        _speechLifetime.Dispose();
        _lifetime.Dispose();
    }
}
