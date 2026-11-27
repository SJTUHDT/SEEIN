using Seein.Core;
using Seein.Core.Hosting;
using Seein.Core.Ipc;
using Seein.Core.Power;
using Seein.Core.Sensors;
using Seein.Core.Storage;
using System.Security.Principal;
using System.Text.Json;
namespace Seein.Service;

// The mandatory base has no reference to display, application, device or private implementations.
public sealed class PanelWorker(ILogger<PanelWorker> logger, PanelState state, ModuleSettingsStore settings,
    SharedHardware hardware, ModuleServiceRegistry modules, IHostApplicationLifetime lifetime) : BackgroundService, IModuleMessageBus
{
    private readonly AppPaths _paths = AppPaths.SystemDefault;
    private readonly object _stateLock = new();
    private readonly object _energyLock = new();
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private PanelPipeServer? _pipe;
    private HardwareSensorCollector? _sensors;
    private EnergyRepository? _energy;
    private DailyEnergyEntry? _highestDay;
    private SensorSnapshot _snapshot = SensorSnapshot.Empty;
    private PowerSnapshot? _power;
    private PanelSettings _settings => settings.Current;
    private readonly Dictionary<DateOnly, PendingEnergy> _pendingEnergy = [];
    private readonly Dictionary<DateOnly, EnergyDelta> _inFlightEnergy = [];
    private sealed record Totals(double Today, double Month, double Total);
    private Totals _persistedTotals = new(0, 0, 0);
    private sealed record Session(string BootId, double Wh);
    private Session _session = new("", 0);
    private MinutePowerBuffer _minutes = new(DateTimeOffset.Now);
    private double? _trayVolume;
    private DateTimeOffset _trayVolumeTimestamp;
    private CancellationTokenSource? _loops;
    private Task? _persistTask, _publishTask;

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        _paths.EnsureCreated(); state.PublishSettings(_settings); settings.Changed += state.PublishSettings;
        _energy = await OpenEnergyDatabaseAsync(token);
        var initial = await _energy.GetTotalsAsync(DateTimeOffset.Now, token); _persistedTotals = new(initial.Today, initial.Month, initial.Total);
        _highestDay = await _energy.GetHighestDayAsync(token);
        _sensors = new HardwareSensorCollector(); hardware.Sensors = _sensors;
        var sidPath = Path.Combine(_paths.DataRoot, "tray-user.sid");
        _pipe = new(File.Exists(sidPath) ? File.ReadAllText(sidPath).Trim() : WindowsIdentity.GetCurrent().User?.Value);
        _pipe.MessageReceived += OnPipeMessageAsync; _pipe.Start();
        var bootId = BootSessionIdentifier.GetCurrent(); var saved = await _energy.GetSessionStateAsync(token);
        var integrator = new EnergyBreakdownIntegrator(saved.BootId == bootId ? saved.SessionWh : 0);
        _session = new(bootId, integrator.SessionWh); _minutes = new(DateTimeOffset.Now);
        _loops = CancellationTokenSource.CreateLinkedTokenSource(token);
        _persistTask = PersistLoopAsync(_loops.Token); _publishTask = PublishLoopAsync(_loops.Token);
        modules.Start(token);
        var lastBootCheck = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(900));
        try
        {
            do
            {
                try
                {
                    var now = DateTimeOffset.Now;
                    if (now - lastBootCheck >= TimeSpan.FromSeconds(15))
                    {
                        lastBootCheck = now; var currentBoot = BootSessionIdentifier.GetCurrent();
                        if (currentBoot != bootId) { bootId = currentBoot; integrator = new(); _power = null; }
                    }
                    var snapshot = hardware.Read();
                    lock (_stateLock)
                    {
                        if (_trayVolume is double volume && now - _trayVolumeTimestamp <= TimeSpan.FromSeconds(5))
                        { var values = snapshot.Values.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase); values[SensorKeys.SystemVolume] = volume; snapshot = snapshot with { Values = values }; }
                    }
                    var breakdown = EstimatedPowerSource.Estimate(snapshot, _settings.PowerBaselineWatts, _settings.PowerEfficiency);
                    var delta = integrator.Add(now, breakdown); AddPendingEnergy(now, delta); _minutes.Add(now, breakdown.TotalWatts);
                    Volatile.Write(ref _session, new(bootId, integrator.SessionWh));
                    Totals stored; (double Today, double Month, double Total) pending;
                    lock (_energyLock) { stored = _persistedTotals; pending = GetPendingTotals(now); }
                    var highest = Volatile.Read(ref _highestDay);
                    _snapshot = snapshot; _power = new(now, breakdown.TotalWatts, true, integrator.SessionWh, stored.Today + pending.Today, stored.Month + pending.Month, stored.Total + pending.Total, "传感器估算", highest?.WattHours ?? 0, highest?.LocalDate);
                    state.Publish(_snapshot, _power);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Sampling failed; display and persistence remain independent"); }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _loops.Cancel();
            try { await Task.WhenAll(_persistTask, _publishTask); } catch (OperationCanceledException) { } catch (Exception ex) { logger.LogError(ex, "Background publication failed during shutdown"); }
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await modules.StopAsync(timeout.Token); } catch (Exception ex) { logger.LogError(ex, "Module shutdown failed"); }
            _minutes.Close(DateTimeOffset.Now);
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await PersistAsync(timeout.Token); } catch (Exception ex) { logger.LogError(ex, "Final energy flush failed"); }
            settings.Changed -= state.PublishSettings;
        }
    }
    private async Task PersistLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(token)) try { await PersistAsync(token); } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Energy database unavailable; pending samples retained"); } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task PersistAsync(CancellationToken token)
    {
        await _storageGate.WaitAsync(token);
        try
        {
            var now = DateTimeOffset.Now; var session = Volatile.Read(ref _session);
            await FlushEnergyAsync(now, session.BootId, session.Wh, token);
            await _minutes.FlushAsync((sample, ct) => _energy!.UpsertMinutePowerAsync(sample.Started, sample.Min, sample.Average, sample.Max, sample.Count, ct), token);
            await _energy!.PruneMinuteHistoryAsync(now, token); Volatile.Write(ref _highestDay, await _energy.GetHighestDayAsync(token));
        }
        finally { _storageGate.Release(); }
    }
    private async Task PublishLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(900));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            { try { var frame = state.Snapshot(); await _pipe!.BroadcastAsync(PipeMessageType.SensorSnapshot, frame.Sensors, token); if (frame.Power is not null) await _pipe.BroadcastAsync(PipeMessageType.PowerSnapshot, frame.Power, token); } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Tray publication failed"); } }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public Task BroadcastAsync(PipeEnvelope message, CancellationToken token = default) => _pipe?.BroadcastEnvelopeAsync(message, token) ?? Task.CompletedTask;
    private async Task OnPipeMessageAsync(PipeEnvelope envelope)
    {
        var query = envelope.Type is PipeMessageType.EnergyDayRequest or PipeMessageType.EnergyHistoryRequest;
        if (query) await _storageGate.WaitAsync();
        try
        {
            switch (envelope.Type)
            {
                case "moduleHealthRequest":
                    if(_pipe is not null)await _pipe.BroadcastAsync("moduleHealthResult",modules.Health(envelope.Read<ModuleHealthRequest>()?.RequestId));
                    break;

                case "previewShutdown":
                    if (AppPaths.InstanceSuffix.Length != 0) lifetime.StopApplication();
                    break;
                case PipeMessageType.SensorSnapshot:
                    var traySensors = envelope.Read<SensorSnapshot>();
                    if (traySensors?.Get(SensorKeys.SystemVolume) is double trayVolume)
                    {
                        lock (_stateLock)
                        {
                            _trayVolume = Math.Clamp(trayVolume, 0, 100);
                            _trayVolumeTimestamp = traySensors.Timestamp;
                        }
                    }
                    break;
                case PipeMessageType.EnergyHistoryRequest:
                    var historyRequest = envelope.Read<EnergyHistoryRequest>();
                    if (historyRequest is null || _pipe is null || _energy is null) return;
                    try
                    {
                        var entries = (await _energy.GetDailyEnergyAsync(historyRequest.Month).ConfigureAwait(false)).ToList();
                        var range = await _energy.GetEnergyMonthRangeAsync().ConfigureAwait(false);
                        var localNow = DateTimeOffset.Now.ToLocalTime();
                        foreach (var pending in SnapshotPendingEnergy())
                        {
                            var month = pending.Key.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
                            if (!string.Equals(historyRequest.Month, month, StringComparison.Ordinal)) continue;
                            var date = pending.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                            var index = entries.FindIndex(entry => entry.LocalDate == date);
                            if (index >= 0) entries[index] = entries[index] with { WattHours = entries[index].WattHours + pending.Value.TotalWh };
                            else entries.Add(new DailyEnergyEntry(date, pending.Value.TotalWh));
                            range = (range.FirstMonth is null || string.CompareOrdinal(month, range.FirstMonth) < 0 ? month : range.FirstMonth,
                                range.LastMonth is null || string.CompareOrdinal(month, range.LastMonth) > 0 ? month : range.LastMonth);
                        }
                        entries.Sort((left, right) => string.CompareOrdinal(left.LocalDate, right.LocalDate));
                        var summary = EnergyMonthSummaryCalculator.Calculate(entries, DateOnly.FromDateTime(localNow.DateTime));
                        await _pipe.BroadcastAsync(PipeMessageType.EnergyHistoryResult,
                            new EnergyHistoryResult(historyRequest.Month, entries, range.FirstMonth, range.LastMonth, summary, historyRequest.RequestId));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Energy history request failed for {Month}", historyRequest.Month);
                        await _pipe.BroadcastAsync(PipeMessageType.EnergyHistoryResult,
                            new EnergyHistoryResult(historyRequest.Month, [], null, null, RequestId: historyRequest.RequestId, Error: ex.Message));
                    }
                    break;
                case PipeMessageType.EnergyDayRequest:
                    var dayRequest = envelope.Read<EnergyDayRequest>();
                    if (dayRequest is null || _pipe is null || _energy is null) return;
                    try
                    {
                        var result = await _energy.GetEnergyDayAsync(dayRequest.LocalDate, dayRequest.RequestId, DateTimeOffset.Now).ConfigureAwait(false);
                        if (DateOnly.TryParseExact(dayRequest.LocalDate, "yyyy-MM-dd", out var date))
                        {
                            var pending = GetPendingEnergy(date);
                            result = result with
                            {
                                TotalWh = result.TotalWh + pending.TotalWh,
                                CpuWh = AddNullable(result.CpuWh, pending.CpuWh),
                                GpuWh = AddNullable(result.GpuWh, pending.GpuWh),
                                OtherWh = AddNullable(result.OtherWh, pending.OtherWh),
                                LossWh = AddNullable(result.LossWh, pending.LossWh),
                                UnclassifiedWh = result.UnclassifiedWh + pending.UnclassifiedWh,
                                CoveredSeconds = result.CoveredSeconds + pending.CoveredSeconds,
                                ClassifiedSeconds = result.ClassifiedSeconds + pending.ClassifiedSeconds,
                                CpuCoveredSeconds = result.CpuCoveredSeconds + pending.CpuCoveredSeconds,
                                GpuCoveredSeconds = result.GpuCoveredSeconds + pending.GpuCoveredSeconds
                            };
                            result = AddCurrentMinutePoint(result, date);
                        }
                        await _pipe.BroadcastAsync(PipeMessageType.EnergyDayResult, result).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Energy day request failed for {Date}", dayRequest.LocalDate);
                        await _pipe.BroadcastAsync(PipeMessageType.EnergyDayResult,
                            new EnergyDayResult(dayRequest.LocalDate, dayRequest.RequestId, 0, null, null, null, null,
                                0, 0, 0, [], "1 分钟", false, ex.Message));
                    }
                    break;

                case PipeMessageType.Hello:
                    if (_pipe is not null)
                    { var snapshot = state.Snapshot(); await _pipe.BroadcastAsync(PipeMessageType.SensorSnapshot, snapshot.Sensors); if (snapshot.Power is not null) await _pipe.BroadcastAsync(PipeMessageType.PowerSnapshot, snapshot.Power); }
                    break;
            }
        }
        finally { if (query) _storageGate.Release(); }
        await modules.RouteAsync(envelope, CancellationToken.None);
    }
    private async Task FlushEnergyAsync(DateTimeOffset now, string bootId, double sessionWh, CancellationToken token)
    {
        Dictionary<DateOnly, EnergyDelta> drained;
        lock (_energyLock)
        {
            drained = _pendingEnergy.ToDictionary(p => p.Key, p => p.Value.ToDelta()); _pendingEnergy.Clear();
            foreach (var pair in drained) _inFlightEnergy[pair.Key] = pair.Value;
        }
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        foreach (var entry in drained.OrderBy(p => p.Key))
        {
            try { await _energy!.AddEnergyAsync(entry.Key, entry.Value, token); }
            catch { lock (_energyLock) { RestorePendingEnergy(drained.Where(p => p.Key >= entry.Key)); _inFlightEnergy.Clear(); } throw; }
            lock (_energyLock)
            {
                var total = _persistedTotals; var wh = entry.Value.TotalWh;
                _persistedTotals = new(total.Today + (entry.Key == today ? wh : 0), total.Month + (entry.Key.Year == today.Year && entry.Key.Month == today.Month ? wh : 0), total.Total + wh);
                _inFlightEnergy.Remove(entry.Key);
            }
        }
        await _energy!.SaveSessionStateAsync(bootId, sessionWh, token); var totals = await _energy.GetTotalsAsync(now, token);
        lock (_energyLock) _persistedTotals = new(totals.Today, totals.Month, totals.Total);
    }
    private async Task<EnergyRepository> OpenEnergyDatabaseAsync(CancellationToken token)
    {
        var migrated = await EnergyDatabaseMigrator.MigrateLegacyDatabaseIfNeededAsync(
            _paths.LegacyDatabase, _paths.Database, token).ConfigureAwait(false);
        if (migrated)
            logger.LogInformation("Existing energy history was preserved in the version 2 database; the legacy database was retained.");

        var repository = new EnergyRepository(_paths.Database);
        try
        {
            await repository.InitializeAsync(token).ConfigureAwait(false);
            return repository;
        }
        catch
        {
            await repository.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    private void AddPendingEnergy(DateTimeOffset timestamp, EnergyDelta delta)
    {
        if (delta.TotalWh <= 0) return;
        var date = DateOnly.FromDateTime(timestamp.ToLocalTime().DateTime);
        lock (_energyLock)
        {
            if (!_pendingEnergy.TryGetValue(date, out var bucket)) _pendingEnergy[date] = bucket = new PendingEnergy();
            bucket.Add(delta);
        }
    }
    private EnergyDelta GetPendingEnergy(DateOnly date)
    {
        lock (_energyLock) return _pendingEnergy.TryGetValue(date, out var bucket) ? bucket.ToDelta() : PendingEnergy.Empty;
    }
    private Dictionary<DateOnly, EnergyDelta> SnapshotPendingEnergy()
    {
        lock (_energyLock) return _pendingEnergy.ToDictionary(pair => pair.Key, pair => pair.Value.ToDelta());
    }
    private (double Today, double Month, double Total) GetPendingTotals(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        var month = today.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = SnapshotPendingEnergy();
        lock (_energyLock) foreach (var p in _inFlightEnergy) snapshot[p.Key] = snapshot.TryGetValue(p.Key, out var old) ? new EnergyDelta(old.TotalWh + p.Value.TotalWh, null, null, 0, 0, 0, 0, 0, 0, 0) : p.Value;
        return (snapshot.Where(pair => pair.Key == today).Sum(pair => pair.Value.TotalWh),
            snapshot.Where(pair => pair.Key.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) == month).Sum(pair => pair.Value.TotalWh),
            snapshot.Sum(pair => pair.Value.TotalWh));
    }
    private void RestorePendingEnergy(IEnumerable<KeyValuePair<DateOnly, EnergyDelta>> entries)
    {
        lock (_energyLock)
        {
            foreach (var entry in entries)
            {
                if (!_pendingEnergy.TryGetValue(entry.Key, out var bucket)) _pendingEnergy[entry.Key] = bucket = new PendingEnergy();
                bucket.Add(entry.Value);
            }
        }
    }
    private static double? AddNullable(double? stored, double? pending) =>
        pending is null ? stored : (stored ?? 0) + pending.Value;

    private EnergyDayResult AddCurrentMinutePoint(EnergyDayResult result, DateOnly date)
    {
        var points = result.Points.ToList();
        foreach (var sample in _minutes.Snapshot(true))
        {
            if (DateOnly.FromDateTime(sample.Started.ToLocalTime().DateTime) != date) continue;
            var minute = sample.Started.ToUnixTimeSeconds() / 60 * 60;
            var coverage = Math.Clamp(sample.Count * .9, 0, 60);
            if (points.All(p => p.UnixSeconds != minute)) points.Add(new(minute, 60, sample.Min, sample.Average, sample.Max, coverage, coverage < 45 ? 1 : 0));
        }
        return result with { Points = points.OrderBy(p => p.UnixSeconds).ToArray() };
    }
    private sealed class PendingEnergy
    {
        public static EnergyDelta Empty => new(0, null, null, 0, 0, 0, 0, 0, 0, 0);
        private double _total, _other, _loss, _unclassified, _covered, _classified, _cpuCovered, _gpuCovered;
        private double? _cpu, _gpu;

        public void Add(EnergyDelta delta)
        {
            _total += delta.TotalWh; _other += delta.OtherWh; _loss += delta.LossWh;
            _unclassified += delta.UnclassifiedWh; _covered += delta.CoveredSeconds;
            _classified += delta.ClassifiedSeconds; _cpuCovered += delta.CpuCoveredSeconds; _gpuCovered += delta.GpuCoveredSeconds;
            if (delta.CpuWh is double cpu) _cpu = (_cpu ?? 0) + cpu;
            if (delta.GpuWh is double gpu) _gpu = (_gpu ?? 0) + gpu;
        }

        public EnergyDelta ToDelta() => new(_total, _cpu, _gpu, _other, _loss, _unclassified,
            _covered, _cpuCovered, _gpuCovered, _classified);
    }


    public override async Task StopAsync(CancellationToken token)
    {
        await base.StopAsync(token);
        if (_pipe is not null) await _pipe.DisposeAsync();
        if (_energy is not null) await _energy.DisposeAsync();
        hardware.Sensors = null; _sensors?.Dispose(); _loops?.Dispose(); _storageGate.Dispose();
    }
}
