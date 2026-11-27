using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Seein.Core;

public sealed class OwnedProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    public Process Process { get; }

    private OwnedProcess(SafeFileHandle job, Process process) => (_job, Process) = (job, process);

    public static OwnedProcess Start(string executable, string arguments = "", IDictionary<string, string?>? environment = null, bool hidden = false)
        => Start(executable, arguments, environment, hidden, allowBreakaway: false);

    // A management workbench may explicitly launch independent services. Only
    // children requesting CREATE_BREAKAWAY_FROM_JOB escape this optional policy.
    public static OwnedProcess Start(string executable, string arguments, IDictionary<string, string?>? environment, bool hidden, bool allowBreakaway)
    {
        if (IntPtr.Size != 8 || !Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Expected an absolute executable path on Windows x64.", nameof(executable));
        var job = CreateJobObjectW(0, null);
        try
        {
            if (job.IsInvalid) throw new Win32Exception();
            var limits = new JobLimits { LimitFlags = 0x2000u | (allowBreakaway ? 0x800u : 0u) };
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>())) throw new Win32Exception();
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            if (size == 0) throw new Win32Exception();
            var list = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
                var jobValue = Marshal.AllocHGlobal(IntPtr.Size);
                nint block = 0;
                try
                {
                    Marshal.WriteIntPtr(jobValue, job.DangerousGetHandle());
                    const int jobListAttribute = 0x2000D;
                    if (!UpdateProcThreadAttribute(list, 0, jobListAttribute, jobValue, (nuint)IntPtr.Size, 0, 0))
                        throw new Win32Exception();
                    if (environment != null)
                    {
                        var entries = environment.Where(pair => pair.Value != null).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase);
                        block = Marshal.StringToHGlobalUni(string.Join('\0', entries.Select(pair => $"{pair.Key}={pair.Value}")) + "\0");
                    }
                    var startup = new StartupInfoEx { Size = Marshal.SizeOf<StartupInfoEx>(), Attributes = list, Flags = hidden ? 1u : 0u };
                    var command = new StringBuilder($"\"{executable}\" {arguments}");
                    const uint flags = 0x00080000 | 0x00000400 | 0x08000000;
                    if (!CreateProcessW(executable, command, 0, 0, false, flags, block, Path.GetDirectoryName(executable)!,
                            ref startup, out var info)) throw new Win32Exception();
                    using var processHandle = new SafeFileHandle(info.ProcessHandle, true);
                    using var threadHandle = new SafeFileHandle(info.ThreadHandle, true);
                    return new OwnedProcess(job, Process.GetProcessById(info.ProcessId));
                }
                finally
                {
                    DeleteProcThreadAttributeList(list);
                    Marshal.FreeHGlobal(jobValue);
                    Marshal.FreeHGlobal(block);
                }
            }
            finally { Marshal.FreeHGlobal(list); }
        }
        catch { job.Dispose(); throw; }
    }

    public void TerminateTree() => _job.Dispose();
    public void Dispose()
    {
        TerminateTree();
        Process.Dispose();
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct JobLimits { [FieldOffset(16)] public uint LimitFlags; }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    private struct StartupInfoEx
    {
        [FieldOffset(0)] public int Size;
        [FieldOffset(60)] public uint Flags;
        [FieldOffset(104)] public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint ProcessHandle, ThreadHandle; public int ProcessId, ThreadId; }

    [DllImport("kernel32", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits limits, int size);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
}
