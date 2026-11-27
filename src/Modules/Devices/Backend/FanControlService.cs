using Seein.Core;
using Seein.Core.Sensors;
using Seein.Core.Thermal;
using System.ServiceProcess;

namespace Seein.Service;

public sealed class FanControlService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FanRampLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EmergencyRampLimit = TimeSpan.FromSeconds(8);
    private const int EmergencyCpuTemperature = 90;
    private readonly object _sync = new();
    private readonly Dictionary<string, ChannelRuntime> _runtime = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<FanControlService> logger;
    private readonly PanelState state;
    private readonly TimeProvider _clock;
    private readonly Func<bool> _msiProbe;
    private IFanControlDevice? _device;
    private FanSettings _settings = FanDefaults.DefaultSettings();
    private DateTimeOffset? _lastWriteAt;
    private string? _lastError;
    private bool _msiFanServiceRunning;

    private sealed class ChannelRuntime
    {
        public double? LastWritten;
        public double? Target;
        public DateTimeOffset LastWriteAt;
        public DateTimeOffset LastVerifiedSample;
        public DateTimeOffset LastRestoreAttempt;
        public double? CoolingReference;
        public double? SecondaryReference;
        public int VerifyFailures;
        public double? BestPwmError;
        public bool Verified;
        public bool Emergency;
        public bool Blocked;
        public bool RestorePending;
        public string? Error;
        public string? BlockReason;
        public readonly Queue<FanTrendSample> Trend = new();
    }

    public FanControlService(ILogger<FanControlService> logger, PanelState state,
        TimeProvider? clock = null, Func<bool>? msiProbe = null)
    {
        this.logger = logger;
        this.state = state;
        _clock = clock ?? TimeProvider.System;
        _msiProbe = msiProbe ?? IsMsiFanServiceRunning;
    }

    public FanSettings Settings { get { lock (_sync) return _settings; } }

    public string? RestoreError
    {
        get
        {
            lock (_sync)
            {
                var errors = _runtime.Values.Where(runtime => runtime.RestorePending).Select(runtime => runtime.Error).ToArray();
                return errors.Length == 0 ? null : string.Join(" · ", errors);
            }
        }
    }

    public void Attach(IFanControlDevice device)
    {
        lock (_sync) _device = device;
    }

    public void Initialize(FanSettings? settings)
    {
        lock (_sync) _settings = FanDefaults.Normalize(settings);
    }

    public FanSettings ApplySettings(FanSettings settings)
    {
        lock (_sync)
        {
            _settings = FanDefaults.Normalize(settings);
            _lastError = null;
            if (!_settings.Enabled) RestoreAll();
            else
            {
                foreach (var runtime in _runtime.Values)
                {
                    if (!runtime.RestorePending)
                    {
                        runtime.Blocked = false;
                        runtime.Error = null;
                        runtime.BlockReason = null;
                    }
                    runtime.CoolingReference = null;
                    runtime.SecondaryReference = null;
                }
            }
            return _settings;
        }
    }

    public string? RestoreAll()
    {
        lock (_sync)
        {
            var failures = new List<string>();
            foreach (var (id, runtime) in _runtime)
                if (!Restore(id, runtime, _clock.GetUtcNow()))
                    failures.Add($"{id}：{runtime.Error}");
            return failures.Count == 0 ? null : string.Join(" · ", failures);
        }
    }

    public FanStatus GetStatus()
    {
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            var snapshot = state.Snapshot().Sensors;
            var fresh = FanPolicy.IsFresh(snapshot, now);
            var profile = ActiveProfile();
            var channels = new List<FanChannelState>();
            foreach (var descriptor in _device?.DescribeFanChannels() ?? [])
            {
                var setting = FanDefaults.FindChannel(profile, descriptor.Id);
                var source = setting?.Source ?? profile.Source;
                var (temperature, secondary) = fresh ? FanPolicy.ReadTemperatures(source, snapshot) : (null, null);
                _runtime.TryGetValue(descriptor.Id, out var runtime);
                var rpm = fresh ? ReadRpm(descriptor.Id, snapshot) : null;
                var owner = runtime?.RestorePending == true ? "restore-failed"
                    : runtime?.Verified == true && runtime.VerifyFailures == 0 && fresh ? "seein"
                    : runtime?.LastWritten is not null ? "verifying" : "firmware";
                channels.Add(new(descriptor.Id, descriptor.Label, descriptor.RawName, rpm ?? 0,
                    fresh ? ReadPwm(descriptor.Id, snapshot) : null,
                    descriptor.ControlAvailable && runtime?.Blocked != true,
                    Math.Max(descriptor.FloorPercent, setting?.FloorPercent ?? 0),
                    Math.Min(descriptor.CeilingPercent, setting?.CeilingPercent ?? 100),
                    (setting?.Mode ?? FanChannelMode.Auto).ToString().ToLowerInvariant(),
                    runtime?.Target, temperature, runtime?.Error, owner, rpm.HasValue, source, secondary,
                    runtime?.Trend.Where(sample => now - sample.At <= TimeSpan.FromMinutes(10)).ToArray()));
            }
            return new(_device is not null, _settings.Enabled, _settings.ActiveProfileId, profile.Source, channels,
                _msiFanServiceRunning, _device is null ? "未检测到主板风扇控制器" : null, _lastError, _lastWriteAt);
        }
    }

    public bool TrySetMsiFanServices(bool stop, out string? error)
    {
        lock (_sync) return SetMsiFanServices(stop, out error);
    }

    private bool SetMsiFanServices(bool stop, out string? error)
    {
        error = null;
        try
        {
            if (!stop)
            {
                _msiFanServiceRunning = true;
                foreach (var (id, runtime) in _runtime.Where(entry => entry.Value.LastWritten is not null))
                    Block(id, runtime, "正在恢复微星风扇服务。", _clock.GetUtcNow());
                if (RestoreError is { } restoreError)
                    throw new InvalidOperationException(restoreError);
            }
            var services = FindMsiFanServices();
            try
            {
                foreach (var service in services)
                {
                    if (stop && service.Status == ServiceControllerStatus.Running) service.Stop();
                    else if (!stop && service.Status == ServiceControllerStatus.Stopped) service.Start();
                }
                _msiFanServiceRunning = _msiProbe();
                return true;
            }
            finally
            {
                foreach (var service in services) service.Dispose();
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _lastError = ex.Message;
            logger.LogWarning(ex, "Toggling the MSI fan services failed");
            return false;
        }
    }

    private static List<ServiceController> FindMsiFanServices()
    {
        var result = new List<ServiceController>();
        foreach (var service in ServiceController.GetServices())
        {
            var name = service.ServiceName;
            if (name.StartsWith("MSI_Case_Service", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("MSI_Center_Service", StringComparison.OrdinalIgnoreCase))
                result.Add(service);
            else service.Dispose();
        }
        return result;
    }

    private static bool IsMsiFanServiceRunning()
    {
        try
        {
            var services = FindMsiFanServices();
            try
            {
                return services.Any(service => service.Status is ServiceControllerStatus.Running
                    or ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending);
            }
            finally
            {
                foreach (var service in services) service.Dispose();
            }
        }
        catch
        {
            return true;
        }
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            do
            {
                try { Tick(); }
                catch (Exception ex)
                {
                    lock (_sync) _lastError = ex.Message;
                    logger.LogWarning(ex, "Fan control tick failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { RestoreAll(); }
    }

    public void Tick()
    {
        lock (_sync)
        {
            _msiFanServiceRunning = _msiProbe();
            if (_device is null) return;
            var now = _clock.GetUtcNow();
            var snapshot = state.Snapshot().Sensors;
            var fresh = FanPolicy.IsFresh(snapshot, now);
            var profile = ActiveProfile();
            var descriptors = _device.DescribeFanChannels();
            foreach (var (id, runtime) in _runtime)
                if (!descriptors.Any(descriptor => StringComparer.OrdinalIgnoreCase.Equals(descriptor.Id, id)))
                    RestoreWhenDue(id, runtime, now);

            foreach (var descriptor in descriptors)
            {
                if (!_runtime.TryGetValue(descriptor.Id, out var runtime))
                    _runtime[descriptor.Id] = runtime = new();
                var channel = FanDefaults.FindChannel(profile, descriptor.Id);
                var source = channel?.Source ?? profile.Source;
                var (primary, secondary) = fresh ? FanPolicy.ReadTemperatures(source, snapshot) : (null, null);
                var rpm = fresh ? ReadRpm(descriptor.Id, snapshot) : null;

                Drive(descriptor, runtime, channel, source, primary, secondary, rpm, snapshot, now);
                while (runtime.Trend.TryPeek(out var oldest) && now - oldest.At >= TimeSpan.FromMinutes(10))
                    runtime.Trend.Dequeue();
                if (channel is not null && (runtime.Trend.Count == 0 || runtime.Trend.Last().At < now))
                    runtime.Trend.Enqueue(new(now, primary, runtime.Target, rpm, secondary));
            }
        }
    }

    private void Drive(FanChannelDescriptor descriptor, ChannelRuntime runtime, FanChannelSettings? channel,
        FanTemperatureSource source, double? primary, double? secondary, int? rpm, SensorSnapshot snapshot, DateTimeOffset now)
    {
        var id = descriptor.Id;
        if (runtime.RestorePending)
        {
            RestoreWhenDue(id, runtime, now);
            return;
        }
        if (!_settings.Enabled || channel is null || channel.Mode == FanChannelMode.Auto || !descriptor.ControlAvailable)
        {
            RestoreWhenDue(id, runtime, now);
            return;
        }
        if (_msiFanServiceRunning)
        {
            Block(id, runtime, "检测到微星风扇服务。暂停后重新应用才能接管。", now);
            return;
        }
        if (runtime.Blocked) return;

        var floor = Math.Max(Math.Max(FanDefaults.FloorFor(id), descriptor.FloorPercent), channel.FloorPercent);
        var ceiling = Math.Min(descriptor.CeilingPercent, channel.CeilingPercent);
        var target = FanPolicy.Evaluate(channel, source, primary, secondary, floor, ceiling);
        var cpu = FanPolicy.IsFresh(snapshot, now) ? FanPolicy.Temperature(snapshot, SensorKeys.CpuTemperature) : null;
        if (target is null || cpu is null)
        {
            const string unavailable = "所需温度传感器离线或读数过期。";
            if (runtime.LastWritten is null) runtime.Error = unavailable;
            else Block(id, runtime, unavailable + "检查后重新应用。", now);
            return;
        }

        var emergency = cpu >= EmergencyCpuTemperature;
        var enteringEmergency = emergency && !runtime.Emergency;
        var pending = Verify(descriptor, runtime, rpm, snapshot, now);
        if (runtime.Blocked || (pending && !enteringEmergency)) return;

        var requested = emergency ? 100 : target.Value;
        if (!emergency && runtime.LastWritten is double last && channel.Mode == FanChannelMode.Curve)
        {
            var hysteresis = Math.Max(0, channel.Curve?.HysteresisC ?? 3);
            var primaryCooled = runtime.CoolingReference - primary >= hysteresis;
            var secondaryCooled = runtime.SecondaryReference - secondary >= hysteresis;
            // A newly applied curve has no temperature decision yet. Let it reach its new
            // target before establishing a cooling band; otherwise a lower curve can latch
            // forever at the previous PWM (or stop after only the first limited step).
            if (requested < last && runtime.CoolingReference.HasValue && !(primaryCooled || secondaryCooled || runtime.Emergency))
                requested = last;
            var step = channel.Curve?.MaxStepPercent ?? 20;
            requested = FanCurveMath.LimitStep(last, requested, requested < last ? Math.Min(8, Math.Max(1, step)) : step);
        }
        runtime.Target = requested;
        if (!enteringEmergency && !FanCurveMath.ShouldWrite(runtime.LastWritten, runtime.LastWriteAt, requested, now,
                TickInterval, requested < (runtime.LastWritten ?? requested) ? 1 : 3))
            return;

        // A failed hardware call may still have changed the controller.
        var previousWrite = runtime.LastWritten;
        runtime.LastWritten = requested;
        string? writeError;
        try
        {
            if (!_device!.TrySetFanPwm(id, requested, out writeError))
            {
                Block(id, runtime, writeError ?? "主板拒绝了 PWM 写入", now);
                return;
            }
        }
        catch (Exception ex)
        {
            Block(id, runtime, ex.Message, now);
            return;
        }
        runtime.LastWriteAt = now;
        runtime.LastVerifiedSample = snapshot.Timestamp;
        runtime.VerifyFailures = 0;
        runtime.BestPwmError = ReadPwm(id, snapshot) is double priorPwm ? Math.Abs(priorPwm - requested) : null;
        runtime.Verified = false;
        if (requested >= (previousWrite ?? requested) || requested == target)
        {
            runtime.CoolingReference = primary;
            runtime.SecondaryReference = secondary;
        }
        runtime.Emergency = emergency;
        runtime.Error = null;
        _lastWriteAt = now;
    }

    // Wait for new samples collected after the write. Verify again while SEEIN owns the channel.
    private bool Verify(FanChannelDescriptor descriptor, ChannelRuntime runtime, int? rpm,
        SensorSnapshot snapshot, DateTimeOffset now)
    {
        if (runtime.LastWritten is not double written) return false;
        if (snapshot.Timestamp <= runtime.LastVerifiedSample || snapshot.Timestamp < runtime.LastWriteAt + TickInterval)
        {
            if (now - runtime.LastWriteAt >= FanPolicy.MaximumSampleAge && !runtime.Verified)
                Block(descriptor.Id, runtime, "PWM 写入没有取得新的回读样本。", now);
            return !runtime.Verified || runtime.VerifyFailures > 0;
        }
        runtime.LastVerifiedSample = snapshot.Timestamp;
        var pwm = ReadPwm(descriptor.Id, snapshot);
        var stopped = rpm == 0 && (descriptor.FloorPercent > 0 || written >= 30);
        if (pwm is double observed && rpm.HasValue && !stopped)
        {
            var error = Math.Abs(observed - written);
            if (error <= 8)
            {
                runtime.VerifyFailures = 0;
                runtime.Verified = true;
                return false;
            }
            // Some Super I/O channels ramp to a new duty instead of reporting it immediately.
            // Extend initial verification only while the fresh hardware samples are converging.
            var limit = runtime.Emergency ? EmergencyRampLimit : FanRampLimit;
            if (!runtime.Verified && now - runtime.LastWriteAt < limit && runtime.BestPwmError is double best && error <= best - 0.75)
            {
                runtime.BestPwmError = error;
                runtime.VerifyFailures = 0;
                return true;
            }
        }

        if (++runtime.VerifyFailures >= 2)
            Block(descriptor.Id, runtime, stopped ? "写入后风扇连续回读为 0 RPM。"
                : "PWM 或 RPM 连续两次未确认，可能有其他控制器覆盖。", now);
        return true;
    }

    private void Block(string id, ChannelRuntime runtime, string reason, DateTimeOffset now)
    {
        runtime.Blocked = true;
        runtime.Error = reason;
        runtime.BlockReason = reason;
        _lastError = reason;
        Restore(id, runtime, now);
        logger.LogWarning("Fan channel {Channel} paused: {Reason}", id, reason);
    }

    private void RestoreWhenDue(string id, ChannelRuntime runtime, DateTimeOffset now)
    {
        if (now - runtime.LastRestoreAttempt >= TimeSpan.FromSeconds(10)) Restore(id, runtime, now);
    }

    private bool Restore(string id, ChannelRuntime runtime, DateTimeOffset now)
    {
        runtime.Target = null;
        if (runtime.LastWritten is null && !runtime.RestorePending) return true;
        runtime.LastRestoreAttempt = now;
        if (_device is null)
        {
            runtime.RestorePending = true;
            runtime.Error = "主板控制器离线，等待交还 BIOS。";
            _lastError = runtime.Error;
            return false;
        }
        string? error;
        try
        {
            if (_device.TryRestoreFan(id, out error))
            {
                runtime.LastWritten = null;
                runtime.Verified = false;
                runtime.Emergency = false;
                runtime.RestorePending = false;
                runtime.Error = runtime.BlockReason;
                runtime.CoolingReference = null;
                runtime.SecondaryReference = null;
                return true;
            }
        }
        catch (Exception ex) { error = ex.Message; }
        runtime.RestorePending = true;
        runtime.Blocked = true;
        runtime.Error = $"交还 BIOS 失败：{error ?? "控制器未确认"}。";
        _lastError = runtime.Error;
        return false;
    }

    private FanProfile ActiveProfile() =>
        _settings.Profiles!.First(profile => profile.Id == _settings.ActiveProfileId);

    public static double? ResolveTemperature(FanTemperatureSource source, SensorSnapshot snapshot) =>
        FanPolicy.ReadTemperatures(source, snapshot).Primary;

    private static int? ReadRpm(string channelId, SensorSnapshot snapshot)
    {
        var rpm = snapshot.Get($"fan.{channelId}.rpm");
        var alias = channelId switch
        {
            "cpu-fan" => SensorKeys.FanRadiator,
            "pump-fan" or "pump-fan-1" or "pump" => SensorKeys.FanPump,
            "system-fan-4" => SensorKeys.FanCase,
            _ => null
        };
        rpm ??= alias is null ? null : snapshot.Get(alias);
        return rpm is >= 0 and <= 100000 ? (int)Math.Round(rpm.Value) : null;
    }

    private static double? ReadPwm(string channelId, SensorSnapshot snapshot) =>
        snapshot.Get($"fan.{channelId}.pwm") is >= 0 and <= 100 and var pwm ? pwm : null;
}
