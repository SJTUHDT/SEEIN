using Microsoft.Win32;
using Seein.Core;
using Seein.Core.Sensors;
using Seein.Core.Thermal;
using System.Text.Json;

namespace Seein.Service;

public sealed class GpuFanService : BackgroundService
{
    private const string RegistryPath = @"SOFTWARE\SEEIN\FanControl";
    private const string JournalKey = "GpuFanRecovery";
    private const string AcceptanceKey = "ValidatedManualFanDriver";
    private readonly object _gate = new();
    private readonly ILogger<GpuFanService> logger;
    private readonly PanelState panel;
    private readonly Func<IAmdFanDevice> _openDevice;
    private readonly Func<AmdFanSnapshot, bool> _isAccepted;
    private readonly Func<Recovery?> _readRecovery;
    private readonly Action<Recovery?> _writeRecovery;

    public GpuFanService(ILogger<GpuFanService> logger, PanelState panel)
        : this(logger, panel, () => new AmdFanDevice(), IsAccepted, ReadRegistryRecovery, WriteRegistryRecovery) { }

    internal GpuFanService(ILogger<GpuFanService> logger, PanelState panel, Func<IAmdFanDevice> openDevice,
        Func<AmdFanSnapshot, bool> isAccepted, Func<Recovery?> readRecovery, Action<Recovery?> writeRecovery)
    {
        this.logger = logger;
        this.panel = panel;
        _openDevice = openDevice;
        _isAccepted = isAccepted;
        _readRecovery = readRecovery;
        _writeRecovery = writeRecovery;
    }

    private readonly Queue<FanTrendSample> _trend = new();
    private IAmdFanDevice? _device;
    private GpuFanStatus _status = new("RX 9070 XT", "driver", "正在探测 AMD 显卡风扇。");
    private DateTimeOffset _retryAfter;
    private bool _owns;
    private bool _recoveryChecked;
    private string? _suspended;

    internal sealed record FanValues(FanCurvePoint[] Points, bool? ZeroRpm)
    {
        public bool Matches(FanValues other) => ZeroRpm == other.ZeroRpm && Points.SequenceEqual(other.Points);
        public static FanValues From(AmdFanSnapshot snapshot) => new(snapshot.Points, snapshot.ZeroRpm);
    }

    internal sealed record Recovery(string Identity, string Driver, FanValues Original, FanValues Previous, FanValues Expected)
    {
        public bool MatchesAdapter(AmdFanSnapshot snapshot) => Identity == snapshot.Identity && Driver == snapshot.DriverVersion;
        public bool MatchesKnown(FanValues current) => current.Matches(Original) || current.Matches(Expected)
            || current.Matches(Previous);
    }

    public GpuFanStatus Status => Volatile.Read(ref _status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ADLX runs independently of the motherboard control loop and the service startup thread.
        await Task.Yield();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            do
            {
                Probe();
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                RestoreSafely();
                _device?.Dispose();
                _device = null;
            }
        }
    }

    internal void Probe()
    {
        lock (_gate) RefreshSafely();
    }

    public Task<string?> ApplyAsync(GpuFanCommand command) => Task.Run(() =>
    {
        lock (_gate)
        {
            string? error = null;
            try { Apply(command); }
            catch (Exception ex)
            {
                error = ex.Message;
                logger.LogWarning(ex, "AMD fan curve apply failed");
            }
            RefreshSafely();
            return error;
        }
    });

    public Task<string?> RestoreAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            var error = RestoreSafely();
            RefreshSafely();
            return error;
        }
    });

    internal static bool ValidateCurve(AmdFanSnapshot current, IReadOnlyList<FanCurvePoint> points) =>
        points.Count == current.Points.Length &&
        points.All(point => current.Temperature.Contains(point.TemperatureC) && current.Speed.Contains(point.Percent)) &&
        points.Zip(points.Skip(1)).All(pair => pair.First.TemperatureC < pair.Second.TemperatureC
            && pair.First.Percent <= pair.Second.Percent);

    private void Apply(GpuFanCommand command)
    {
        var device = _device ?? throw new InvalidOperationException("AMD 风扇接口离线。");
        var current = device.Read();
        if (!CanWrite(current)) throw new InvalidOperationException("显卡未通过现场写入验收，或 Adrenalin 当前在出厂模式。");
        var previous = FanValues.From(current);
        if (command.BaseCurve is null || command.BaseZeroRpm != previous.ZeroRpm || !command.BaseCurve.SequenceEqual(previous.Points))
            throw new InvalidOperationException("驱动回读已变化，请重置草稿再编辑。");
        var recovery = _readRecovery();
        if (_suspended is not null || (recovery is not null &&
            (!_owns || !recovery.MatchesAdapter(current) || !recovery.Expected.Matches(previous))))
            throw new InvalidOperationException("显卡调校状态已变化，请先交还控制并检查 Adrenalin。");
        if (!ValidateCurve(current, command.Points) || !ValidateCurve(current, previous.Points))
            throw new InvalidOperationException("曲线点数、升序或步进超出驱动范围，或原始曲线无法安全恢复。");
        if (command.ZeroRpm == false && previous.ZeroRpm is null)
            throw new InvalidOperationException("此驱动未提供可恢复的 Zero RPM 状态。");
        if (command.ZeroRpm == true)
            throw new InvalidOperationException("此次设置只支持保留 Zero RPM 或手动关闭。");

        var desired = new FanValues(command.Points.ToArray(), command.ZeroRpm ?? previous.ZeroRpm);
        if (desired.Matches(previous)) return;
        var original = recovery?.Original ?? previous;
        if (!desired.Points.SequenceEqual(previous.Points))
        {
            device.ValidateCurve(original.Points);
            device.ValidateCurve(desired.Points);
        }
        try
        {
            if (!desired.Points.SequenceEqual(previous.Points))
            {
                var curveOnly = desired with { ZeroRpm = previous.ZeroRpm };
                recovery = new(current.Identity, current.DriverVersion, original, previous, curveOnly);
                _writeRecovery(recovery);
                device.SetCurve(curveOnly.Points);
                RequireReadback(device, recovery, curveOnly);
                previous = curveOnly;
            }
            if (desired.ZeroRpm != previous.ZeroRpm)
            {
                recovery = new(current.Identity, current.DriverVersion, original, previous, desired);
                _writeRecovery(recovery);
                device.SetZeroRpm(desired.ZeroRpm!.Value);
                RequireReadback(device, recovery, desired);
            }
            _owns = true;
        }
        catch (Exception ex)
        {
            var restoreError = RestoreSafely();
            throw new InvalidOperationException(restoreError is null ? ex.Message : $"{ex.Message} · {restoreError}", ex);
        }
    }

    private static void RequireReadback(IAmdFanDevice device, Recovery recovery, FanValues expected)
    {
        var readback = device.Read();
        if (readback.Factory || !recovery.MatchesAdapter(readback) || !expected.Matches(FanValues.From(readback)))
            throw new InvalidOperationException("AMD 驱动回读与请求不一致。");
    }

    private string? RestoreSafely()
    {
        try
        {
            var recovery = _readRecovery();
            if (recovery is null)
            {
                _suspended = null;
                _owns = false;
                return null;
            }
            var device = _device ?? throw new InvalidOperationException("AMD 驱动离线，已保留恢复记录。");
            var snapshot = device.Read();
            var current = FanValues.From(snapshot);
            if (!recovery.MatchesAdapter(snapshot) || !recovery.MatchesKnown(current))
                throw new InvalidOperationException("检测到 Adrenalin 外部更改或驱动更新，已暂停写入并保留恢复记录。");
            if (!recovery.Original.Matches(current))
            {
                if (snapshot.Factory || !ValidateCurve(snapshot, recovery.Original.Points))
                    throw new InvalidOperationException("AMD 驱动当前无法独立恢复原有风扇调校。");
                if (!current.Points.SequenceEqual(recovery.Original.Points))
                {
                    var curveOnly = recovery.Original with { ZeroRpm = current.ZeroRpm };
                    recovery = recovery with { Previous = current, Expected = curveOnly };
                    _writeRecovery(recovery);
                    device.SetCurve(curveOnly.Points);
                    RequireReadback(device, recovery, curveOnly);
                    current = curveOnly;
                }
                if (current.ZeroRpm != recovery.Original.ZeroRpm)
                {
                    var originalZero = recovery.Original.ZeroRpm ??
                        throw new InvalidOperationException("原始 Zero RPM 状态不可恢复。");
                    recovery = recovery with { Previous = current, Expected = recovery.Original };
                    _writeRecovery(recovery);
                    device.SetZeroRpm(originalZero);
                }
                RequireReadback(device, recovery, recovery.Original);
            }
            _writeRecovery(null);
            _suspended = null;
            _owns = false;
            return null;
        }
        catch (Exception ex)
        {
            _suspended = $"交还显卡控制失败：{ex.Message}";
            _owns = false;
            logger.LogWarning(ex, "Could not restore AMD fan tuning");
            return _suspended;
        }
    }

    private void RefreshSafely()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = panel.Snapshot().Sensors;
        var fresh = FanPolicy.IsFresh(snapshot, now);
        var fallbackRpm = fresh && snapshot.Get(SensorKeys.GpuFan) is >= 0 and <= 100000 and var rpm
            ? (int?)Math.Round(rpm) : null;
        var fallbackTemperature = fresh ? FanPolicy.Temperature(snapshot, SensorKeys.GpuTemperature) : null;
        try
        {
            if (_device is null && now >= _retryAfter)
            {
                _device = _openDevice();
                _recoveryChecked = false;
            }
            if (_device is null)
            {
                SetStatus(Status with { Rpm = fallbackRpm, TemperatureC = fallbackTemperature, CanWrite = false }, now);
                return;
            }
            if (!_recoveryChecked)
            {
                RestoreSafely();
                _recoveryChecked = true;
            }
            var device = _device;
            var current = device.Read();
            if (_owns && (_readRecovery() is not { } recovery || current.Factory ||
                !recovery.MatchesAdapter(current) || !recovery.Expected.Matches(FanValues.From(current))))
            {
                _owns = false;
                _suspended = "Adrenalin 风扇设置发生变化，SEEIN 已暂停接管。";
            }
            var metrics = device.ReadMetrics();
            var canWrite = _suspended is null && CanWrite(current);
            var message = _suspended ?? (canWrite ? "风扇曲线由 AMD 驱动执行。" :
                current.Factory ? "Adrenalin 正在使用出厂调校，显卡风扇只读。" :
                "等待现场确认独立接管和恢复，显卡风扇只读。");
            SetStatus(new(current.Name, _owns ? "seein" : _suspended is not null ? "suspended" : "driver",
                message, current.Temperature, current.Speed, current.Points, current.ZeroRpm,
                metrics.Rpm ?? fallbackRpm, metrics.Temperature ?? fallbackTemperature, canWrite), now);
        }
        catch (Exception ex)
        {
            _retryAfter = now.AddSeconds(30);
            _device?.Dispose();
            _device = null;
            SetStatus(new("RX 9070 XT", _owns || _suspended is not null ? "suspended" : "driver",
                $"AMD 风扇接口暂不可用：{ex.Message}", Rpm: fallbackRpm, TemperatureC: fallbackTemperature), now);
        }
    }

    private void SetStatus(GpuFanStatus status, DateTimeOffset now)
    {
        while (_trend.TryPeek(out var oldest) && now - oldest.At >= TimeSpan.FromMinutes(10))
            _trend.Dequeue();
        // ADLX does not expose the curve's instantaneous target with the RPM measurement.
        if (_trend.Count == 0 || now - _trend.Last().At >= TimeSpan.FromSeconds(1))
            _trend.Enqueue(new(now, status.TemperatureC, null, status.Rpm));
        Volatile.Write(ref _status, status with { Trend = _trend.ToArray() });
    }

    private bool CanWrite(AmdFanSnapshot snapshot) => !snapshot.Factory && _isAccepted(snapshot);

    private static bool IsAccepted(AmdFanSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.DriverVersion) || AppPaths.InstanceSuffix.Length != 0)
            return false;
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        return string.Equals(key?.GetValue(AcceptanceKey) as string, snapshot.DriverVersion, StringComparison.Ordinal);
    }

    private static Recovery? ReadRegistryRecovery()
    {
        if (AppPaths.InstanceSuffix.Length != 0) return null;
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        return key?.GetValue(JournalKey) is string json
            ? JsonSerializer.Deserialize<Recovery>(json) ?? throw new InvalidDataException("AMD 恢复记录无法读取。")
            : null;
    }

    private static void WriteRegistryRecovery(Recovery? recovery)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
        if (recovery is null) key.DeleteValue(JournalKey, throwOnMissingValue: false);
        else key.SetValue(JournalKey, JsonSerializer.Serialize(recovery), RegistryValueKind.String);
        key.Flush();
    }
}
