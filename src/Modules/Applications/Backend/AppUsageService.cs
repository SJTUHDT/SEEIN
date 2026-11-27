using Seein.Core;
using Seein.Core.Apps;
using Seein.Core.Storage;

namespace Seein.Service;

/// <summary>
/// Samples the process table, groups processes into applications, attributes the measured CPU/GPU
/// power and persists runtime intervals plus hourly/daily aggregates.  Query methods build the
/// payloads the tray application renders.
/// </summary>
public sealed partial class AppUsageService : IAsyncDisposable
{
    private sealed class Agg
    {
        public string? Path;
        public double Cpu;
        public double GpuUnits;
    }

    private sealed class NameAgg
    {
        public string Key = string.Empty;
        public string Name = string.Empty;
        public string? ExePath;
        public double Seconds;
        public double ActiveSeconds;
        public double Wh;
        public double CpuWh;
        public double GpuWh;
        public double CpuSeconds;
        public List<AppSpan> Spans = [];
    }

    private readonly ILogger<AppUsageService> _logger;
    private readonly AppUsageRepository _repository;
    private IReadOnlyList<CategoryRule> _categories = CategoryCatalog.Default;
    private readonly PanelState _state;
    private readonly AppIdentityResolver _resolver = new();
    private readonly AppUsageTracker _tracker = new();
    private readonly GpuEngineSampler _gpu = new();
    private readonly NetworkUsageSampler _tcpNetwork = new();
    private readonly EtwNetworkSampler _etwNetwork = new();
    private bool _etwPreferred;
    private int _gpuDiag;
    private int _networkDiag;
    private double _machineCpuPeak;
    private double _machineCpuMin;
    private double _machineGpuMin;
    private double _machineGpuPeak;
    private string _machinePeakDate = string.Empty;
    private readonly SystemClockMonitor _clock = new();
    private readonly Dictionary<int, (long Started, double Cpu)> _previous = new();
    private readonly Dictionary<string, (string? ExePath, string Name)> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _metaWritten = new(StringComparer.Ordinal);
    private readonly HashSet<string> _appliedRemaps = new(StringComparer.Ordinal);
    private readonly List<(long Start, long End)> _pendingAfkGaps = [];
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;
    private DateTimeOffset _lastTickUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastFlushUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastPruneUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastAfkSampleUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _afkStartedUtc;
    private int _lastIdleSeconds = -1;

    public AppUsageService(ILogger<AppUsageService> logger, PanelState state, AppUsageRepository repository)
    {
        _state = state;
        _logger = logger;
        _repository = repository;
    }

    public async Task EnsureInitializedAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _initialized)) return;
        // A failed start (locked or missing database) must be retryable, so the gate is released
        // on failure instead of caching a faulted task.
        await _initializationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _initialized)) return;
            await InitializeAsync(token).ConfigureAwait(false);
            Volatile.Write(ref _initialized, true);
        }
        finally { _initializationGate.Release(); }
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        await _repository.InitializeAsync(token).ConfigureAwait(false);
        try
        {
            var lastEnd = await _repository.GetLastEndUtcAsync(token).ConfigureAwait(false);
            var nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (lastEnd is long end && nowUtc - end > 120)
                await _repository.RecordGapAsync(end, nowUtc, "offline", token).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to register the offline coverage gap"); }

        try { _categories = CategoryCatalog.Load(Path.Combine(AppPaths.SystemDefault.DataRoot, "categories.json")); }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to load category rules"); }

        _logger.LogInformation("GPU sampler available={Available} discreteLuid={Luid}", _gpu.IsAvailable,
            _gpu.DiscreteAdapterLuid is long luid ? $"0x{luid:X16}" : "none");
        _etwPreferred = _etwNetwork.Start("_" + Environment.ProcessId);
        if (_etwPreferred)
        {
            _logger.LogInformation("Network usage sampler started (kernel ETW session)");
        }
        else if (_tcpNetwork.Start("_" + Environment.ProcessId))
            _logger.LogInformation("Network usage sampler started (per-connection TCP statistics); ETW unavailable: {Error}", _etwNetwork.LastError);
        else
            _logger.LogWarning("Network usage sampler unavailable: {Error}", _tcpNetwork.LastError);
    }

    /// <summary>
    /// Idle time comes from the tray: a session 0 service cannot read the user's input queue.
    /// </summary>
    public const int AfkThresholdSeconds = 40 * 60;

    public void OnAfkSample(AfkSample sample)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(sample.TimestampUtc);
        if (now < _lastAfkSampleUtc) return;
        _lastAfkSampleUtc = now;
        _lastIdleSeconds = sample.IdleSeconds;

        if (sample.IdleSeconds >= AfkThresholdSeconds)
        {
            if (_afkStartedUtc is null)
            {
                _afkStartedUtc = now.AddSeconds(-sample.IdleSeconds);
                _logger.LogInformation("AFK detected, idle since {Start}", _afkStartedUtc);
            }
            return;
        }

        if (_afkStartedUtc is { } started)
        {
            var end = now.AddSeconds(-sample.IdleSeconds);
            if (end > started)
            {
                _pendingAfkGaps.Add((started.ToUnixTimeSeconds(), end.ToUnixTimeSeconds()));
                _logger.LogInformation("AFK period recorded: {Minutes:F1} min idle", (end - started).TotalMinutes);
            }
            _afkStartedUtc = null;
        }
    }

    public async Task TickAsync(CancellationToken token)
    {
        await EnsureInitializedAsync(token).ConfigureAwait(false);
        var (sensors, power, settings) = _state.Snapshot();
        if (!settings.AppStatsEnabled) return;

        var now = DateTimeOffset.UtcNow;
        var dt = (now - _lastTickUtc).TotalSeconds;
        _lastTickUtc = now;
        if (dt <= 0.2 || dt > 120) return;

        var sleepSeconds = _clock.Advance();
        if (sleepSeconds > 3)
        {
            var start = now.AddSeconds(-sleepSeconds - dt).ToUnixTimeSeconds();
            var end = now.AddSeconds(-dt).ToUnixTimeSeconds();
            await _repository.RecordGapAsync(start, end, "sleep", token).ConfigureAwait(false);
            _previous.Clear();
        }

        var processes = ProcessSampler.Capture();
        if (processes.Count == 0) return;

        var keys = _resolver.Resolve(processes, p => ProcessCommandLine.Read(p.Pid, p.StartedUtc));
        var gpuByPid = _gpu.IsAvailable ? _gpu.Sample() : new Dictionary<int, double>();
        if (_gpuDiag++ % 12 == 0)
            _logger.LogInformation("GPU sample: {Diagnostic}", _gpu.LastDiagnostic);
        var cpuWatts = FiniteNonNegative(sensors.Get(SensorKeys.CpuPower));
        var gpuWatts = FiniteNonNegative(sensors.Get(SensorKeys.GpuPower));
        var totalWatts = FiniteNonNegative(power?.Watts);
        var pulse = Math.Max(5, settings.AppStatsSampleSeconds * 2);

        double totalCpuDelta = 0;
        double totalGpuUnits = 0;
        var current = new Dictionary<int, (long Started, double Cpu)>(processes.Count);
        var apps = new Dictionary<string, Agg>(StringComparer.Ordinal);
        var systemCpu = 0d;
        var systemGpuUnits = 0d;

        foreach (var process in processes)
        {
            current[process.Pid] = (process.StartedUtc, process.CpuSeconds);
            var delta = 0d;
            if (_previous.TryGetValue(process.Pid, out var previous) && previous.Started == process.StartedUtc)
            {
                delta = process.CpuSeconds - previous.Cpu;
                if (delta < 0 || delta > 60) delta = 0;
            }
            var gpuUnits = gpuByPid.TryGetValue(process.Pid, out var util) ? Math.Max(0, util) * dt : 0;
            totalCpuDelta += delta;
            totalGpuUnits += gpuUnits;

            if (!keys.TryGetValue(process.Pid, out var key) || key == AppStatKeys.Self) continue;
            if (key == AppStatKeys.System)
            {
                systemCpu += delta;
                systemGpuUnits += gpuUnits;
                continue;
            }

            if (!apps.TryGetValue(key, out var agg))
            {
                agg = new Agg();
                apps[key] = agg;
            }
            agg.Path ??= process.ExePath;
            agg.Cpu += delta;
            agg.GpuUnits += gpuUnits;
        }
        _previous.Clear();
        foreach (var item in current) _previous[item.Key] = item.Value;

        // Network bytes since the previous tick, attributed to the application that owns the socket.
        var networkByKey = new Dictionary<string, (long Up, long Down)>(StringComparer.Ordinal);
        if (NetworkSamplerPolicy.ShouldUseTcpFallback(_etwPreferred, _etwNetwork.IsRunning))
        {
            _etwPreferred = false;
            _logger.LogWarning("Kernel ETW sampler stopped; falling back to per-connection TCP statistics: {Error}", _etwNetwork.LastError);
            if (!_tcpNetwork.Start("_" + Environment.ProcessId))
                _logger.LogWarning("Fallback network sampler could not start: {Error}", _tcpNetwork.LastError);
        }
        Dictionary<int, (long Up, long Down)> networkSample;
        try { networkSample = _etwPreferred ? _etwNetwork.Drain() : _tcpNetwork.Drain(); }
        catch (Exception ex) when (_etwPreferred)
        {
            _logger.LogWarning(ex, "Kernel ETW sampling failed; switching to per-connection TCP statistics");
            _etwPreferred = false;
            _etwNetwork.Dispose();
            if (!_tcpNetwork.Start("_" + Environment.ProcessId))
                _logger.LogWarning("Fallback network sampler could not start: {Error}", _tcpNetwork.LastError);
            networkSample = _tcpNetwork.Drain();
        }
        if (_networkDiag++ % 12 == 0)
            _logger.LogInformation("Network sample: source={Source} pids={Pids} bytes={Bytes}",
                _etwPreferred ? "etw" : "estats", networkSample.Count,
                networkSample.Values.Sum(value => value.Up + value.Down));
        foreach (var (netPid, netBytes) in networkSample)
        {
            if (netBytes.Up == 0 && netBytes.Down == 0) continue;
            if (!keys.TryGetValue(netPid, out var netKey) || netKey == AppStatKeys.Self) continue;
            networkByKey.TryGetValue(netKey, out var previousNet);
            networkByKey[netKey] = (previousNet.Up + netBytes.Up, previousNet.Down + netBytes.Down);
        }


        var localDate = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        var localDateText = AppUsageRepository.FormatDate(localDate);
        var nowUtc = now.ToUnixTimeSeconds();
        // Without a recent idle report we assume the user is at the machine.
        var isActive = _afkStartedUtc is null;

        _tracker.BeginTick();
        foreach (var (key, agg) in apps)
        {
            var cpuWh = AppPowerAttributor.CpuWh(cpuWatts, agg.Cpu, totalCpuDelta, dt);
            var gpuWh = AppPowerAttributor.GpuWh(gpuWatts, agg.GpuUnits, totalGpuUnits, dt);
            networkByKey.TryGetValue(key, out var net);
            var cpuPeak = dt > 0 ? Math.Clamp(agg.Cpu / dt / Math.Max(1, Environment.ProcessorCount) * 100d, 0, 100) : 0;
            var gpuPeak = dt > 0 ? Math.Clamp(agg.GpuUnits / dt, 0, 100) : 0;
            _tracker.Observe(key, nowUtc, localDateText, dt, agg.Cpu, agg.GpuUnits, cpuWh, gpuWh, pulse,
                isActive: isActive, netUpBytes: net.Up, netDownBytes: net.Down,
                cpuPeak: cpuPeak, gpuPeak: gpuPeak, cpuMin: cpuPeak, gpuMin: gpuPeak);
            await RememberAsync(key, agg.Path, token).ConfigureAwait(false);
        }

        if (dt > 0)
        {
            var machineCpu = Math.Clamp((totalCpuDelta / dt / Math.Max(1, Environment.ProcessorCount)) * 100d, 0, 100);
            var machineGpu = Math.Clamp(totalGpuUnits / dt, 0, 100);
            if (_machinePeakDate != localDateText)
            {
                _machinePeakDate = localDateText;
                _machineCpuPeak = machineCpu;
                _machineCpuMin = machineCpu;
                _machineGpuMin = machineGpu;
                _machineGpuPeak = machineGpu;
            }
            else
            {
                _machineCpuPeak = Math.Max(_machineCpuPeak, machineCpu);
                _machineCpuMin = AppendMin(_machineCpuMin, machineCpu);
                _machineGpuMin = AppendMin(_machineGpuMin, machineGpu);
                _machineGpuPeak = Math.Max(_machineGpuPeak, machineGpu);
            }
        }

        _tracker.Observe(AppStatKeys.System, nowUtc, localDateText, dt, systemCpu, systemGpuUnits,
            AppPowerAttributor.CpuWh(cpuWatts, systemCpu, totalCpuDelta, dt),
            AppPowerAttributor.GpuWh(gpuWatts, systemGpuUnits, totalGpuUnits, dt), pulse, countsAsRuntime: false, isActive: isActive);

        var baselineWh = AppPowerAttributor.BaselineWh(totalWatts, cpuWatts, gpuWatts, dt);
        if (baselineWh > 0)
            _tracker.Observe(AppStatKeys.Baseline, nowUtc, localDateText, dt, 0, 0, 0, baselineWh, pulse, countsAsRuntime: false);

        _tracker.EndTick();
        await ApplyRemapsAsync(token).ConfigureAwait(false);

        if ((now - _lastFlushUtc).TotalSeconds >= 60) await FlushAsync(now, token).ConfigureAwait(false);
        if ((now - _lastPruneUtc).TotalHours >= 6)
        {
            _lastPruneUtc = now;
            try { await _repository.PruneAsync(now, token).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Unable to prune application usage history"); }
        }
    }

    private async Task RememberAsync(string key, string? exePath, CancellationToken token)
    {
        // For folder based keys (runtime hosts) the folder names the product; the process executable
        // would just say "electron" or "node".
        var name = key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? AppNameCatalog.DisplayName(exePath ?? key)
            : AppNameCatalog.DisplayName(key);
        if (_names.TryGetValue(key, out var existing) && existing.ExePath == exePath && existing.Name == name) return;
        _names[key] = (exePath, name);
        if (!_metaWritten.Add(key)) return;
        try { await _repository.UpsertMetaAsync(key, exePath, name, token).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Unable to store metadata for {Key}", key); }
    }

    /// <summary>Folds history recorded under a runtime-host key into the real application key.</summary>
    private async Task ApplyRemapsAsync(CancellationToken token)
    {
        if (_resolver.PendingRemaps.Count == 0) return;
        // a -> b and b -> c must both end up at c, otherwise history is split across the chain.
        var pending = _resolver.PendingRemaps.Select(pair => (From: pair.Key, To: pair.Value)).ToArray();
        foreach (var (from, to) in CanonicalRemaps(pending))
        {
            if (!_appliedRemaps.Add(from + " -> " + to)) continue;
            try
            {
                if (await _repository.RemapAppKeyAsync(from, to, token).ConfigureAwait(false))
                    _logger.LogInformation("Application key remapped: {From} -> {To}", from, to);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Unable to remap {From} -> {To}", from, to); }
        }
        _resolver.PendingRemaps.Clear();
    }

    private static IReadOnlyList<(string From, string To)> CanonicalRemaps(IReadOnlyList<(string From, string To)> maps)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in maps) lookup[from] = to;

        var result = new List<(string, string)>(maps.Count);
        foreach (var (from, to) in maps)
        {
            var target = to;
            for (var depth = 0; depth < 4 && lookup.TryGetValue(target, out var next); depth++)
            {
                if (string.Equals(next, target, StringComparison.Ordinal)) break;
                target = next;
            }
            if (!string.Equals(from, target, StringComparison.Ordinal)) result.Add((from, target));
        }
        return result;
    }

    private async Task FlushAsync(DateTimeOffset now, CancellationToken token)
    {
        _lastFlushUtc = now;
        try
        {
            foreach (var span in _tracker.SnapshotSpans())
                await _repository.UpsertSpanAsync(span.AppKey, span.StartUtc, span.EndUtc, span.CpuSeconds, span.GpuUnits, token)
                    .ConfigureAwait(false);
            await _repository.AddHoursAsync(_tracker.DrainHours(), token).ConfigureAwait(false);
            if (_machinePeakDate.Length > 0)
                await _repository.UpsertMachinePeakAsync(_machinePeakDate, _machineCpuPeak, _machineGpuPeak, token)
                    .ConfigureAwait(false);
            var days = _tracker.DrainDays();
            foreach (var day in days)
                await _repository.AddDayAsync(day, token).ConfigureAwait(false);
            foreach (var (start, end) in _pendingAfkGaps)
                await _repository.RecordGapAsync(start, end, "afk", token).ConfigureAwait(false);
            _pendingAfkGaps.Clear();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to flush application usage data"); }
    }

    public ValueTask DisposeAsync()
    {
        _gpu.Dispose();
        _etwNetwork.Dispose();
        _tcpNetwork.Dispose();
        return ValueTask.CompletedTask;
    }
}
