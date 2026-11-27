using Microsoft.Win32;
using Seein.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Seein.Tray;

internal sealed record FileIndexAvailability(bool Ready, string Message);

internal sealed class EverythingRuntime : IDisposable
{
    public static EverythingRuntime Instance { get; } = new();
    internal static string InstanceName => "SEEIN" + AppPaths.InstanceSuffix;
    private readonly SemaphoreSlim _gate = new(1);
    private OwnedProcess? _owner;
    private string? _executable;
    public async Task<FileIndexAvailability> EnsureAsync(CancellationToken cancellation = default)
    {
        try { await _gate.WaitAsync(cancellation); }
        catch (OperationCanceledException) { return new(false, "文件索引启动已取消。"); }
        try
        {
            if (Ready()) return new(true, "文件索引已连接。");
            if (Endpoint() == 0)
            {
                var executable = FindExecutable();
                if (executable is null) return new(false, "文件搜索组件未安装完整，请修复 SEEIN 安装。");
                var data = Path.Combine(AppPaths.UserDefault.DataRoot, "Tools", "everything");
                Directory.CreateDirectory(data);
                var ini = Path.Combine(data, "Everything.ini");
                if (!File.Exists(ini)) File.WriteAllText(ini,
                    "[Everything]\r\nrun_in_background=1\r\nshow_tray_icon=0\r\ncheck_for_updates_on_startup=0\r\nrun_as_admin=0\r\nhttp_server_enabled=0\r\netp_server_enabled=0\r\n");
                _owner?.Dispose();
                _executable = executable;
                var arguments = $"-instance \"{InstanceName}\" -startup -config \"{ini}\" -db \"{Path.Combine(data, "Everything.db")}\"";
                _owner = OwnedProcess.Start(executable, arguments, hidden: true);
            }
            for (var attempt = 0; attempt < 150; attempt++)
            {
                if (Ready()) return new(true, "文件索引已连接。");
                await Task.Delay(200, cancellation);
            }
            return new(false, "文件索引仍在准备，稍后重试即可。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(false, "文件索引启动失败：" + ex.Message); }
        catch (OperationCanceledException) { return new(false, "文件索引启动已取消。"); }
        finally { _gate.Release(); }
    }
    private static string? FindExecutable()
    {
        var configured = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\SEEIN\Dependencies", "EverythingExecutable", null) as string;
        return new[] { ProcessNames.Executable("index"), configured }.FirstOrDefault(path =>
            path is not null && Path.GetFileName(path).Equals(ProcessNames.FileName("index"), StringComparison.OrdinalIgnoreCase) && File.Exists(path));
    }
    private static nint Endpoint()
    {
        return FindWindowW($"EVERYTHING_TASKBAR_NOTIFICATION_({InstanceName})", null);
    }
    private static bool Ready()
    {
        var window = Endpoint();
        // Everything SDK: EVERYTHING_WM_IPC = WM_USER; IS_DB_LOADED = 401.
        return window != 0 && SendMessageTimeoutW(window, 0x400, 401, 0, 2, 100, out var loaded) != 0 && loaded != 0;
    }
    public void Dispose()
    {
        // Only close the SEEIN-owned instance; a user's separate Everything stays open.
        if (_owner is { } owner)
        {
            try
            {
                if (!owner.Process.HasExited && _executable is { } executable)
                {
                    using var control = Process.Start(new ProcessStartInfo(executable, $"-instance \"{InstanceName}\" -exit")
                        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                    owner.Process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { owner.Dispose(); _owner = null; }
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string name, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}
