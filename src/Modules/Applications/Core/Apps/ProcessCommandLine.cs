using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Seein.Core.Apps;

/// <summary>
/// Reads the command line of a process through <c>NtQueryInformationProcess</c>
/// (<c>ProcessCommandLineInformation</c>), which needs no access to the target's memory and works for
/// services too.  Results are cached per pid + start time because a command line never changes.
/// </summary>
public static class ProcessCommandLine
{
    private const int ProcessCommandLineInformation = 60;
    private static readonly Dictionary<int, (long Started, string? Value)> Cache = new();
    private static readonly object Gate = new();

    public static string? Read(int pid, long startedUtc)
    {
        lock (Gate)
            if (Cache.TryGetValue(pid, out var cached) && cached.Started == startedUtc) return cached.Value;

        var value = Query(pid);
        lock (Gate)
        {
            if (Cache.Count > 2048) Cache.Clear();
            Cache[pid] = (startedUtc, value);
        }
        return value;
    }

    private static string? Query(int pid)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            var handle = process.Handle;
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
            if (size <= 0) return null;

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out size);
                if (status < 0) return null;
                var length = Marshal.ReadInt16(buffer);
                if (length <= 0) return null;
                var pointer = Marshal.ReadIntPtr(buffer, Marshal.SizeOf<IntPtr>() == 8 ? 8 : 4);
                return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer, length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return null; }
        finally { process?.Dispose(); }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr buffer, int bufferSize, out int returnedSize);
}
