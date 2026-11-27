using Seein.Core;
using Seein.Core.Tools;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace Seein.Tray;

internal sealed class ToolRuntime : IDisposable
{
    public static ToolRuntime Instance { get; } = new();
    public string DataRoot { get; } = Path.Combine(AppPaths.UserDefault.DataRoot, "Tools");
    private readonly Dictionary<string, Process> _processes = [];
    private readonly Dictionary<string, OwnedProcess> _owners = [];
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _namespace = "Seein.Tools." + WindowsIdentity.GetCurrent().User!.Value + "." +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.UserDefault.DataRoot)))[..12];
    public ToolPreferences Preferences { get; private set; } = new();
    public event Action<string>? SettingsRequested;
    public event Action? PreferencesChanged;
    private readonly ConcurrentDictionary<string, bool> _capabilities = new();
    private readonly Dictionary<string, IReadOnlyList<ToolField>> _settings = [];
    private readonly object _settingsGate = new();
    private ToolSettingsSnapshotStore? _snapshotStore;
    private readonly Dictionary<string, (long Revision, Task<ToolResponse> Task)> _settingsReads = [];
    private readonly Dictionary<string, long> _settingsRevisions = [];
    private ToolSettingsSnapshotStore SnapshotStore { get { lock (_settingsGate) return _snapshotStore ??= new(DataRoot); } }
    public Task<ToolResponse> GetSettingsAsync(string module)
    {
        lock (_settingsGate)
        {
            var revision = _settingsRevisions.GetValueOrDefault(module);
            if (_settingsReads.TryGetValue(module, out var existing) && existing.Revision == revision) return existing.Task;
            var completion = new TaskCompletionSource<ToolResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _settingsReads[module] = (revision, completion.Task);
            _ = Task.Run(async () =>
            {
                try { completion.SetResult(await SendAsync(module, "settings.get")); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { lock (_settingsGate) if (_settingsReads.TryGetValue(module, out var pending) && pending.Task == completion.Task) _settingsReads.Remove(module); }
            });
            return completion.Task;
        }
    }
    private string EngineStamp(string module)
    {
        var executable = new FileInfo(ProcessNames.Executable(module == "snow" ? "capture" : "efficiency"));
        var bridge = new FileInfo(typeof(ToolRuntime).Assembly.Location);
        var managed = new FileInfo(Path.ChangeExtension(executable.FullName, ".dll"));
        return $"{executable.Length}:{executable.LastWriteTimeUtc.Ticks}:{bridge.Length}:{bridge.LastWriteTimeUtc.Ticks}:{(managed.Exists ? managed.LastWriteTimeUtc.Ticks : 0)}";
    }
    public IReadOnlyList<ToolField>? CachedSettings(string module)
    {
        lock (_settingsGate)
        {
            if (_settings.TryGetValue(module, out var fields)) return fields;
            try { fields = SnapshotStore.Load(module, EngineStamp(module)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            if (fields is not null) _settings[module] = fields;
            return fields;
        }
    }
    private async Task RememberSettingsAsync(string module, IReadOnlyList<ToolField> fields, long revision)
    {
        lock (_settingsGate) { if (_settingsRevisions.GetValueOrDefault(module) != revision) return; _settings[module] = fields; }
        try { await Task.Run(() => { lock (_settingsGate) if (_settingsRevisions.GetValueOrDefault(module) == revision) SnapshotStore.Save(module, EngineStamp(module), fields); }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(ex.Message); }
    }
    public bool ActionEnabled(string action)
    {
        if (!action.StartsWith("pane.")) return !action.StartsWith("snow.") || Enabled("snow");
        var capability = action switch { "pane.launcher" or "pane.files" => "launcher", "pane.folder-jump" or "pane.folder-menu" => "folder-jump", "pane.power" => "power-timer", _ => action[5..] };
        return Enabled("pane") && _capabilities.GetValueOrDefault(capability, true);
    }
    private ToolRuntime()
    {
        // The integrated tools are available by default; obsolete preview toggles
        // must not silently disable them after upgrading.
        _ = ListenAsync(_shutdown.Token);
    }

    public static readonly HashSet<string> ActiveModules=[];
    public bool Enabled(string module) { lock (ActiveModules) return ActiveModules.Contains(module) && (module == "snow" ? Preferences.ScreenshotEnabled : Preferences.PaneEnabled); }
    public async Task SavePreferencesAsync(ToolPreferences value)
    {
        foreach (var module in new[] { "snow", "pane" })
            if (module == "snow" ? !value.ScreenshotEnabled : !value.PaneEnabled) await StopAsync(module);
        Directory.CreateDirectory(DataRoot);
        var file = Path.Combine(DataRoot, "preferences.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(value));
        File.Move(file + ".tmp", file, true);
        Preferences = value;
        if (Enabled("pane")) await EnsureStartedAsync("pane");
        PreferencesChanged?.Invoke();
    }

    public async Task<ToolResponse> SendAsync(string module, string operation, string? action = null, JsonObject? values = null)
    {
        long revision;
        lock (_settingsGate)
        {
            revision = _settingsRevisions.GetValueOrDefault(module);
            if (operation == "settings.apply") _settingsRevisions[module] = ++revision;
        }
        try
        {
            if (!Enabled(module)) return new("", false, "此工具已停用，请先启用。");
            if (operation == "execute" && action == "pane.files")
            {
                var index = await EverythingRuntime.Instance.EnsureAsync(_shutdown.Token);
                if (!index.Ready) return new("", false, index.Message);
            }
            else if (operation == "execute" && action == "pane.launcher") _ = EverythingRuntime.Instance.EnsureAsync(_shutdown.Token);
            await EnsureStartedAsync(module);
            if (operation == "execute" && _processes.TryGetValue(module, out var process))
                AllowSetForegroundWindow((uint)process.Id);
            var response = await ToolPipeClient.SendAsync(PipeName(module), new(Guid.NewGuid().ToString("N"), operation, action, values), _shutdown.Token);
            if (response.Success && operation is "settings.get" or "settings.apply")
            {
                var schema = operation == "settings.get" ? response : await ToolPipeClient.SendAsync(PipeName(module), new(Guid.NewGuid().ToString("N"), "settings.get"), _shutdown.Token);
                if (schema.Success && schema.Fields is {} fields)
                {
                    await RememberSettingsAsync(module, fields, revision);
                    response = response with { Fields = fields };
                }
                var changed = false;
                foreach (var field in schema.Fields ?? [])
                    if (field.Kind == "capability" && field.Value is {} value)
                    {
                        var key = field.Id[11..]; var enabled = value.GetValue<bool>();
                        changed |= _capabilities.GetValueOrDefault(key, true) != enabled; _capabilities[key] = enabled;
                    }
                if (changed) PreferencesChanged?.Invoke();
            }
            return response;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new("", false, ex.Message); }
    }
    public Task<ToolResponse> ExecuteAsync(string action)
    {
        if (!ToolCatalog.IsAction(action)) throw new ArgumentException("不支持的小工具动作。");
        return SendAsync(ToolCatalog.ModuleOf(action), "execute", action);
    }
    public Task PrepareAsync(string module) => EnsureStartedAsync(module);
    private string PipeName(string module) => _namespace + "." + module;
    public string ShellPipeName => _namespace + ".shell";
    private async Task EnsureStartedAsync(string module)
    {
        if(!Enabled(module))throw new InvalidOperationException("此功能插件未启用。");
        await _gate.WaitAsync(_shutdown.Token);
        try
        {
            if (_processes.TryGetValue(module, out var existing) && !existing.HasExited) return;
            if (_owners.Remove(module, out var previousOwner)) previousOwner.Dispose();
            var directory = Path.Combine(AppContext.BaseDirectory, "tools", module);
            var executable = ProcessNames.Executable(module == "snow" ? "capture" : "efficiency");
            if (!File.Exists(executable)) throw new FileNotFoundException("工具组件未安装完整，请重新安装 SEEIN。", executable);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.Environment["SEEIN_TOOL_PIPE"] = PipeName(module);
            start.Environment["SEEIN_SHELL_PIPE"] = ShellPipeName;
            start.Environment["SEEIN_TOOL_DATA"] = Path.Combine(DataRoot, module);
            start.Environment["PANE_DATA_DIR"] = Path.Combine(DataRoot, module);
            start.Environment["PANE_INSTANCE"] = PipeName(module);
            start.Environment["SEEIN_INDEX_INSTANCE"] = EverythingRuntime.InstanceName;
            if (module == "snow")
            {
                var paths = ToolStoragePaths.Resolve(AppPaths.UserDefault.DataRoot);
                Directory.CreateDirectory(paths.Cache); Directory.CreateDirectory(paths.Temporary);
                start.Environment["SEEIN_CACHE_DIRECTORY"] = paths.Cache;
                var recordingStaging = Path.Combine(paths.Cache, "录屏实时缓存");
                Directory.CreateDirectory(recordingStaging);
                start.Environment["SEEIN_RECORDING_STAGING_DIRECTORY"] = recordingStaging;
                start.Environment["TEMP"] = paths.Temporary; start.Environment["TMP"] = paths.Temporary;
                if (paths.Pdf is {} pdf) { Directory.CreateDirectory(pdf); start.Environment["SEEIN_PDF_DIRECTORY"] = pdf; }
            }
            var owner = OwnedProcess.Start(executable, "--autostart", start.Environment, hidden: true);
            _owners[module] = owner;
            var process = owner.Process;
            _processes[module] = process;
            try
            {
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    if (process.HasExited) throw new IOException($"工具启动失败，退出码 {process.ExitCode}。");
                    using var probe = new NamedPipeClientStream(".", PipeName(module), PipeDirection.InOut, PipeOptions.Asynchronous);
                    try { await probe.ConnectAsync(100, _shutdown.Token); return; }
                    catch (TimeoutException) { await Task.Delay(100, _shutdown.Token); }
                }
                throw new IOException("工具正在准备资源，请稍后重试。");
            }
            catch
            {
                _processes.Remove(module);
                _owners.Remove(module);
                owner.Dispose();
                throw;
            }
        }
        finally { _gate.Release(); }
    }
    public async Task StartBackgroundAsync()
    {
        if (Enabled("pane"))
            try
            {
                // Start indexing in parallel so clipboard and app launching remain responsive.
                var index = EverythingRuntime.Instance.EnsureAsync(_shutdown.Token);
                await GetSettingsAsync("pane");
                if ((await index).Ready) await SendAsync("pane", "files.refresh");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    public async Task WarmSettingsAsync(string module, CancellationToken token)
    {
        try
        {
            await Task.Delay(2500, token);
            if (Enabled(module)) await GetSettingsAsync(module);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(ShellPipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                var line = await reader.ReadLineAsync(token);
                if (line is null || line.Length > 16384) continue;
                var request = JsonSerializer.Deserialize<ToolRequest>(line, ToolCatalog.Json);
                if (request?.Version != 1 || request.Action is not ("snow" or "pane")) continue;
                if (request.Operation == "settings") SettingsRequested?.Invoke(request.Action);
                else if (request.Operation == "external" && request.Values?["url"]?.GetValue<string>() is { } target)
                {
                    var start = ToolExternalLaunch.Create(target);
                    await Task.Run(() => { using var process = Process.Start(start); }, token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(100, token); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or System.ComponentModel.Win32Exception) { }
        }
    }
    public async Task StopAsync(string module)
    {
        if (!_processes.TryGetValue(module, out var process) || process.HasExited) return;
        try
        {
            var response = await ToolPipeClient.SendAsync(PipeName(module), new(Guid.NewGuid().ToString("N"), "shutdown")).ConfigureAwait(false);
            if (!response.Success) throw new IOException(response.Error);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _processes.Remove(module);
            if (_owners.Remove(module, out var owner)) owner.Dispose();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { throw new IOException("工具仍在忙，请结束录屏或保存操作后重试。", ex); }
    }
    public async Task StopAllAsync()
    {
        foreach (var module in _processes.Keys.ToArray()) await StopAsync(module);
    }
    public void Dispose()
    {
        foreach (var module in _processes.Keys.ToArray())
            try { StopAsync(module).GetAwaiter().GetResult(); } catch (Exception) { }
        _shutdown.Cancel();
        foreach (var process in _processes.Values) process.Dispose();
        foreach (var owner in _owners.Values) owner.Dispose();
        _owners.Clear();
        EverythingRuntime.Instance.Dispose();
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
