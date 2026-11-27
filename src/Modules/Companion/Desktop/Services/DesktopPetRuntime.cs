using Seein.Core;
using Seein.Core.Ipc;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Seein.Tray;

internal sealed class DesktopPetRuntime
{
    private const int MaxLine = 17 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1);
    private OwnedProcess? _owner;
    private Process? _process => _owner?.Process;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Task? _receiveTask;
    private CancellationTokenSource? _receiveLifetime;
    private PendingReply? _pendingReply;
    private long _nextId;
    private sealed record PendingReply(long Id, TaskCompletionSource<JsonElement> Completion);

    public static DesktopPetRuntime Instance { get; } = new();
    public event Action? SettingsRequested;

    public string Home { get; } = Path.Combine(AppPaths.UserDefault.DataRoot, "PetHome");
    public bool HasData => File.Exists(Path.Combine(Home, "dsh-pet", "main-config.jsonc"));
    private string EnabledMarker => Path.Combine(Home, "seein-enabled");
    public bool StartAtLogin => !File.Exists(Path.Combine(Home, "seein-disabled")) && (HasData || File.Exists(EnabledMarker));
    public bool IsRunning => _pipe?.IsConnected == true && _receiveTask is { IsCompleted: false } &&
        _process is { HasExited: false };
    public bool IsNative => Environment.GetEnvironmentVariable("SEEIN_PET_ENGINE") == "native" ||
        (!File.Exists(ProcessNames.Executable("webPet")) && File.Exists(ProcessNames.Executable("pet")));

    public async Task StartAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            Clear();
            var executable = ProcessNames.Executable(IsNative ? "pet" : "webPet");
            if (!File.Exists(executable)) throw new InvalidOperationException("SEEIN 桌宠运行时未安装。");

            var pipeName = "Seein.Pet." + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var environment = new ProcessStartInfo(executable).Environment;
            environment["DSH_HOME"] = Home;
            environment["SEEIN_PET_PROFILE"] = Path.Combine(Home, "Electron");
            environment["SEEIN_PET_PIPE"] = $@"\\.\pipe\{pipeName}";
            if (IsNative) environment["SEEIN_PET_PARENT"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                Directory.CreateDirectory(Home);
                _owner = OwnedProcess.Start(executable, environment: environment);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(18));
                await _pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var peer) || peer != _owner.Process.Id)
                    throw new IOException("桌宠宿主连接不匹配。");
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                _receiveLifetime = new CancellationTokenSource();
                _receiveTask = ReceiveAsync(new BoundedPipeLineReader(_pipe, MaxLine), _receiveLifetime.Token);
                _ = ReapOnDisconnectAsync(_owner, _receiveTask);
                var status = await CallCoreAsync("status", null, null, null, null, deadline.Token).ConfigureAwait(false);
                if (IsNative ? status.GetProperty("protocolVersion").GetInt32() != 1 || status.GetProperty("engine").GetString() != "native"
                    : status.GetProperty("version").GetString() != "0.4.9")
                    throw new InvalidOperationException("SEEIN 桌宠版本与 SEEIN 不匹配。");
                if (!HasData && !File.Exists(EnabledMarker)) File.WriteAllText(EnabledMarker, "");
                File.Delete(Path.Combine(Home, "seein-disabled"));
            }
            catch
            {
                Clear();
                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("SEEIN 桌宠启动失败。请检查桌宠日志或稍后重试。");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonElement> CallAsync(string operation, string? method = null, string? path = null,
        byte[]? body = null, string? mediaType = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!IsRunning) throw new InvalidOperationException("桌宠正在停止或尚未启动。");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                return await CallCoreAsync(operation, method, path, body, mediaType, deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            {
                Clear();
                throw new InvalidOperationException("与桌宠的私有连接已断开，请重新启动桌宠。");
            }
        }
        finally { _gate.Release(); }
    }

    public Task StopAsync() => StopCoreAsync(false);

    public Task DisableAsync() => StopCoreAsync(true);

    private async Task StopCoreAsync(bool disable)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try { await _gate.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Closing the private channel asks the owned child to shut down even with an operation in flight.
            _owner?.TerminateTree();
            _pipe?.Dispose();
            if (disable) RememberDisabled();
            return;
        }
        try
        {
            if (IsRunning)
            {
                try
                {
                    await CallCoreAsync("stop", null, null, null, null, deadline.Token).ConfigureAwait(false);
                    await _process!.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
            }
        }
        finally
        {
            try
            {
                Clear();
                if (disable) RememberDisabled();
            }
            finally { _gate.Release(); }
        }
    }

    private void RememberDisabled()
    {
        try { File.WriteAllText(Path.Combine(Home, "seein-disabled"), ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("桌宠已停止，但未能保存停用状态。", ex); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    private async Task<JsonElement> CallCoreAsync(string operation, string? method, string? path, byte[]? body,
        string? mediaType, CancellationToken token)
    {
        if (_receiveTask is not { IsCompleted: false } receive) throw new IOException("Desktop pet pipe closed");
        var id = ++_nextId;
        var message = JsonSerializer.Serialize(new
        {
            id, op = operation, method, path,
            body = body is null || body.Length == 0 ? null : Convert.ToBase64String(body),
            mediaType
        }, JsonDefaults.Options);
        var pending = new PendingReply(id, new(TaskCreationOptions.RunContinuationsAsynchronously));
        Volatile.Write(ref _pendingReply, pending);
        try
        {
            await _writer!.WriteLineAsync(message.AsMemory(), token).ConfigureAwait(false);
            var completed = await Task.WhenAny(pending.Completion.Task, receive).WaitAsync(token).ConfigureAwait(false);
            if (completed == receive && !pending.Completion.Task.IsCompleted) throw new IOException("Desktop pet pipe closed");
            var root = await pending.Completion.Task.ConfigureAwait(false);
            if (!root.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.GetString() : "桌宠未能完成该操作。");
            return root.GetProperty("result");
        }
        finally { Interlocked.CompareExchange(ref _pendingReply, null, pending); }
    }

    private static async Task ReapOnDisconnectAsync(OwnedProcess owner, Task receiver)
    {
        try
        {
            await receiver.ConfigureAwait(false);
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await owner.Process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException) { }
        finally { owner.TerminateTree(); }
    }

    private async Task ReceiveAsync(BoundedPipeLineReader reader, CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object || token.IsCancellationRequested) break;
                if (message.TryGetProperty("event", out var kind))
                {
                    if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "openSettings")
                        SettingsRequested?.Invoke();
                    continue;
                }
                var pending = Volatile.Read(ref _pendingReply);
                if (pending is null) continue;
                if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                    !id.TryGetInt64(out var number) || number != pending.Id ||
                    !message.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    (ok.ValueKind == JsonValueKind.True && !message.TryGetProperty("result", out _))) break;
                pending.Completion.TrySetResult(message.Clone());
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
    }

    private void Clear()
    {
        _receiveLifetime?.Cancel();
        _pipe?.Dispose();
        _pipe = null;
        _writer = null;
        _receiveLifetime?.Dispose();
        _receiveLifetime = null;
        _receiveTask = null;
        _owner?.Dispose();
        _owner = null;
    }
}
