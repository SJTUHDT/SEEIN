using LibreHardwareMonitor.Hardware;
using Seein.Core.Thermal;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Seein.Core.Sensors;

public interface ISensorCollector : IDisposable
{
    SensorSnapshot Read();
}

public sealed class HardwareSensorCollector : ISensorCollector, IFanControlDevice
{
    private readonly Computer _computer;
    private readonly object _hardwareLock = new();
    private readonly UpdateVisitor _visitor = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly SystemVolumeReader _volume = new();
    private DateTimeOffset _lastNetworkAt = DateTimeOffset.Now;
    private long _lastReceived;
    private long _lastSent;

    public HardwareSensorCollector()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsPsuEnabled = true
        };
        _computer.Open();
        _computer.Accept(_visitor);
        (_lastReceived, _lastSent) = ReadNetworkCounters();
    }

    public SensorSnapshot Read()
    {
        lock (_hardwareLock) return ReadSensors();
    }

    private SensorSnapshot ReadSensors()
    {
        _computer.Accept(_visitor);
        var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        var hardwareItems = Flatten(_computer.Hardware).ToArray();
        foreach (var hardware in hardwareItems)
            foreach (var sensor in hardware.Sensors)
                MapSensor(hardware, sensor, values);
        // The CPU object only exposes per-core VID; the board reports the real Vcore.
        var vcore = hardwareItems
            .SelectMany(item => item.Sensors)
            .FirstOrDefault(sensor => sensor.SensorType == SensorType.Voltage &&
                                      string.Equals(sensor.Name, "Vcore", StringComparison.OrdinalIgnoreCase));
        if (vcore?.Value is float vcoreValue) values[SensorKeys.CpuVoltage] = vcoreValue;

        var boardTemp = hardwareItems
            .Where(item => item.HardwareType is not (HardwareType.Cpu or HardwareType.GpuAmd or HardwareType.GpuNvidia
                or HardwareType.GpuIntel or HardwareType.Storage))
            .SelectMany(item => item.Sensors)
            .Where(sensor => sensor.SensorType == SensorType.Temperature && sensor.Value is > 0)
            .OrderByDescending(sensor => sensor.Name.Contains("system", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (boardTemp?.Value is float boardValue) values[SensorKeys.MotherboardTemp] = boardValue;

        var storageTemp = hardwareItems
            .Where(item => item.HardwareType == HardwareType.Storage)
            .SelectMany(item => item.Sensors)
            .FirstOrDefault(sensor => sensor.SensorType == SensorType.Temperature && sensor.Value is > 0);
        if (storageTemp?.Value is float storageValue) values[SensorKeys.StorageTemp] = storageValue;

        // Confirmed wiring on this board: CPU_FAN drives the 360 radiator fans,
        // PUMP_FAN the water-block pump and SYS_FAN4 the front/bottom/rear case fans.
        foreach (var sensor in hardwareItems
                     .Where(item => item.HardwareType is not (HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel))
                     .SelectMany(item => item.Sensors)
                     .Where(sensor => sensor.SensorType == SensorType.Fan && sensor.Value is >= 0))
        {
            values[$"fan.{FanChannelId(sensor.Name)}.rpm"] = sensor.Value;
            var fanName = sensor.Name.ToLowerInvariant();
            if (fanName.Contains("cpu fan")) values[SensorKeys.FanRadiator] = sensor.Value;
            else if (fanName.Contains("pump")) values[SensorKeys.FanPump] = sensor.Value;
            else if (fanName.Contains("system fan #4")) values[SensorKeys.FanCase] = sensor.Value;
        }

        // PWM duty per channel, consumed by the fan control page.
        foreach (var sensor in hardwareItems
                     .Where(item => item.HardwareType is not (HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel))
                     .SelectMany(item => item.Sensors)
                     .Where(sensor => sensor.SensorType == SensorType.Control && sensor.Value is not null))
            values[$"fan.{FanChannelId(sensor.Name)}.pwm"] = sensor.Value;

        var cpuLoad = ReadCpuTotalLoad(hardwareItems);
        if (cpuLoad is not null) values[SensorKeys.CpuLoad] = cpuLoad;
        var discreteGpuLoad = ReadDiscreteGpuLoad(hardwareItems);
        if (discreteGpuLoad is not null) values[SensorKeys.GpuLoad] = discreteGpuLoad;

        var now = DateTimeOffset.Now;
        var (received, sent) = ReadNetworkCounters();
        var seconds = Math.Max(.001, (now - _lastNetworkAt).TotalSeconds);
        values[SensorKeys.NetworkDown] = Math.Max(0, received - _lastReceived) / seconds;
        values[SensorKeys.NetworkUp] = Math.Max(0, sent - _lastSent) / seconds;
        values[SensorKeys.Uptime] = Environment.TickCount64 / 1000d;
        var systemDriveLoad = ReadSystemDriveLoad();
        if (systemDriveLoad is not null) values[SensorKeys.SystemDriveLoad] = systemDriveLoad;
        var volume = _volume.ReadPercent();
        if (volume is not null) values[SensorKeys.SystemVolume] = volume;
        _lastReceived = received; _lastSent = sent; _lastNetworkAt = now;
        return new SensorSnapshot(now, values);
    }

    // ------------------------------------------------------------------ fan control (1.3.0)

    /// <summary>Stable channel id used by settings, IPC and the UI ("CPU Fan" becomes "cpu-fan").</summary>
    public static string FanChannelId(string rawName)
    {
        var builder = new System.Text.StringBuilder(rawName.Length);
        var lastDash = false;
        foreach (var character in rawName.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                lastDash = false;
            }
            else if (!lastDash && builder.Length > 0)
            {
                builder.Append('-');
                lastDash = true;
            }
        }
        return builder.ToString().Trim('-');
    }

    private IEnumerable<(string Id, string RawName, ISensor Sensor)> ControlSensors()
    {
        foreach (var hardware in Flatten(_computer.Hardware)
                     .Where(item => item.HardwareType is not (HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)))
            foreach (var sensor in hardware.Sensors)
                if (sensor.SensorType == SensorType.Control)
                    yield return (FanChannelId(sensor.Name), sensor.Name, sensor);
    }

    public IReadOnlyList<FanChannelDescriptor> DescribeFanChannels()
    {
        lock (_hardwareLock) return DescribeChannels();
    }

    private List<FanChannelDescriptor> DescribeChannels()
    {
        var channels = new List<FanChannelDescriptor>();
        foreach (var (id, rawName, sensor) in ControlSensors())
        {
            var label = FanDefaults.LabelFor(id);
            channels.Add(new FanChannelDescriptor(id, rawName, label, sensor.Control is not null,
                FanDefaults.FloorFor(id), FanDefaults.CeilingFor(id)));
        }
        foreach (var sensor in Flatten(_computer.Hardware)
                     .Where(item => item.HardwareType is not (HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel))
                     .SelectMany(item => item.Sensors).Where(sensor => sensor.SensorType == SensorType.Fan))
        {
            var id = FanChannelId(sensor.Name);
            if (channels.All(channel => channel.Id != id))
                channels.Add(new(id, sensor.Name, FanDefaults.LabelFor(id), false, FanDefaults.FloorFor(id), 100));
        }
        return channels;
    }

    public bool TrySetFanPwm(string channelId, double percent, out string? error)
    {
        lock (_hardwareLock) return SetPwm(channelId, percent, out error);
    }

    private bool SetPwm(string channelId, double percent, out string? error)
    {
        error = null;
        var target = ControlSensors().FirstOrDefault(entry => entry.Id == channelId);
        if (target.Sensor is null)
        {
            error = $"风扇通道不存在：{channelId}";
            return false;
        }
        if (target.Sensor.Control is null)
        {
            error = "该通道不支持软件调速";
            return false;
        }
        try
        {
            target.Sensor.Control.SetSoftware((float)Math.Clamp(percent, 0, 100));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryRestoreFan(string channelId, out string? error)
    {
        lock (_hardwareLock) return RestoreFan(channelId, out error);
    }

    private bool RestoreFan(string channelId, out string? error)
    {
        error = null;
        var target = ControlSensors().FirstOrDefault(entry => entry.Id == channelId);
        if (target.Sensor?.Control is null)
        {
            error = "该通道不支持软件调速";
            return false;
        }
        try
        {
            target.Sensor.Control.SetDefault();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void RestoreAllFans()
    {
        lock (_hardwareLock)
        foreach (var (_, _, sensor) in ControlSensors())
        {
            try { sensor.Control?.SetDefault(); }
            catch { }
        }
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> hardware)
    {
        foreach (var item in hardware)
        {
            yield return item;
            foreach (var child in Flatten(item.SubHardware)) yield return child;
        }
    }

    private static void MapSensor(IHardware hardware, ISensor sensor, Dictionary<string, double?> values)
    {
        if (sensor.Value is not float value) return;
        var name = sensor.Name.ToLowerInvariant();
        bool cpu = hardware.HardwareType == HardwareType.Cpu;
        bool gpu = hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;

        if (cpu)
        {
            if (sensor.SensorType == SensorType.Temperature && (name.Contains("package") || !values.ContainsKey(SensorKeys.CpuTemperature))) SetPreferred(values, SensorKeys.CpuTemperature, value, name.Contains("package"));
            if (sensor.SensorType == SensorType.Power && name.Contains("package")) values[SensorKeys.CpuPower] = value;
            if (sensor.SensorType == SensorType.Clock && name.Contains("core")) Average(values, SensorKeys.CpuClock, value);
            if (sensor.SensorType == SensorType.Voltage && name.Contains("core")) Average(values, SensorKeys.CpuVoltage, value);
        }
        else if (gpu && !IsIntegratedGpu(hardware))
        {
            if (sensor.SensorType == SensorType.Temperature && (name.Contains("core") || name.Contains("gpu"))) SetPreferred(values, SensorKeys.GpuTemperature, value, name.Contains("core"));
            // "GPU Memory" is also a load sensor and is commonly 0%.  The final selection below
            // prefers the D3D 3D engine, then falls back to GPU Core when the driver does not expose it.
            if (sensor.SensorType == SensorType.Load && name.Contains("gpu core", StringComparison.OrdinalIgnoreCase)) values[SensorKeys.GpuLoad] = value;
            if (sensor.SensorType == SensorType.Power && (name.Contains("total") || name.Contains("board") || name.Contains("package"))) SetPreferred(values, SensorKeys.GpuPower, value, name.Contains("total") || name.Contains("board"));
            if (sensor.SensorType == SensorType.Clock && name.Contains("core")) values[SensorKeys.GpuClock] = value;
            if (sensor.SensorType == SensorType.Voltage && (name.Contains("core") || name.Contains("gpu"))) values[SensorKeys.GpuVoltage] = value;
            if (sensor.SensorType == SensorType.Fan) values[SensorKeys.GpuFan] = value;
            if (sensor.SensorType == SensorType.SmallData && name.Contains("memory") && name.Contains("used")) values[SensorKeys.GpuMemoryUsed] = value;
        }
        else if (hardware.HardwareType == HardwareType.Memory && sensor.SensorType == SensorType.Load)
            values[SensorKeys.MemoryLoad] = value;
        else if (hardware.HardwareType == HardwareType.Storage && sensor.SensorType == SensorType.Throughput)
        {
            if (name.Contains("read")) values[SensorKeys.DiskRead] = (values.GetValueOrDefault(SensorKeys.DiskRead) ?? 0) + value;
            if (name.Contains("write")) values[SensorKeys.DiskWrite] = (values.GetValueOrDefault(SensorKeys.DiskWrite) ?? 0) + value;
        }

        var safeHardware = Slug(hardware.Name);
        var safeSensor = Slug(sensor.Name);
        values[$"hardware.{safeHardware}.{sensor.SensorType.ToString().ToLowerInvariant()}.{safeSensor}"] = value;
    }

    private static bool IsIntegratedGpu(IHardware hardware) =>
        hardware.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
        || hardware.Name.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase)
        || hardware.Name.Contains("Integrated", StringComparison.OrdinalIgnoreCase);

    private static double? ReadCpuTotalLoad(IEnumerable<IHardware> hardwareItems)
    {
        var cpu = hardwareItems.FirstOrDefault(item => item.HardwareType == HardwareType.Cpu);
        if (cpu is null) return null;

        // "CPU Core Max" measures the busiest logical core.  It can be 90% while the 9800X3D's
        // total processor usage is only a few percent, so it must never be presented as CPU total.
        var total = cpu.Sensors.FirstOrDefault(sensor => sensor.SensorType == SensorType.Load
            && sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))?.Value;
        if (total is not null) return total;

        // Older drivers may not expose CPU Total.  Use the mean of logical cores, explicitly
        // excluding the driver's Core Max aggregate, as the closest equivalent.
        var cores = cpu.Sensors
            .Where(sensor => sensor.SensorType == SensorType.Load
                && sensor.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)
                && sensor.Value is not null)
            .Select(sensor => (double)sensor.Value!.Value)
            .ToArray();
        return cores.Length == 0 ? null : cores.Average();
    }

    private static double? ReadDiscreteGpuLoad(IEnumerable<IHardware> hardwareItems)
    {
        var selected = hardwareItems
            .Where(item => item.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
            .OrderByDescending(GetDiscreteGpuPriority)
            .FirstOrDefault();
        if (selected is null) return null;

        // GPU Core is a driver-wide aggregate.  On AMD it can include background engines and report
        // a persistent double-digit value at the desktop.  D3D 3D matches the visible render workload
        // (and Windows Task Manager's 3D graph) more closely for the panel's "GPU usage" reading.
        var d3d3D = selected.Sensors.FirstOrDefault(sensor => sensor.SensorType == SensorType.Load
            && sensor.Name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase))?.Value;
        return d3d3D ?? selected.Sensors.FirstOrDefault(sensor => sensor.SensorType == SensorType.Load
            && sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static double GetDedicatedMemoryMb(IHardware hardware) => hardware.Sensors
        .Where(sensor => sensor.SensorType == SensorType.SmallData && sensor.Value is not null
            && (sensor.Name.Contains("Dedicated Memory Total", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase)))
        .Select(sensor => (double)sensor.Value!.Value)
        .DefaultIfEmpty(0d)
        .Max();

    private static double GetDiscreteGpuPriority(IHardware hardware)
    {
        var name = hardware.Name;
        var modelLooksDiscrete = name.Contains(" RX ", StringComparison.OrdinalIgnoreCase)
            || name.Contains(" RTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains(" GTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains(" ARC ", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        return (modelLooksDiscrete ? 1_000_000_000d : 0d) + GetDedicatedMemoryMb(hardware);
    }

    private static void SetPreferred(Dictionary<string, double?> values, string key, double value, bool preferred)
    {
        if (preferred || !values.ContainsKey(key)) values[key] = value;
    }
    private static void Average(Dictionary<string, double?> values, string key, double value) => values[key] = values.TryGetValue(key, out var current) && current is not null ? (current.Value + value) / 2 : value;
    private static string Slug(string value) => string.Join('-', value.ToLowerInvariant().Split(Path.GetInvalidFileNameChars().Concat([' ', '/', '\\']).ToArray(), StringSplitOptions.RemoveEmptyEntries));

    private static (long Received, long Sent) ReadNetworkCounters()
    {
        long received = 0, sent = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try { var stats = nic.GetIPv4Statistics(); received += stats.BytesReceived; sent += stats.BytesSent; } catch (NetworkInformationException) { }
        }
        return (received, sent);
    }

    private static double? ReadSystemDriveLoad()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root)) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0) return null;
            return Math.Clamp((1d - ((double)drive.AvailableFreeSpace / drive.TotalSize)) * 100d, 0d, 100d);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        lock (_hardwareLock)
        {
            _volume.Dispose();
            _computer.Close();
            _uptime.Stop();
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (var child in hardware.SubHardware) child.Accept(this); }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
