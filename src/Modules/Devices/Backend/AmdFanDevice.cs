using System.Diagnostics;
using System.Runtime.InteropServices;
using Seein.Core.Thermal;

namespace Seein.Service;

internal sealed record AmdFanSnapshot(string Name, string Identity, string DriverVersion, bool Factory,
    GpuFanRange Temperature, GpuFanRange Speed, FanCurvePoint[] Points, bool? ZeroRpm);

internal interface IAmdFanDevice : IDisposable
{
    AmdFanSnapshot Read();
    (int? Rpm, double? Temperature) ReadMetrics();
    void ValidateCurve(IReadOnlyList<FanCurvePoint> points);
    void SetCurve(IReadOnlyList<FanCurvePoint> points);
    void SetZeroRpm(bool enabled);
}

// ADLX C ABI, pinned to GPUOpen ADLX 32b5a740d42295c5dfe9026b9f52683da0f3af91.
// Slots come from ISystem.h, IGPUTuning.h, IGPUManualFanTuning.h and IPerformanceMonitoring.h.
// ADLX interfaces use the Windows x64 ABI. The system pointer is a non-refcounted singleton.
internal sealed class AmdFanDevice : IAmdFanDevice
{
    private nint _module;
    private nint _system;
    private nint _gpu;
    private nint _tuning;
    private nint _fan;
    private nint _performance;
    private string _name = "";
    private string _identity = "";
    private readonly string _driverVersion;

    internal AmdFanDevice()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("ADLX 需要 64 位 Windows。");
        var path = Path.Combine(Environment.SystemDirectory, "amdadlx64.dll");
        _driverVersion = FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim() ?? "";
        _module = NativeLibrary.Load(path);
        try
        {
            Check(Export<QueryVersion>("ADLXQueryFullVersion")(out var version), "读取 ADLX 版本");
            Check(Export<Initialize>("ADLXInitialize")(version, out _system), "初始化 ADLX");
            FindGpu();
            _tuning = GetObject(_system, 8, "获取调校接口");
            Check(Slot<GpuBool>(_tuning, 10)(_tuning, _gpu, out var supported), "检测风扇曲线支持");
            if (supported == 0) throw new InvalidOperationException("AMD 驱动未开放手动风扇曲线。");
            Check(Slot<GpuObject>(_tuning, 16)(_tuning, _gpu, out var fan), "获取风扇控制接口");
            using var baseFan = new Handle(fan);
            Check(Slot<QueryInterface>(baseFan.Value, 2)(baseFan.Value, "IADLXManualFanTuning", out _fan), "读取风扇控制接口");
            if (Slot<ObjectOut>(_system, 9)(_system, out _performance) != 0) Release(ref _performance);
        }
        catch { Dispose(); throw; }
    }

    private void FindGpu()
    {
        using var list = new Handle(GetObject(_system, 1, "枚举 AMD 显卡"));
        var begin = Slot<Size>(list.Value, 5)(list.Value);
        var end = Slot<Size>(list.Value, 6)(list.Value);
        for (var index = begin; index < end; index++)
        {
            using var gpu = new Handle(GetAt(list.Value, index));
            var name = ReadString(gpu.Value, 7, "读取 AMD 显卡型号");
            if (!name.Contains("RX 9070 XT", StringComparison.OrdinalIgnoreCase)) continue;
            if (_gpu != 0) throw new InvalidOperationException("检测到多张 RX 9070 XT，无法唯一定位调校目标。");
            _identity = ReadString(gpu.Value, 9, "读取 AMD 设备标识");
            Check(Slot<ReadInt>(gpu.Value, 18)(gpu.Value, out var id), "读取 AMD 适配器编号");
            _identity = $"{_identity}|{id}";
            _name = name;
            _gpu = gpu.Detach();
        }
        if (_gpu == 0) throw new InvalidOperationException("AMD ADLX 未发现 RX 9070 XT。");
    }

    public AmdFanSnapshot Read()
    {
        Check(Slot<GpuBool>(_tuning, 4)(_tuning, _gpu, out var factory), "读取 AMD 调校状态");
        Check(Slot<Ranges>(_fan, 3)(_fan, out var speed, out var temperature), "读取曲线范围");
        Check(Slot<ReadBool>(_fan, 8)(_fan, out var supportsZero), "检查 Zero RPM 支持");
        bool? zero = null;
        if (supportsZero != 0)
        {
            Check(Slot<ReadBool>(_fan, 9)(_fan, out var value), "读取 Zero RPM");
            zero = value != 0;
        }
        using var list = new Handle(GetObject(_fan, 4, "读取驱动曲线"));
        var points = new FanCurvePoint[PointCount(list.Value)];
        for (uint index = 0; index < points.Length; index++)
        {
            using var point = new Handle(GetAt(list.Value, index));
            Check(Slot<ReadInt>(point.Value, 5)(point.Value, out var temp), "读取曲线温度");
            Check(Slot<ReadInt>(point.Value, 3)(point.Value, out var duty), "读取曲线转速");
            points[index] = new(temp, duty);
        }
        return new(_name, _identity, _driverVersion, factory != 0, temperature.ToModel(), speed.ToModel(), points, zero);
    }

    public (int? Rpm, double? Temperature) ReadMetrics()
    {
        if (_performance == 0 || Slot<GpuObject>(_performance, 18)(_performance, _gpu, out var value) != 0 || value == 0)
            return (null, null);
        using var metrics = new Handle(value);
        int? rpm = Slot<ReadInt>(metrics.Value, 11)(metrics.Value, out var speed) == 0 && speed >= 0 ? speed : null;
        double? temperature = Slot<ReadDouble>(metrics.Value, 7)(metrics.Value, out var degrees) == 0 && degrees is > 0 and <= 130 ? degrees : null;
        return (rpm, temperature);
    }

    public void ValidateCurve(IReadOnlyList<FanCurvePoint> points)
    {
        using var list = new Handle(PrepareCurve(points));
    }

    public void SetCurve(IReadOnlyList<FanCurvePoint> points)
    {
        using var list = new Handle(PrepareCurve(points));
        Check(Slot<SetStates>(_fan, 7)(_fan, list.Value), "应用 AMD 风扇曲线");
    }

    private nint PrepareCurve(IReadOnlyList<FanCurvePoint> points)
    {
        using var list = new Handle(GetObject(_fan, 5, "分配 AMD 曲线点"));
        if (PointCount(list.Value) != points.Count) throw new InvalidOperationException("曲线点数与 AMD 驱动要求不同。");
        for (uint index = 0; index < points.Count; index++)
        {
            using var point = new Handle(GetAt(list.Value, index));
            var requested = points[(int)index];
            Check(Slot<WriteInt>(point.Value, 6)(point.Value, (int)requested.TemperatureC), "设置曲线温度草稿");
            Check(Slot<WriteInt>(point.Value, 4)(point.Value, (int)requested.Percent), "设置曲线转速草稿");
        }
        var result = Slot<ValidateStates>(_fan, 6)(_fan, list.Value, out var invalidIndex);
        Check(result, $"验证 AMD 曲线（失败位置 {invalidIndex + 1}）");
        return list.Detach();
    }

    public void SetZeroRpm(bool enabled) =>
        Check(Slot<WriteBool>(_fan, 10)(_fan, enabled ? (byte)1 : (byte)0), "设置 Zero RPM");

    private static int PointCount(nint list)
    {
        var count = Slot<Size>(list, 3)(list);
        return count is >= 2 and <= 32 ? (int)count : throw new InvalidOperationException("AMD 返回了无效的曲线点数。");
    }

    private static nint GetAt(nint list, uint index)
    {
        Check(Slot<At>(list, 11)(list, index, out var item), $"读取 ADLX 列表的第 {index + 1} 项");
        return RequirePointer(item);
    }

    private static nint GetObject(nint instance, int slot, string action)
    {
        Check(Slot<ObjectOut>(instance, slot)(instance, out var pointer), action);
        return RequirePointer(pointer);
    }

    private static string ReadString(nint instance, int slot, string action) =>
        Marshal.PtrToStringAnsi(GetObject(instance, slot, action)) ?? "";

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));

    private static T Slot<T>(nint instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(RequirePointer(instance)), index * IntPtr.Size));

    private static nint RequirePointer(nint pointer) =>
        pointer != 0 ? pointer : throw new InvalidOperationException("ADLX 返回了空接口。");

    private static void Check(int result, string action)
    {
        if (result is 0 or 1 or 2) return;
        throw new InvalidOperationException(result == 18
            ? $"{action}需要重置整个 AMD 调校配置，已阻止。"
            : $"{action}失败（ADLX {result}）。");
    }

    private static void Release(ref nint pointer)
    {
        if (pointer == 0) return;
        Slot<ReleaseInterface>(pointer, 1)(pointer);
        pointer = 0;
    }

    public void Dispose()
    {
        Release(ref _performance);
        Release(ref _fan);
        Release(ref _tuning);
        Release(ref _gpu);
        if (_module == 0) return;
        if (_system != 0) Export<Terminate>("ADLXTerminate")();
        NativeLibrary.Free(_module);
        _system = _module = 0;
    }

    private sealed class Handle(nint pointer) : IDisposable
    {
        private nint _pointer = RequirePointer(pointer);
        public nint Value => _pointer;
        public nint Detach()
        {
            var value = _pointer;
            _pointer = 0;
            return value;
        }
        public void Dispose() => Release(ref _pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IntRange
    {
        public int Minimum;
        public int Maximum;
        public int Step;
        public readonly GpuFanRange ToModel() => new(Minimum, Maximum, Step);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int QueryVersion(out ulong version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize(ulong version, out nint system);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Terminate();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseInterface(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Size(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int At(nint self, uint index, out nint item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ObjectOut(nint self, out nint item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GpuObject(nint self, nint gpu, out nint item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GpuBool(nint self, nint gpu, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterface(nint self, [MarshalAs(UnmanagedType.LPWStr)] string name, out nint item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Ranges(nint self, out IntRange speed, out IntRange temperature);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadBool(nint self, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WriteBool(nint self, byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadInt(nint self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadDouble(nint self, out double value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WriteInt(nint self, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ValidateStates(nint self, nint list, out int index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetStates(nint self, nint list);
}
