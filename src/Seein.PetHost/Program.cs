using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using Seein.Core.Ipc;
using Seein.Pet;

namespace Seein.PetHost;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var pipeName = Environment.GetEnvironmentVariable("SEEIN_PET_PIPE") ?? "";
        var home = Environment.GetEnvironmentVariable("DSH_HOME") ?? "";
        if (!Regex.IsMatch(pipeName, @"\A\\\\\.\\pipe\\Seein\.Pet\.[a-f0-9]{32}\z") || !Path.IsPathFullyQualified(home)
            || !int.TryParse(Environment.GetEnvironmentVariable("SEEIN_PET_PARENT"), out var parentId)) return 2;
        using var parent = Process.GetProcessById(parentId);
        using var pipe = new NamedPipeClientStream(".", pipeName[9..], PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(10000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != parentId || parent.HasExited) return 3;
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var theme = new NativeTheme(application, home);
            using var lifetime = new CancellationTokenSource();
            using var engine = new PetEngine(home, Path.Combine(Seein.Core.ProcessNames.ModuleDirectory("companion"), "pet-assets"), application.Dispatcher);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => application.Dispatcher.BeginInvoke(() => application.Shutdown());
            application.Startup += async (_, _) =>
            {
                try
                {
                    engine.Reload();
                    await ServeAsync(pipe, engine, lifetime.Token);
                }
                catch (Exception ex) { Log(home, ex); }
                finally { application.Shutdown(); }
            };
            application.Exit += (_, _) => lifetime.Cancel();
            application.Run();
            return 0;
        }
        catch (Exception ex) { Log(home, ex); return 1; }
    }

    private static async Task ServeAsync(NamedPipeClientStream pipe, PetEngine engine, CancellationToken token)
    {
        var reader = new BoundedPipeLineReader(pipe, 17 * 1024 * 1024);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, true) { AutoFlush = true, NewLine = "\n" };
        using var writes = new SemaphoreSlim(1, 1);
        async Task SendAsync(JsonObject response)
        {
            var payload = response.ToJsonString();
            if (Encoding.UTF8.GetByteCount(payload) > 17 * 1024 * 1024) throw new InvalidDataException("桌宠响应超过大小限制。");
            await writes.WaitAsync(token);
            try { await writer.WriteLineAsync(payload.AsMemory(), token); }
            finally { writes.Release(); }
        }
        async void OpenSettings()
        {
            try { await SendAsync(new JsonObject { ["event"] = "openSettings" }); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        engine.SettingsRequested += OpenSettings;
        try
        {
        while (await reader.ReadLineAsync(token) is { } line)
        {
            var message = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("桌宠命令格式无效。");
            var id = message["id"]?.GetValue<long>() ?? throw new InvalidDataException("桌宠命令缺少编号。");
            JsonObject response;
            try
            {
                var result = await engine.ExecuteAsync(message, token);
                response = new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = result };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                response = new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = ex is OperationCanceledException ? "操作已取消或超时。" : ex.Message };
                if (message.Text("op") == "validateAssets") response["error"] = ex.ToString();
            }
            await SendAsync(response);
            if (message.Text("op") == "stop") return;
        }
        }
        finally { engine.SettingsRequested -= OpenSettings; }
    }

    private static void Log(string home, Exception exception)
    {
        try
        {
            var directory = Path.Combine(home, "native-logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "host.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:o} {exception.GetType().Name}: {exception.Message}\n");
        }
        catch (IOException) { }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int serverPid);
}
