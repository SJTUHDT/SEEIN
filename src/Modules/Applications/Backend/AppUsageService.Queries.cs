using Seein.Core;
using Seein.Core.Apps;
using Seein.Core.Storage;
using System.Globalization;
using System.Text;

namespace Seein.Service;

public sealed partial class AppUsageService
{
    public async Task<AppStatsResult> QueryAsync(AppStatsRequest request, CancellationToken token)
    {
        await EnsureInitializedAsync(token).ConfigureAwait(false);
        return string.Equals(request.Kind, "week", StringComparison.OrdinalIgnoreCase)
            ? await QueryWeekAsync(request, token).ConfigureAwait(false)
            : await QueryDayAsync(request, token).ConfigureAwait(false);
    }

    private async Task<AppStatsResult> QueryWeekAsync(AppStatsRequest request, CancellationToken token)
    {
        var endDay = ParseDate(request.LocalDate) ?? DateOnly.FromDateTime(DateTime.Now);
        var startDay = endDay.AddDays(-6);
        var rows = await _repository.GetDaysAsync(AppUsageRepository.FormatDate(startDay.AddDays(-7)),
            AppUsageRepository.FormatDate(endDay), token).ConfigureAwait(false);
        var meta = (await _repository.GetMetaAsync(token).ConfigureAwait(false)).ToDictionary(m => m.AppKey, StringComparer.Ordinal);
        var aliases = ResolveAliases(meta);
        var current = rows.Where(r => string.CompareOrdinal(r.LocalDate, AppUsageRepository.FormatDate(startDay)) >= 0).ToArray();
        var (weekStartUtc, _) = EnergyRepository.GetLocalDayUtcRange(startDay, TimeZoneInfo.Local);
        var (_, weekEndUtc) = EnergyRepository.GetLocalDayUtcRange(endDay, TimeZoneInfo.Local);
        var weekEvents = await _repository.GetEventsAsync(weekStartUtc.ToUnixTimeSeconds(),
            weekEndUtc.ToUnixTimeSeconds(), token).ConfigureAwait(false);

        // One row per display name, exactly like the day view.
        var byName = new Dictionary<string, List<(string Target, AppDayRow Row)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in current.Where(r => r.AppKey is not (AppStatKeys.System or AppStatKeys.Baseline)))
        {
            var target = aliases.TryGetValue(row.AppKey, out var mapped) ? mapped : row.AppKey;
            var name = ResolveName(meta, target);
            if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
            list.Add((target, row));
        }

        var merged = new Dictionary<string, NameAgg>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, members) in byName)
        {
            var visible = members.Where(member => request.IncludeHidden || !IsHidden(meta, member.Target)).ToArray();
            if (visible.Length == 0) continue;
            var primary = visible.OrderByDescending(member => member.Row.Seconds).First();
            var agg = new NameAgg { Key = primary.Target, Name = name };
            agg.ExePath = meta.TryGetValue(primary.Target, out var metaRow) ? metaRow.ExePath : null;
            foreach (var member in visible)
            {
                agg.Seconds += member.Row.Seconds;
                agg.ActiveSeconds += member.Row.ActiveSeconds;
                agg.Wh += member.Row.Wh;
                agg.CpuWh += member.Row.CpuWh;
                agg.GpuWh += member.Row.GpuWh;
                agg.CpuSeconds += member.Row.CpuSeconds;
            }
            merged[name] = agg;
        }

        // Per-day union, then add up the days: overlapping members count once.
        var unionByName = UnionSecondsPerGroup(weekEvents,
            key => ResolveName(meta, aliases.TryGetValue(key, out var target) ? target : key), startDay, 7);
        foreach (var (name, seconds) in unionByName)
            if (merged.TryGetValue(name, out var group))
                group.Seconds = seconds;

        var timeline = new List<AppTimelineRow>();
        var power = new List<AppPowerRow>();
        var visibleSeconds = 0d;
        var visibleActive = 0d;
        var visibleWh = 0d;
        var colors = ColorIndex(merged.Keys);
        foreach (var agg in merged.Values)
        {
            var category = CategoryCatalog.Match(_categories, agg.Key, agg.Name);
            var color = ParseColor(meta, agg.Key) ?? AppPalette.ColorAt(colors.TryGetValue(agg.Key, out var position) ? position : 0);
            var iconPath = IconOf(meta, agg.Key);
            var activeSeconds = Math.Min(agg.ActiveSeconds, agg.Seconds);
            var hiddenFromRuntime = meta.TryGetValue(agg.Key, out var aggMeta) &&
                (string.Equals(aggMeta.MergedInto, AppStatKeys.Background, StringComparison.Ordinal) ||
                 IsSystemRow(aggMeta, agg.Key));
            if (!hiddenFromRuntime)
            {
                timeline.Add(new AppTimelineRow(agg.Key, agg.Name, agg.ExePath, agg.Seconds, agg.Wh, [], activeSeconds, category, color, iconPath));
                visibleSeconds += agg.Seconds;
            }
            power.Add(new AppPowerRow(agg.Key, agg.Name, agg.ExePath, agg.Seconds, agg.Wh, agg.CpuWh, agg.GpuWh, 0, activeSeconds, category, color, iconPath));
            visibleActive += activeSeconds;
            visibleWh += agg.Wh;
        }

        timeline.Sort((left, right) => right.Seconds.CompareTo(left.Seconds));
        power.Sort((left, right) => right.Wh.CompareTo(left.Wh));
        for (var index = 0; index < power.Count; index++)
            power[index] = power[index] with { Share = visibleWh > 0 ? power[index].Wh / visibleWh : 0 };

        var systemWh = current.Where(r => r.AppKey == AppStatKeys.System).Sum(r => r.Wh);
        var baselineWh = current.Where(r => r.AppKey == AppStatKeys.Baseline).Sum(r => r.Wh);
        var previous = rows.Where(r => string.CompareOrdinal(r.LocalDate, AppUsageRepository.FormatDate(startDay)) < 0 &&
                                       r.AppKey is not (AppStatKeys.System or AppStatKeys.Baseline)).Sum(r => r.Wh);
        var boot = SystemClockMonitor.BootTimeUtc();
        var systemActive = current.Where(r => r.AppKey == AppStatKeys.System).Sum(r => r.ActiveSeconds);
        var summary = new AppStatsSummary(timeline.Count, visibleSeconds, visibleWh, visibleSeconds, 0, systemWh, baselineWh, systemActive);
        var series = BuildWeekSeries(rows, endDay);
        return new AppStatsResult("week", AppUsageRepository.FormatDate(endDay), summary, timeline, power, series, [],
            boot.ToUnixTimeSeconds(), request.RequestId, null, previous);
    }

    private async Task<AppStatsResult> QueryDayAsync(AppStatsRequest request, CancellationToken token)
    {
        var (_, _, settings) = _state.Snapshot();
        var day = ParseDate(request.LocalDate) ?? DateOnly.FromDateTime(DateTime.Now);
        var (startUtc, endUtc) = EnergyRepository.GetLocalDayUtcRange(day, TimeZoneInfo.Local);
        var meta = (await _repository.GetMetaAsync(token).ConfigureAwait(false)).ToDictionary(m => m.AppKey, StringComparer.Ordinal);
        var aliases = ResolveAliases(meta);
        var events = await _repository.GetEventsAsync(startUtc.ToUnixTimeSeconds(), endUtc.ToUnixTimeSeconds(), token).ConfigureAwait(false);
        var gaps = await _repository.GetGapsAsync(startUtc.ToUnixTimeSeconds(), endUtc.ToUnixTimeSeconds(), token).ConfigureAwait(false);
        var dayRows = await _repository.GetDaysAsync(AppUsageRepository.FormatDate(day), AppUsageRepository.FormatDate(day), token).ConfigureAwait(false);

        var nowUtc = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dayLength = (endUtc - startUtc).TotalSeconds;
        var elapsedSeconds = day < today
            ? dayLength
            : Math.Clamp((nowUtc - startUtc).TotalSeconds, 60, dayLength);
        var cores = Math.Max(1, Environment.ProcessorCount);

        var dayByApp = dayRows.ToDictionary(r => r.AppKey, StringComparer.Ordinal);
        // A service restart records an "offline" coverage gap.  Applications that were running before
        // and after a short restart are drawn as one continuous bar instead of two pieces.
        var bridges = gaps
            .Where(gap => string.Equals(gap.Reason, "offline", StringComparison.OrdinalIgnoreCase))
            .Select(gap => (Start: gap.StartUtc, End: gap.EndUtc))
            .ToArray();
        var spansByApp = events
            .Where(e => e.AppKey is not (AppStatKeys.System or AppStatKeys.Baseline))
            .GroupBy(e => e.AppKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => MergeSpans(g, startUtc.ToUnixTimeSeconds(), endUtc.ToUnixTimeSeconds(), bridges,
                SystemClockMonitor.BootTimeUtc().ToUnixTimeSeconds()), StringComparer.Ordinal);

        // One row per display name; overlapping member intervals count once (union), and the two
        // classifications stay out of the runtime page while keeping their power rows.
        var byName = new Dictionary<string, List<(string Target, IReadOnlyList<AppSpan> Spans, AppDayRow? Row)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in spansByApp.Keys.Concat(dayByApp.Keys).Distinct(StringComparer.Ordinal))
        {
            if (key is AppStatKeys.System or AppStatKeys.Baseline) continue;
            var target = aliases.TryGetValue(key, out var mapped) ? mapped : key;
            var name = ResolveName(meta, target);
            if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
            list.Add((target,
                spansByApp.TryGetValue(key, out var spans) ? spans : [],
                dayByApp.TryGetValue(key, out var dayRow) ? dayRow : null));
        }

        var merged = new Dictionary<string, NameAgg>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, members) in byName)
        {
            var visible = members.Where(member => request.IncludeHidden || !IsHidden(meta, member.Target)).ToArray();
            if (visible.Length == 0) continue;
            var primary = visible
                .OrderByDescending(member => member.Row?.Seconds ?? member.Spans.Sum(span => (double)(span.EndUtc - span.StartUtc)))
                .First();
            var agg = new NameAgg { Key = primary.Target, Name = name };
            agg.ExePath = meta.TryGetValue(primary.Target, out var metaRow) ? metaRow.ExePath : null;
            foreach (var member in visible)
            {
                agg.Seconds += member.Row?.Seconds ?? member.Spans.Sum(span => (double)(span.EndUtc - span.StartUtc));
                agg.ActiveSeconds += member.Row?.ActiveSeconds ?? 0;
                agg.Wh += member.Row?.Wh ?? 0;
                agg.CpuWh += member.Row?.CpuWh ?? 0;
                agg.GpuWh += member.Row?.GpuWh ?? 0;
                agg.CpuSeconds += member.Row?.CpuSeconds ?? 0;
                agg.Spans.AddRange(member.Spans);
            }
            merged[name] = agg;
        }

        var timeline = new List<AppTimelineRow>();
        var power = new List<AppPowerRow>();
        var visibleSeconds = 0d;
        var visibleActive = 0d;
        var visibleWh = 0d;
        var colors = ColorIndex(merged.Keys);
        foreach (var agg in merged.Values)
        {
            // Runtime is the union of the processes intervals: a 8-10, b 9-13 -> 8-13.
            var spans = MergeSpans(agg.Spans);
            var unionSeconds = spans.Sum(span => (double)(span.EndUtc - span.StartUtc));
            var displaySeconds = unionSeconds > 0 ? unionSeconds : agg.Seconds;
            var shownActive = Math.Min(agg.ActiveSeconds, displaySeconds);
            var category = CategoryCatalog.Match(_categories, agg.Key, agg.Name);
            var color = ParseColor(meta, agg.Key) ?? AppPalette.ColorAt(colors.TryGetValue(agg.Key, out var position) ? position : 0);
            var iconPath = IconOf(meta, agg.Key);
            var hiddenFromRuntime = meta.TryGetValue(agg.Key, out var aggMeta) &&
                (string.Equals(aggMeta.MergedInto, AppStatKeys.Background, StringComparison.Ordinal) ||
                 IsSystemRow(aggMeta, agg.Key));
            if (!hiddenFromRuntime)
            {
                timeline.Add(new AppTimelineRow(agg.Key, agg.Name, agg.ExePath, displaySeconds, agg.Wh, spans, shownActive, category, color, iconPath));
                visibleSeconds += displaySeconds;
            }
            power.Add(new AppPowerRow(agg.Key, agg.Name, agg.ExePath, displaySeconds, agg.Wh, agg.CpuWh, agg.GpuWh, 0, shownActive, category, color, iconPath));
            visibleActive += shownActive;
            visibleWh += agg.Wh;
        }

        timeline.Sort((left, right) => right.Seconds.CompareTo(left.Seconds));
        power.Sort((left, right) => right.Wh.CompareTo(left.Wh));
        for (var index = 0; index < power.Count; index++)
        {
            var share = visibleWh > 0 ? power[index].Wh / visibleWh : 0;
            power[index] = power[index] with { Share = share };
        }

        var systemWh = dayByApp.TryGetValue(AppStatKeys.System, out var systemRow) ? systemRow.Wh : 0;
        var baselineWh = dayByApp.TryGetValue(AppStatKeys.Baseline, out var baselineRow) ? baselineRow.Wh : 0;
        var boot = SystemClockMonitor.BootTimeUtc();
        var systemSeconds = Math.Max(0, Math.Min((nowUtc - (boot > startUtc ? boot : startUtc)).TotalSeconds, (endUtc - startUtc).TotalSeconds));

        // Active time = machine uptime minus the away-from-keyboard coverage, which also works for days
        // recorded before per-tick activity was stored.
        var afkSeconds = UnionGaps(gaps, "afk", startUtc.ToUnixTimeSeconds(), endUtc.ToUnixTimeSeconds());
        var activeSeconds = Math.Max(0, systemSeconds - afkSeconds);
        var systemActive = dayByApp.TryGetValue(AppStatKeys.System, out var systemActiveRow)
            ? Math.Max(systemActiveRow.ActiveSeconds, activeSeconds)
            : activeSeconds;
        var summary = new AppStatsSummary(timeline.Count, visibleSeconds, visibleWh,
            UnionSeconds(spansByApp.Values), systemSeconds, systemWh, baselineWh, systemActive);

        var weekSeries = BuildWeekSeries(await _repository.GetDaysAsync(
            AppUsageRepository.FormatDate(day.AddDays(-13)), AppUsageRepository.FormatDate(day), token).ConfigureAwait(false), day);

        return new AppStatsResult(request.Kind, AppUsageRepository.FormatDate(day), summary, timeline, power,
            weekSeries, gaps.Select(g => new AppGap(g.StartUtc, g.EndUtc, g.Reason)).ToArray(),
            boot.ToUnixTimeSeconds(), request.RequestId);
    }

    /// <summary>
    /// Every application the service knows about, with its management metadata.  Entries that map to the
    /// same display name (WeChat's several processes, UU Remote's server and helper) are folded into one
    /// row so the list never shows the same name twice; the individual keys are exposed as members.
    /// </summary>
    /// <summary>Per-application CPU/GPU share and network bytes for one local day.</summary>
    public async Task<AppLoadResult> QueryLoadAsync(AppLoadRequest request, CancellationToken token)
    {
        await EnsureInitializedAsync(token).ConfigureAwait(false);
        var day = ParseDate(request.LocalDate) ?? DateOnly.FromDateTime(DateTime.Now);
        var dateText = AppUsageRepository.FormatDate(day);
        var (startUtc, endUtc) = EnergyRepository.GetLocalDayUtcRange(day, TimeZoneInfo.Local);
        var meta = (await _repository.GetMetaAsync(token).ConfigureAwait(false))
            .ToDictionary(row => row.AppKey, StringComparer.Ordinal);
        var aliases = ResolveAliases(meta);
        var dayRows = await _repository.GetDaysAsync(dateText, dateText, token).ConfigureAwait(false);
        var events = await _repository.GetEventsAsync(startUtc.ToUnixTimeSeconds(), endUtc.ToUnixTimeSeconds(), token)
            .ConfigureAwait(false);
        var gaps = await _repository.GetGapsAsync(startUtc.ToUnixTimeSeconds(), endUtc.ToUnixTimeSeconds(), token)
            .ConfigureAwait(false);

        var cores = Math.Max(1, Environment.ProcessorCount);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dayLength = (endUtc - startUtc).TotalSeconds;
        var nowUtc = DateTimeOffset.UtcNow;
        var covered = day < today ? dayLength : Math.Clamp((nowUtc - startUtc).TotalSeconds, 60, dayLength);

        var bridges = gaps
            .Where(gap => string.Equals(gap.Reason, "offline", StringComparison.OrdinalIgnoreCase))
            .Select(gap => (Start: gap.StartUtc, End: gap.EndUtc))
            .ToArray();
        var bootUtc = SystemClockMonitor.BootTimeUtc().ToUnixTimeSeconds();
        var spansByKey = events
            .Where(item => item.AppKey is not (AppStatKeys.System or AppStatKeys.Baseline))
            .GroupBy(item => item.AppKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => MergeSpans(group, startUtc.ToUnixTimeSeconds(), endUtc.ToUnixTimeSeconds(), bridges, bootUtc),
                StringComparer.Ordinal);

        var byName = new Dictionary<string, List<(string Target, AppDayRow Row, IReadOnlyList<AppSpan> Spans)>>(StringComparer.OrdinalIgnoreCase);
        var machineCpuSeconds = 0d;
        var machineGpuUnits = 0d;
        long machineUp = 0;
        long machineDown = 0;
        foreach (var row in dayRows)
        {
            if (string.Equals(row.AppKey, AppStatKeys.Baseline, StringComparison.Ordinal)) continue;
            if (string.Equals(row.AppKey, AppStatKeys.System, StringComparison.Ordinal))
            {
                machineCpuSeconds += row.CpuSeconds;
                machineGpuUnits += row.GpuUnits;
                machineUp += row.NetUpBytes;
                machineDown += row.NetDownBytes;
                continue;
            }

            machineCpuSeconds += row.CpuSeconds;
            machineGpuUnits += row.GpuUnits;
            machineUp += row.NetUpBytes;
            machineDown += row.NetDownBytes;

            var target = aliases.TryGetValue(row.AppKey, out var mapped) ? mapped : row.AppKey;
            if (IsHidden(meta, target)) continue;
            var name = ResolveName(meta, target);
            if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
            list.Add((target,
                row,
                spansByKey.TryGetValue(row.AppKey, out var spans) ? spans : []));
        }

        var entries = new List<AppLoadEntry>(byName.Count);
        var cpuWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        var gpuWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        var colors = ColorIndex(byName.Keys);
        foreach (var (name, members) in byName)
        {
            var primary = members.OrderByDescending(member => member.Row.Seconds).First();
            var spans = MergeSpans(members.SelectMany(member => member.Spans));
            var unionSeconds = spans.Sum(span => (double)(span.EndUtc - span.StartUtc));
            var seconds = unionSeconds > 0
                ? unionSeconds
                : members.Sum(member => member.Row.Seconds);
            var cpuSeconds = members.Sum(member => member.Row.CpuSeconds);
            var gpuUnits = members.Sum(member => member.Row.GpuUnits);
            var up = members.Sum(member => member.Row.NetUpBytes);
            var down = members.Sum(member => member.Row.NetDownBytes);
            var cpuPercent = seconds > 0 ? Math.Clamp(cpuSeconds / seconds / cores * 100d, 0, 100) : 0;
            var gpuPercent = seconds > 0 ? Math.Clamp(gpuUnits / seconds, 0, 100) : 0;
            var cpuPeak = members.Max(member => member.Row.CpuPeak);
            var gpuPeak = members.Max(member => member.Row.GpuPeak);
            var cpuMin = members.Where(member => member.Row.CpuMin > 0).Select(member => member.Row.CpuMin).DefaultIfEmpty(0).Min();
            var gpuMin = members.Where(member => member.Row.GpuMin > 0).Select(member => member.Row.GpuMin).DefaultIfEmpty(0).Min();
            cpuWeights[primary.Target] = cpuSeconds;
            gpuWeights[primary.Target] = gpuUnits;
            entries.Add(new AppLoadEntry(primary.Target, name, meta.TryGetValue(primary.Target, out var primaryMeta) ? primaryMeta.ExePath : null, seconds, cpuPercent, gpuPercent,
                up, down,
                CategoryCatalog.Match(_categories, primary.Target, name),
                ParseColor(meta, primary.Target) ?? AppPalette.ColorAt(colors.TryGetValue(name, out var position) ? position : 0),
                IconOf(meta, primary.Target),
                IsSystemRow(meta.TryGetValue(primary.Target, out var metaRow) ? metaRow : null, primary.Target),
                meta.TryGetValue(primary.Target, out var entryMeta) &&
                string.Equals(entryMeta.MergedInto, AppStatKeys.Background, StringComparison.Ordinal),
                cpuPeak, gpuPeak, cpuMin, gpuMin));
        }

        var totalCpuWeight = cpuWeights.Values.Sum();
        var totalGpuWeight = gpuWeights.Values.Sum();
        if (totalCpuWeight > 0 || totalGpuWeight > 0)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                entries[index] = entry with
                {
                    CpuShare = totalCpuWeight > 0 && cpuWeights.TryGetValue(entry.Key, out var cpuWeight) ? cpuWeight / totalCpuWeight * 100d : 0,
                    GpuShare = totalGpuWeight > 0 && gpuWeights.TryGetValue(entry.Key, out var gpuWeight) ? gpuWeight / totalGpuWeight * 100d : 0
                };
            }
        }
        entries.Sort((left, right) => right.CpuPercent.CompareTo(left.CpuPercent));
        var machineCpu = covered > 0 ? Math.Clamp(machineCpuSeconds / covered / cores * 100d, 0, 100) : 0;
        var machineGpu = covered > 0 ? Math.Clamp(machineGpuUnits / covered, 0, 100) : 0;
        var (machineCpuPeak, machineGpuPeak) = await _repository.GetMachinePeakAsync(dateText, token).ConfigureAwait(false);
        return new AppLoadResult(dateText,
            new AppLoadMachineRow(covered, machineCpu, machineGpu, machineUp, machineDown, machineCpuPeak, machineGpuPeak, _machineCpuMin, _machineGpuMin),
            entries, request.RequestId);
    }



    private static double AppendMin(double current, double sample) =>
        sample <= 0 ? current : current <= 0 ? sample : Math.Min(current, sample);

    private static double FiniteNonNegative(double? value) =>
        value is double number && double.IsFinite(number) ? Math.Max(0, number) : 0;

    // ---------------------------------------------------------------- summary report

    public async Task<AppSummaryResult> BuildSummaryAsync(AppSummaryRequest request, CancellationToken token)
    {
        try
        {
            var stats = await QueryAsync(new AppStatsRequest(request.Kind, request.LocalDate), token).ConfigureAwait(false);
            var markdown = BuildMarkdown(stats);
            var directory = Path.Combine(AppPaths.SystemDefault.DataRoot, "reports");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"summary-{stats.LocalDate}-{stats.Kind}.md");
            File.WriteAllText(path, markdown, new UTF8Encoding(false));
            return new AppSummaryResult(true, path, null, request.RequestId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to build the summary report");
            return new AppSummaryResult(false, null, ex.Message, request.RequestId);
        }
    }

    private static string BuildMarkdown(AppStatsResult stats)
    {
        var builder = new StringBuilder();
        var week = string.Equals(stats.Kind, "week", StringComparison.OrdinalIgnoreCase);
        builder.AppendLine($"# 应用{(week ? "周" : "日")}摘要 · {stats.LocalDate}");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 数值 |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| 应用运行时长合计 | {CategoryCatalog.Format(stats.Summary.TotalSeconds)} |");
        builder.AppendLine($"| 其中活跃时长 | {CategoryCatalog.Format(stats.Summary.ActiveSeconds)} |");
        builder.AppendLine($"| 打开应用个数 | {stats.Summary.AppCount} |");
        builder.AppendLine($"| 应用耗电合计 | {FormatEnergy(stats.Summary.TotalWh)} |");
        builder.AppendLine($"| 系统进程耗电 | {FormatEnergy(stats.Summary.SystemWh)} |");
        builder.AppendLine($"| 基线与其他 | {FormatEnergy(stats.Summary.BaselineWh)} |");
        builder.AppendLine();
        builder.AppendLine("## 应用排行");
        builder.AppendLine();
        builder.AppendLine("| 应用 | 分类 | 运行时长 | 活跃时长 | 耗电 |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var row in stats.Timeline.Take(20))
        {
            var wh = stats.Power.FirstOrDefault(power => power.Key == row.Key)?.Wh ?? row.Wh;
            builder.AppendLine($"| {row.Name} | {row.Category ?? CategoryCatalog.Fallback} | {CategoryCatalog.Format(row.Seconds)} | {CategoryCatalog.Format(row.ActiveSeconds)} | {FormatEnergy(wh)} |");
        }

        var groups = stats.Timeline.GroupBy(row => row.Category ?? CategoryCatalog.Fallback)
            .Select(group => (Category: group.Key, Seconds: group.Sum(row => row.Seconds), Wh: group.Sum(row => row.Wh)))
            .OrderByDescending(item => item.Seconds);
        builder.AppendLine();
        builder.AppendLine("## 分类汇总");
        builder.AppendLine();
        builder.AppendLine("| 分类 | 运行时长 | 耗电 |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var group in groups)
            builder.AppendLine($"| {group.Category} | {CategoryCatalog.Format(group.Seconds)} | {FormatEnergy(group.Wh)} |");
        builder.AppendLine();
        builder.AppendLine($"_生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm}_");
        return builder.ToString();
    }

    private static string FormatEnergy(double wattHours) =>
        wattHours >= 1000 ? $"{wattHours / 1000d:0.00} kWh" : $"{wattHours:0.0} Wh";

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Colour slot per application, computed from every application of the period (not only the ones
    /// that pass the display filters) so the runtime page and the power page agree on each colour.
    /// </summary>




    private static Dictionary<string, int> ColorIndex(IEnumerable<string> keys)
    {
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var key in keys.OrderBy(key => key, StringComparer.Ordinal))
            positions[key] = index++;
        return positions;
    }


    /// <summary>System classification, mirroring the catalogue rule (manual override wins).</summary>




    /// <summary>
    /// Manual renames win; otherwise the catalog is authoritative so that improving the built-in names
    /// also updates history that was recorded before the improvement.
    /// </summary>


    private static IReadOnlyList<AppDayPoint> BuildWeekSeries(IReadOnlyList<AppDayRow> rows, DateOnly endDay)
    {
        var startDay = endDay.AddDays(-13);
        var points = new List<AppDayPoint>(14);
        for (var offset = 0; offset < 14; offset++)
        {
            var text = AppUsageRepository.FormatDate(startDay.AddDays(offset));
            var dayRows = rows.Where(r => r.LocalDate == text).ToArray();
            var apps = dayRows.Where(r => r.AppKey is not (AppStatKeys.System or AppStatKeys.Baseline)).ToArray();
            points.Add(new AppDayPoint(text, apps.Sum(r => r.Seconds), apps.Sum(r => r.Wh),
                dayRows.Where(r => r.AppKey == AppStatKeys.System).Sum(r => r.Wh),
                dayRows.Where(r => r.AppKey == AppStatKeys.Baseline).Sum(r => r.Wh)));
        }
        return points;
    }

    private static bool IsAlwaysShown(PanelSettings settings, string key, string? name)
    {
        foreach (var entry in settings.AppStatsAlwaysShow)
        {
            if (string.Equals(entry, key, StringComparison.OrdinalIgnoreCase)) return true;
            if (name is not null && string.Equals(entry, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }



    /// <summary>Sampling hiccups shorter than this are always treated as continuous.</summary>
    private const long MaxSamplingGapSeconds = 120;

    private static IReadOnlyList<AppSpan> MergeSpans(IEnumerable<AppEventRow> events, long fromUtc, long toUtc,
        IReadOnlyList<(long Start, long End)>? bridges = null, long bootUtc = 0)
    {
        var spans = new List<AppSpan>();
        foreach (var item in events.OrderBy(e => e.StartUtc))
        {
            var start = Math.Max(item.StartUtc, fromUtc);
            var end = Math.Min(item.EndUtc, toUtc);
            if (end <= start) continue;
            if (spans.Count > 0 && start <= spans[^1].EndUtc)
            {
                var last = spans[^1];
                spans[^1] = last with { EndUtc = Math.Max(last.EndUtc, end) };
                continue;
            }
            if (spans.Count > 0 && ShouldBridge(spans[^1].EndUtc, start, bridges, bootUtc))
            {
                var last = spans[^1];
                spans[^1] = last with { EndUtc = end };
                continue;
            }
            spans.Add(new AppSpan(start, end));
        }
        return spans;
    }

    /// <summary>
    /// True when the application kept running across the hole: either the hole is a short sampling
    /// hiccup, or it is fully covered by a service-restart gap and no reboot happened in between.
    /// Rebooting and sleeping always break the bar.
    /// </summary>
    private static bool ShouldBridge(long endUtc, long nextStartUtc, IReadOnlyList<(long Start, long End)>? bridges,
        long bootUtc)
    {
        if (nextStartUtc <= endUtc) return true;
        if (bootUtc > endUtc && bootUtc < nextStartUtc) return false;
        var hole = nextStartUtc - endUtc;
        if (hole <= MaxSamplingGapSeconds) return true;
        if (bridges is null || bridges.Count == 0) return false;
        foreach (var (start, end) in bridges)
            if (start <= endUtc + 2 && end + 2 >= nextStartUtc) return true;
        return false;
    }

    private static double UnionGaps(IReadOnlyList<AppGapRow> gaps, string reason, long fromUtc, long toUtc)
    {
        var spans = gaps
            .Where(gap => string.Equals(gap.Reason, reason, StringComparison.OrdinalIgnoreCase))
            .Select(gap => new AppSpan(Math.Max(gap.StartUtc, fromUtc), Math.Min(gap.EndUtc, toUtc)))
            .Where(span => span.EndUtc > span.StartUtc)
            .OrderBy(span => span.StartUtc)
            .ToArray();
        if (spans.Length == 0) return 0;
        var total = 0d;
        var start = spans[0].StartUtc;
        var end = spans[0].EndUtc;
        foreach (var span in spans.Skip(1))
        {
            if (span.StartUtc <= end) { end = Math.Max(end, span.EndUtc); continue; }
            total += end - start;
            start = span.StartUtc;
            end = span.EndUtc;
        }
        return total + (end - start);
    }

    /// <summary>Union of every member's intervals per local day, summed over the requested days.</summary>
    private static Dictionary<string, double> UnionSecondsPerGroup(IReadOnlyList<AppEventRow> events,
        Func<string, string> groupOf, DateOnly firstDay, int days)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var offset = 0; offset < days; offset++)
        {
            var (from, to) = EnergyRepository.GetLocalDayUtcRange(firstDay.AddDays(offset), TimeZoneInfo.Local);
            var fromUtc = from.ToUnixTimeSeconds();
            var toUtc = to.ToUnixTimeSeconds();
            var grouped = events
                .Where(item => item.EndUtc > fromUtc && item.StartUtc < toUtc)
                .Select(item => (Group: groupOf(item.AppKey), Start: Math.Max(item.StartUtc, fromUtc), End: Math.Min(item.EndUtc, toUtc)))
                .GroupBy(item => item.Group, StringComparer.Ordinal);
            foreach (var group in grouped)
            {
                long? start = null;
                long end = 0;
                var total = 0d;
                foreach (var piece in group.OrderBy(item => item.Start))
                {
                    if (start is null) { start = piece.Start; end = piece.End; continue; }
                    if (piece.Start <= end) { end = Math.Max(end, piece.End); continue; }
                    total += end - start.Value;
                    start = piece.Start;
                    end = piece.End;
                }
                if (start is not null) total += end - start.Value;
                totals[group.Key] = totals.TryGetValue(group.Key, out var existing) ? existing + total : total;
            }
        }
        return totals;
    }

    private static double UnionSeconds(IEnumerable<IReadOnlyList<AppSpan>> all)
    {
        var intervals = all.SelectMany(s => s).OrderBy(s => s.StartUtc).ToArray();
        var total = 0d;
        long? currentStart = null;
        long currentEnd = 0;
        foreach (var span in intervals)
        {
            if (currentStart is null) { currentStart = span.StartUtc; currentEnd = span.EndUtc; continue; }
            if (span.StartUtc <= currentEnd) { currentEnd = Math.Max(currentEnd, span.EndUtc); continue; }
            total += currentEnd - currentStart.Value;
            currentStart = span.StartUtc;
            currentEnd = span.EndUtc;
        }
        if (currentStart is not null) total += currentEnd - currentStart.Value;
        return total;
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public async Task<AppCatalogResult> QueryCatalogAsync(AppCatalogRequest request, CancellationToken token)
    {
        await EnsureInitializedAsync(token).ConfigureAwait(false);
        var day = DateOnly.FromDateTime(DateTime.Now);
        var dayRows = await _repository.GetDaysAsync(AppUsageRepository.FormatDate(day),
            AppUsageRepository.FormatDate(day), token).ConfigureAwait(false);
        var meta = (await _repository.GetMetaAsync(token).ConfigureAwait(false))
            .ToDictionary(row => row.AppKey, StringComparer.Ordinal);
        var automatic = AppCompanionRules.Match(meta);
        var aliases = ResolveAliases(meta, automatic);
        var totals = await _repository.GetTotalsAsync(token).ConfigureAwait(false);
        var allSpans = await GetCatalogSpansAsync(token).ConfigureAwait(false);
        var todayRange = EnergyRepository.GetLocalDayUtcRange(day, TimeZoneInfo.Local);
        var todayStartUtc = todayRange.StartUtc.ToUnixTimeSeconds();
        var todayEndUtc = todayRange.EndUtc.ToUnixTimeSeconds();

        var today = dayRows
            .Where(row => row.AppKey is not (AppStatKeys.System or AppStatKeys.Baseline))
            .GroupBy(row => row.AppKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (Seconds: group.Sum(row => row.Seconds), Wh: group.Sum(row => row.Wh)), StringComparer.Ordinal);

        var keys = meta.Keys.Where(key => key is not (AppStatKeys.System or AppStatKeys.Baseline)).ToList();
        foreach (var key in today.Keys)
            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);

        double TotalOf(string key) => totals.TryGetValue(key, out var value) ? value.Seconds : 0;

        // Group by display name, but a key explicitly unmerged from a same-name sibling keeps its own row.
        var groupsByName = new Dictionary<string, CatalogGroup>(StringComparer.OrdinalIgnoreCase);
        var groupsByKey = new Dictionary<string, CatalogGroup>(StringComparer.Ordinal);
        var groupList = new List<CatalogGroup>();
        foreach (var key in keys)
        {
            if (aliases.ContainsKey(key)) continue;
            var name = ResolveName(meta, key);
            meta.TryGetValue(key, out var entry);
            var groupKey = IsBucket(entry?.MergedInto) ? name + "\0" + entry!.MergedInto : name;
            CatalogGroup group;
            if (entry?.MergeDetached == true)
            {
                group = new CatalogGroup(name);
                groupList.Add(group);
            }
            else if (groupsByName.TryGetValue(groupKey, out var existing)) group = existing;
            else
            {
                group = new CatalogGroup(name);
                groupsByName[groupKey] = group;
                groupList.Add(group);
            }
            group.Keys.Add(key);
            groupsByKey[key] = group;
        }

        foreach (var key in keys)
        {
            if (!aliases.TryGetValue(key, out var target)) continue;
            if (!groupsByKey.TryGetValue(target, out var group))
            {
                group = new CatalogGroup(ResolveName(meta, target));
                groupList.Add(group);
                groupsByKey[target] = group;
            }
            group.Members.Add(key);
        }

        var rows = new List<AppCatalogRow>(groupList.Count);
        foreach (var group in groupList)
        {
            if (group.Keys.Count == 0) continue;
            var primary = group.Keys.OrderByDescending(TotalOf).ThenBy(key => key, StringComparer.Ordinal).First();
            meta.TryGetValue(primary, out var row);
            // Same-name processes stay flat; an application merged into this one keeps its own identity
            // and expands into its processes one level deeper.
            var members = group.Keys
                .Select(key => new AppCatalogMember(key, meta.TryGetValue(key, out var entry) ? entry.ExePath : null,
                    UnionOrFallback(allSpans, [key], long.MinValue, long.MaxValue, TotalOf(key)),
                    false, meta.TryGetValue(key, out var own) && own.Hidden))
                .Concat(group.Members
                    .GroupBy(key => ResolveName(meta, key), StringComparer.OrdinalIgnoreCase)
                    .SelectMany(subGroup =>
                    {
                        var keys = subGroup.ToArray();
                        if (keys.Length == 1)
                        {
                            var key = keys[0];
                            return new AppCatalogMember[] { new(key, meta.TryGetValue(key, out var entry) ? entry.ExePath : null,
                                TotalOf(key), true, meta.TryGetValue(key, out var merged) && merged.Hidden,
                                Automatic: automatic.ContainsKey(key)) };
                        }
                        var children = keys
                            .Select(key => new AppCatalogMember(key, meta.TryGetValue(key, out var entry) ? entry.ExePath : null,
                                TotalOf(key), false, meta.TryGetValue(key, out var own) && own.Hidden,
                                System.IO.Path.GetFileName(key), Automatic: automatic.ContainsKey(key)))
                            .OrderByDescending(child => child.TotalSeconds)
                            .ToArray();
                        var head = keys.OrderByDescending(TotalOf).ThenBy(key => key, StringComparer.Ordinal).First();
                        return new AppCatalogMember[] { new(head, meta.TryGetValue(head, out var headEntry) ? headEntry.ExePath : null,
                            UnionOrFallback(allSpans, keys, long.MinValue, long.MaxValue, children.Sum(child => child.TotalSeconds)), true,
                            meta.TryGetValue(head, out var headMeta) && headMeta.Hidden, subGroup.Key, children,
                            children.All(child => child.Automatic)) };
                    }))
                .OrderByDescending(member => member.TotalSeconds)
                .ToArray();

            // A parent row lasts as long as the union of its processes, never the sum of them.
            var groupKeys = group.Keys.Concat(group.Members).ToArray();
            var totalSeconds = UnionOrFallback(allSpans, groupKeys, long.MinValue, long.MaxValue, members.Sum(member => member.TotalSeconds));
            var todaySeconds = UnionOrFallback(allSpans, groupKeys, todayStartUtc, todayEndUtc,
                groupKeys.Sum(key => today.TryGetValue(key, out var usage) ? usage.Seconds : 0));
            // Fall back to the key itself when the metadata row has no executable path.
            var pathHint = row?.ExePath ?? (primary.StartsWith("exe:", StringComparison.Ordinal) ? null : primary);
            var bucket = row?.MergedInto;
            var background = string.Equals(bucket, AppStatKeys.Background, StringComparison.Ordinal);
            var system = string.Equals(bucket, AppStatKeys.System, StringComparison.Ordinal) ||
                (row?.SystemOverride is int forced
                    ? forced != 0
                    : primary == AppStatKeys.System || AppIdentityResolver.IsSystemPath(pathHint));
            rows.Add(new AppCatalogRow(primary, group.Name, row?.ExePath,
                bucket == AppStatKeys.Testing ? "测试进程" : CategoryCatalog.Match(_categories, primary, group.Name),
                ParseColor(meta, primary) ?? 0,
                row?.Hidden ?? false,
                string.IsNullOrWhiteSpace(row?.MergedInto) ? null : row!.MergedInto,
                IconOf(meta, primary), todaySeconds,
                group.Keys.Concat(group.Members).Sum(key => today.TryGetValue(key, out var usage) ? usage.Wh : 0),
                totalSeconds, totalSeconds,
                system,
                members, row?.SystemOverride, background));
        }

        rows.Sort((left, right) => right.TotalSeconds.CompareTo(left.TotalSeconds));

        // Deterministic colours: alphabetical position of the name, unless the user picked one.
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var name in rows.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal))
            if (!positions.ContainsKey(name)) positions[name] = index++;
        for (var position = 0; position < rows.Count; position++)
            if (rows[position].Color == 0)
                rows[position] = rows[position] with { Color = AppPalette.ColorAt(positions[rows[position].Name]) };

        // The two pinned classifications are not processes: give them the union of their members.
        var backgroundKeys = meta.Where(pair => string.Equals(pair.Value.MergedInto, AppStatKeys.Background, StringComparison.Ordinal))
            .Select(pair => pair.Key).ToArray();
        var systemKeys = meta.Where(pair => string.Equals(pair.Value.MergedInto, AppStatKeys.System, StringComparison.Ordinal))
            .Select(pair => pair.Key).ToArray();
        var backgroundSeconds = UnionOrFallback(allSpans, backgroundKeys, long.MinValue, long.MaxValue, 0);
        var systemSeconds = UnionOrFallback(allSpans, systemKeys, long.MinValue, long.MaxValue, 0);
        var backgroundToday = UnionOrFallback(allSpans, backgroundKeys, todayStartUtc, todayEndUtc, 0);
        var systemToday = UnionOrFallback(allSpans, systemKeys, todayStartUtc, todayEndUtc, 0);
        return new AppCatalogResult(rows, request.RequestId, backgroundSeconds, systemSeconds, backgroundToday, systemToday);
    }
    private Dictionary<string, List<AppSpan>>? _catalogSpans;
    private DateTimeOffset _catalogSpansAt;

    /// <summary>All recorded intervals per application, cached for a minute so the catalog can union them.</summary>
    private async Task<Dictionary<string, List<AppSpan>>> GetCatalogSpansAsync(CancellationToken token)
    {
        if (_catalogSpans is not null && (DateTimeOffset.UtcNow - _catalogSpansAt).TotalSeconds < 60) return _catalogSpans;
        var events = await _repository.GetEventsAsync(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), token).ConfigureAwait(false);
        _catalogSpans = events
            .GroupBy(item => item.AppKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(item => new AppSpan(item.StartUtc, item.EndUtc)).ToList(),
                StringComparer.Ordinal);
        _catalogSpansAt = DateTimeOffset.UtcNow;
        return _catalogSpans;
    }

    /// <summary>Union in seconds of the given applications inside the window, or the fallback when unknown.</summary>
    private static double UnionOrFallback(IReadOnlyDictionary<string, List<AppSpan>> spansByKey,
        IEnumerable<string> keys, long fromUtc, long toUtc, double fallback)
    {
        var clipped = new List<AppSpan>();
        foreach (var key in keys)
        {
            if (!spansByKey.TryGetValue(key, out var spans)) continue;
            foreach (var span in spans)
            {
                var start = Math.Max(span.StartUtc, fromUtc);
                var end = Math.Min(span.EndUtc, toUtc);
                if (end > start) clipped.Add(new AppSpan(start, end));
            }
        }
        var union = MergeSpans(clipped).Sum(span => (double)(span.EndUtc - span.StartUtc));
        return union > 0 ? union : fallback;
    }

    private sealed class CatalogGroup(string name)
    {
        public string Name { get; } = name;
        public List<string> Keys { get; } = [];

        /// <summary>Keys merged into one of the keys above.</summary>
        public List<string> Members { get; } = [];
    }


    private static uint? ParseColor(IReadOnlyDictionary<string, AppMetaRow> meta, string key)
    {
        if (!meta.TryGetValue(key, out var row) || string.IsNullOrWhiteSpace(row.Color)) return null;
        var text = row.Color.Trim().TrimStart('#');
        if (text.Length != 6) return null;
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? 0xFF000000u | rgb
            : null;
    }
    private static string? IconOf(IReadOnlyDictionary<string, AppMetaRow> meta, string key) =>
        meta.TryGetValue(key, out var row) && !string.IsNullOrWhiteSpace(row.IconPath) ? row.IconPath : null;
    private static Dictionary<string, string> ResolveAliases(IReadOnlyDictionary<string, AppMetaRow> meta,
        IReadOnlyDictionary<string, string>? automatic = null)
    {
        var direct = new Dictionary<string, string>(automatic ?? AppCompanionRules.Match(meta), StringComparer.Ordinal);
        foreach (var (key, row) in meta)
        {
            var target = row.MergedInto;
            if (string.IsNullOrEmpty(target) || string.Equals(target, key, StringComparison.Ordinal)) continue;
            if (!IsBucket(target)) direct[key] = target;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, first) in direct)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { key };
            var target = first;
            while (seen.Add(target))
            {
                if (direct.TryGetValue(target, out var next)) { target = next; continue; }
                map[key] = target;
                break;
            }
        }
        return map;
    }
    private static bool IsSystemRow(AppMetaRow? row, string key)
    {
        if (row?.SystemOverride is int forced) return forced != 0;
        if (row is not null && string.Equals(row.MergedInto, AppStatKeys.System, StringComparison.Ordinal)) return true;
        var path = row?.ExePath ?? (key.StartsWith("exe:", StringComparison.Ordinal) ? null : key);
        return AppIdentityResolver.IsSystemPath(path);
    }
    private static bool IsBucket(string? target) =>
        target is AppStatKeys.Background or AppStatKeys.System or AppStatKeys.Testing;
    private static bool IsHidden(IReadOnlyDictionary<string, AppMetaRow> meta, string key) =>
        meta.TryGetValue(key, out var row) && row.Hidden;
    private static string ResolveName(IReadOnlyDictionary<string, AppMetaRow> meta, string key)
    {
        if (meta.TryGetValue(key, out var row) && row.Source == "manual" && !string.IsNullOrWhiteSpace(row.DisplayName))
            return row.DisplayName;
        return AppNameCatalog.DisplayName(key);
    }
    private static IReadOnlyList<AppSpan> MergeSpans(IEnumerable<AppSpan> spans)
    {
        var merged = new List<AppSpan>();
        foreach (var span in spans.OrderBy(s => s.StartUtc))
        {
            if (merged.Count > 0 && span.StartUtc <= merged[^1].EndUtc)
                merged[^1] = merged[^1] with { EndUtc = Math.Max(merged[^1].EndUtc, span.EndUtc) };
            else merged.Add(span);
        }
        return merged;
    }
}
