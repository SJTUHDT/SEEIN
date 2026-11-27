using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Seein.Core.Apps;
/// <summary>One row of the process table for a single sampling tick.</summary>
public sealed record ProcessSample(
    int Pid,
    int ParentPid,
    string ExeName,
    string? ExePath,
    int SessionId,
    double CpuSeconds,
    long StartedUtc);



/// <summary>
/// Reads pid, parent pid and executable name in a single Toolhelp snapshot (about 2-3 ms on a
/// 400 process machine) and then enriches each entry with CPU time and start time.
/// </summary>
public static class ProcessSampler
{
    private const uint SnapshotProcesses = 0x00000002;

    public static IReadOnlyList<ProcessSample> Capture()
    {
        var entries = Snapshot();
        var samples = new List<ProcessSample>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Pid <= 4) continue;
            var cpu = 0d;
            var started = 0L;
            var session = 0;
            string? path = null;
            Process? process = null;
            try
            {
                process = Process.GetProcessById(entry.Pid);
                try { cpu = process.TotalProcessorTime.TotalSeconds; } catch { }
                try { started = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds(); } catch { }
                try { session = process.SessionId; } catch { }
                try { path = process.MainModule?.FileName; } catch { }
            }
            catch { continue; }
            finally { process?.Dispose(); }

            samples.Add(new ProcessSample(entry.Pid, entry.ParentPid, entry.ExeName, path, session, cpu, started));
        }
        return samples;
    }

    private static List<(int Pid, int ParentPid, string ExeName)> Snapshot()
    {
        var list = new List<(int, int, string)>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == new IntPtr(-1)) return list;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return list;
            do
            {
                list.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
