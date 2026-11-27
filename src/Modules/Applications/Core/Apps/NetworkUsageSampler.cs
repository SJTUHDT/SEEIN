using System.Net;
using System.Runtime.InteropServices;

namespace Seein.Core.Apps;

/// <summary>
/// Per-process network counters built from the TCP table plus the per-connection extended
/// statistics (GetPerTcpConnectionEStats).  Works without an ETW kernel session, which Windows
/// refuses to create on machines that already run many tracing sessions.
/// </summary>
public sealed class NetworkUsageSampler : IDisposable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int TcpConnectionEstatsData = 1;

    private readonly object _gate = new();
    private readonly Dictionary<(long Local, long Remote), (long In, long Out)> _previous = new();
    private readonly HashSet<(long Local, long Remote)> _enabled = new();
    private readonly Dictionary<int, (long Up, long Down)> _pending = new();

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Connections inspected (diagnostic).</summary>
    public long EventCount;

    /// <summary>Last SetPerTcpConnectionEStats / GetPerTcpConnectionEStats status (diagnostic).</summary>
    public static uint LastEnableStatus;
    public static uint LastReadStatus;

    public bool Start(string instanceSuffix)
    {
        try
        {
            // Probe once so a failure is reported at start-up rather than silently later.
            Sample();
            IsRunning = true;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Bytes per process since the previous drain.</summary>
    public Dictionary<int, (long Up, long Down)> Drain()
    {
        lock (_gate)
        {
            Sample();
            var copy = new Dictionary<int, (long Up, long Down)>(_pending);
            _pending.Clear();
            return copy;
        }
    }

    private void Sample()
    {
        var seen = new Dictionary<(long Local, long Remote), (long In, long Out, int Pid)>();
        Collect(seen, ipv6: false);
        Collect(seen, ipv6: true);
        Interlocked.Add(ref EventCount, seen.Count);

        foreach (var (key, current) in seen)
        {
            if (_previous.TryGetValue(key, out var before))
            {
                var up = current.Out - before.Out;
                var down = current.In - before.In;
                if (up < 0) up = current.Out;
                if (down < 0) down = current.In;
                if (up == 0 && down == 0) continue;
                _pending.TryGetValue(current.Pid, out var total);
                _pending[current.Pid] = (total.Up + up, total.Down + down);
            }
        }

        _previous.Clear();
        foreach (var (key, value) in seen) _previous[key] = (value.In, value.Out);
    }

    private void Collect(Dictionary<(long, long), (long In, long Out, int Pid)> seen, bool ipv6)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, ipv6 ? AfInet6 : AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, false, ipv6 ? AfInet6 : AfInet, TcpTableOwnerPidAll, 0);
            if (result != 0) return;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = ipv6 ? Marshal.SizeOf<MibTcp6RowOwnerPid>() : Marshal.SizeOf<MibTcpRowOwnerPid>();
            var pointer = buffer + 4;
            for (var index = 0; index < count; index++)
            {
                var row = pointer + index * rowSize;
                if (ipv6)
                {
                    var entry = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(row);
                    if (NetworkSamplerPolicy.IsLoopbackTraffic(new IPAddress(entry.LocalAddr), new IPAddress(entry.RemoteAddr)))
                        continue;
                    var key = (Local: Hash6(entry.LocalAddr, (int)entry.LocalPort), Remote: Hash6(entry.RemoteAddr, (int)entry.RemotePort));
                    if (_enabled.Add(key) && EnableStats6(entry) != 0) _enabled.Remove(key);
                    var (bytesIn, bytesOut) = ReadStats6(entry);
                    if (bytesIn > 0 || bytesOut > 0) seen[key] = (bytesIn, bytesOut, (int)entry.OwningPid);
                }
                else
                {
                    var entry = Marshal.PtrToStructure<MibTcpRowOwnerPid>(row);
                    if (NetworkSamplerPolicy.IsLoopbackTraffic(new IPAddress(entry.LocalAddr), new IPAddress(entry.RemoteAddr)))
                        continue;
                    var key = (Local: ((long)entry.LocalAddr << 16) | (int)entry.LocalPort, Remote: ((long)entry.RemoteAddr << 16) | (int)entry.RemotePort);
                    if (_enabled.Add(key) && EnableStats4(entry) != 0) _enabled.Remove(key);
                    var (bytesIn, bytesOut) = ReadStats4(entry);
                    if (bytesIn > 0 || bytesOut > 0) seen[key] = (bytesIn, bytesOut, (int)entry.OwningPid);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static long Hash6(byte[] address, int port)
    {
        long hash = port;
        if (address is null) return hash;
        for (var index = 0; index < address.Length && index < 8; index++) hash = (hash << 8) ^ address[index];
        return hash ^ ((long)port << 32);
    }

    private static (long In, long Out) ReadStats4(MibTcpRowOwnerPid entry)
    {
        var row = new MibTcpRow
        {
            State = entry.State,
            LocalAddr = entry.LocalAddr,
            LocalPort = entry.LocalPort,
            RemoteAddr = entry.RemoteAddr,
            RemotePort = entry.RemotePort
        };
        var output = new byte[64];
        var handle = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            var rw = new byte[1] { 1 };
            var rwHandle = GCHandle.Alloc(rw, GCHandleType.Pinned);
            uint status;
            try
            {
                status = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, rwHandle.AddrOfPinnedObject(), 0, 1,
                    IntPtr.Zero, 0, 0, handle.AddrOfPinnedObject(), 0, (uint)output.Length);
            }
            finally { rwHandle.Free(); }
            LastReadStatus = status;
            if (status != 0) return (0, 0);
            var bytesOut = BitConverter.ToUInt64(output, 0);
            var bytesIn = BitConverter.ToUInt64(output, 8);
            return ((long)bytesIn, (long)bytesOut);
        }
        finally { handle.Free(); }
    }

    private static (long In, long Out) ReadStats6(MibTcp6RowOwnerPid entry)
    {
        var row = new MibTcp6Row
        {
            LocalAddr = entry.LocalAddr,
            LocalScopeId = entry.LocalScopeId,
            LocalPort = entry.LocalPort,
            RemoteAddr = entry.RemoteAddr,
            RemoteScopeId = entry.RemoteScopeId,
            RemotePort = entry.RemotePort,
            State = entry.State
        };
        var output = new byte[64];
        var handle = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            var status = GetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, IntPtr.Zero, 0, 0,
                IntPtr.Zero, 0, 0, handle.AddrOfPinnedObject(), 0, (uint)output.Length);
            if (status != 0) return (0, 0);
            var bytesOut = BitConverter.ToUInt64(output, 0);
            var bytesIn = BitConverter.ToUInt64(output, 8);
            return ((long)bytesIn, (long)bytesOut);
        }
        finally { handle.Free(); }
    }

    private static uint EnableStats4(MibTcpRowOwnerPid entry)
    {
        var row = new MibTcpRow
        {
            State = entry.State,
            LocalAddr = entry.LocalAddr,
            LocalPort = entry.LocalPort,
            RemoteAddr = entry.RemoteAddr,
            RemotePort = entry.RemotePort
        };
        var enable = new byte[1] { 1 };
        var handle = GCHandle.Alloc(enable, GCHandleType.Pinned);
        try
        {
            LastEnableStatus = SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, handle.AddrOfPinnedObject(), 0, 1, IntPtr.Zero, 0, 0);
            return LastEnableStatus;
        }
        finally { handle.Free(); }
    }

    private static uint EnableStats6(MibTcp6RowOwnerPid entry)
    {
        var row = new MibTcp6Row
        {
            LocalAddr = entry.LocalAddr,
            LocalScopeId = entry.LocalScopeId,
            LocalPort = entry.LocalPort,
            RemoteAddr = entry.RemoteAddr,
            RemoteScopeId = entry.RemoteScopeId,
            RemotePort = entry.RemotePort,
            State = entry.State
        };
        var enable = new byte[1] { 1 };
        var handle = GCHandle.Alloc(enable, GCHandleType.Pinned);
        try
        {
            return SetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, handle.AddrOfPinnedObject(), 0, 1, IntPtr.Zero, 0, 0);
        }
        finally { handle.Free(); }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _previous.Clear();
            _enabled.Clear();
            _pending.Clear();
        }
        IsRunning = false;
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6Row
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetPerTcp6ConnectionEStats(ref MibTcp6Row row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetPerTcp6ConnectionEStats(ref MibTcp6Row row, int estatsType,
        IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize);
}
