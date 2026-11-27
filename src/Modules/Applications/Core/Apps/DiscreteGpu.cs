using System.Runtime.InteropServices;

namespace Seein.Core.Apps;

/// <summary>
/// Resolves the LUID of the discrete GPU (the adapter with the largest dedicated video memory)
/// so GPU utilization counters can ignore the integrated graphics.
/// </summary>
internal static class DiscreteGpu
{
    private const uint DxgiAdapterFlagSoftware = 0x2;

    private static bool _resolved;
    private static long? _luid;

    public static long? Luid
    {
        get
        {
            if (!_resolved)
            {
                _resolved = true;
                try { _luid = ResolveLuid(); }
                catch { _luid = null; }
            }
            return _luid;
        }
    }

    private static long? ResolveLuid()
    {
        var iid = typeof(IDxgiFactory1).GUID;
        var factoryPointer = IntPtr.Zero;
        // CreateDXGIFactory1 exists on Windows 8+; fall back silently when unavailable.
        if (CreateDXGIFactory1(ref iid, out factoryPointer) != 0 || factoryPointer == IntPtr.Zero) return null;
        try
        {
            var factory = (IDxgiFactory1)Marshal.GetObjectForIUnknown(factoryPointer);
            IDxgiAdapter1? best = null;
            ulong bestMemory = 0;
            for (uint index = 0; ; index++)
            {
                IDxgiAdapter1 adapter;
                try { adapter = factory.EnumAdapters1(index); }
                catch (COMException) { break; }
                if (adapter is null) break;
                adapter.GetDesc1(out var description);
                if ((description.Flags & DxgiAdapterFlagSoftware) != 0) continue;
                var memory = (ulong)description.DedicatedVideoMemory.ToUInt64();
                if (memory > bestMemory)
                {
                    bestMemory = memory;
                    best = adapter;
                }
            }
            if (best is null) return null;
            best.GetDesc1(out var bestDesc);
            // Integrated graphics report little or no dedicated memory; keep the filter only when the
            // selected adapter clearly owns its own VRAM.
            if (bestMemory < 256UL * 1024 * 1024) return null;
            return bestDesc.AdapterLuid;
        }
        finally { Marshal.Release(factoryPointer); }
    }

    [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiFactory1
    {
        // IDXGIObject
        int SetPrivateData(ref Guid name, uint size, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        int GetParent(ref Guid iid, out IntPtr parent);
        // IDXGIFactory
        int EnumAdapters(uint index, out IntPtr adapter);
        int MakeWindowAssociation(IntPtr window, uint flags);
        int GetWindowAssociation(out IntPtr window);
        int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        // IDXGIFactory1
        IDxgiAdapter1 EnumAdapters1(uint index);
        int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiAdapter1
    {
        // IDXGIObject
        int SetPrivateData(ref Guid name, uint size, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        int GetParent(ref Guid iid, out IntPtr parent);
        // IDXGIAdapter
        int EnumOutputs(uint index, out IntPtr output);
        int GetDesc(out AdapterDesc description);
        int CheckInterfaceSupport(ref Guid iid, out long umdVersion);
        // IDXGIAdapter1
        int GetDesc1(out AdapterDesc1 description);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}
