using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Seein.Core;
using Seein.Core.Lighting;

namespace Seein.Service;

public sealed class RgbEngineHost(ILogger<RgbEngineHost> logger)
{
    private OwnedProcess? _owner;
    private Process? _process => _owner?.Process;
    private DateTimeOffset _retryAfter;

    public string? LastError { get; private set; }
    public int Port => OpenRgbProvider.DefaultPort;

    public bool EnsureReady(bool autoStart)
    {
        if (_process is { HasExited: true } exited)
        {
            var code = ReadExitCode(exited);
            LastError = code is int value ? $"灯效引擎退出（代码 {value}）" : "灯效引擎已退出";
            logger.LogWarning("RGB engine exited with code {ExitCode}", code?.ToString() ?? "unknown");
            Stop();
        }

        var listeners = GetListenerOwners(Port);
        if (_process is { } running && listeners.Count == 1 &&
            listeners[0].Pid == running.Id && listeners[0].Address.Equals(IPAddress.Loopback))
        {
            LastError = null;
            return true;
        }

        if (listeners.Count != 0)
        {
            LastError = $"灯效引擎端口 {Port} 被占用或监听地址异常";
            Stop();
            return false;
        }

        if (!autoStart)
        {
            Stop();
            LastError = "灯效引擎自动启动已关闭";
            return false;
        }

        if (_process is null && DateTimeOffset.UtcNow >= _retryAfter) Start();
        LastError ??= "正在等待 SEEIN 灯效引擎启动";
        return false;
    }

    private void Start()
    {
        _retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
        var engine = ProcessNames.Executable("rgb");
        if (!File.Exists(engine))
        {
            LastError = "安装目录缺少 SEEIN 灯效引擎";
            return;
        }

        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(localData)) throw new IOException("找不到服务账户的本地配置目录");
            var config = Path.Combine(localData, AppPaths.FolderName, "rgb-engine");
            Directory.CreateDirectory(config);
            _owner = OwnedProcess.Start(engine, $"--server --server-host 127.0.0.1 --server-port {Port} --noautoconnect --config \"{config}\"", hidden: true);
            LastError = null;
            logger.LogInformation("RGB engine started (PID {Pid}, session {Session})", _owner.Process.Id, _owner.Process.SessionId);
        }
        catch (Exception ex)
        {
            Stop();
            LastError = $"灯效引擎启动失败：{ex.Message}";
            logger.LogWarning(ex, "Starting the RGB engine failed");
        }
    }


    public void Stop()
    {
        _owner?.Dispose();
        _owner = null;
    }

    internal static int? ReadExitCode(Process process)
    {
        // OwnedProcess uses CreateProcessW/GetProcessById. Once the PID is gone,
        // .NET may know it exited without retaining a handle to query its code.
        // Diagnostic metadata must never prevent releasing/restarting the owner.
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static List<(int Pid, IPAddress Address)> GetListenerOwners(int port)
    {
        int size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        while (result == 122)
        {
            var table = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(table, ref size, false, 2, 3, 0);
                if (result != 0) continue;
                var listeners = new List<(int, IPAddress)>();
                var count = Marshal.ReadInt32(table);
                // MIB_TCPROW_OWNER_PID contains six 32-bit fields.
                for (var i = 0; i < count; i++)
                {
                    var row = 4 + i * 24;
                    var localPort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(table, row + 8));
                    if (localPort != port) continue;
                    var address = new IPAddress((uint)Marshal.ReadInt32(table, row + 4));
                    listeners.Add((Marshal.ReadInt32(table, row + 20), address));
                }
                return listeners;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new System.ComponentModel.Win32Exception((int)result);
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

}
