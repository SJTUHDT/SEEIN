using System.Diagnostics;
using Seein.Core;
using Seein.Core.Lighting;
using Seein.Core.Sensors;

namespace Seein.Service;

public sealed class RgbService : BackgroundService
{
    private readonly ILogger<RgbService> _logger;
    private readonly PanelState _state;
    private readonly RgbEngineHost _engine;
    private readonly OpenRgbProvider _provider;
    private readonly IMsiMysticLightBoard? _msi;
    private RgbDeviceInfo? _board;
    private RgbEffectSettings? _boardEffect;
    private RgbWriteResult? _boardResult;
    private DateTimeOffset _retryBoardAfter;
    private RgbEffectSettings? _uniformGroup;
    private RgbColor? _uniformColor;
    private DateTimeOffset _nextBoardFrame;
    private string? _groupPlayback;
    private PlaybackPeakReader? _audio;
    private DateTimeOffset _nextAudio;
    private PlaybackLevels? _levels;
    private bool _firmwareOnly;
    private bool _ringActive;
    private DateTimeOffset _nextRingVerification;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Address, int? Zone), int> _lastFrames = [];
    private readonly HashSet<(string Device, int? Zone)> _paused = [];
    private volatile RgbSettings _settings = RgbEffects.DefaultSettings();
    private readonly Dictionary<(string Device, int Zone, string Name), RgbColorCalibration> _calibrations = [];
    private string? _calibrationError;
    private volatile RgbStatus _status = new(RgbProviderState.NotConfigured, "灯效引擎正在初始化", []);
    private DateTimeOffset _nextRefresh;
    private string? _diagnostic;
    private string? _matchError;
    private bool _initialized;

    public RgbService(ILogger<RgbService> logger, PanelState state, RgbEngineHost engine)
        : this(logger, state, engine, new OpenRgbProvider(engine.Port), new MsiMysticLightBoard()) { }

    internal RgbService(ILogger<RgbService> logger, PanelState state, RgbEngineHost engine, OpenRgbProvider provider,
        IMsiMysticLightBoard? msi = null)
    {
        _logger = logger;
        _state = state;
        _engine = engine;
        _provider = provider;
        _msi = msi;
    }

    public RgbSettings Settings => _settings;
    public event Action<RgbSettings>? SettingsChanged;

    public void Initialize(RgbSettings? settings)
    {
        _settings = RgbEffects.Normalize(settings);
        IndexCalibrations(_settings.ZoneCalibrations);
        Volatile.Write(ref _initialized, true);
    }

    public RgbStatus GetStatus() => _status with
    {
        Enabled = _settings.Enabled,
        ActiveProfileId = _settings.ActiveProfileId,
        LastError = _status.LastError ?? _calibrationError ?? _matchError ?? MissingGroupInput() ??
            (_audio is not null && _levels is null ? "音乐律动正在等待可用的播放设备" : null),
        GroupPlayback = _settings.Enabled && _provider.IsAvailable ? _groupPlayback : null,
        MusicPeak = _settings.Enabled && _provider.IsAvailable && _audio is not null ? _levels?.Master : null
    };

    private string? MissingGroupInput()
    {
        if (!_settings.Enabled || _settings.Targets is not null ||
            (_settings.GroupEffect is null && !RgbEffects.IsGroupProfile(ActiveProfile()))) return null;
        var missing = _status.Devices.FirstOrDefault(device => RgbEffects.RequiresGroupExternalInput(device.Info) &&
            RgbEffects.ExternalInput(device.Info) is null);
        return missing is null ? null : $"{missing.Info.Name} 缺少外部同步模式，请重新扫描设备";
    }

    public async Task<RgbStatus> RefreshAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_nextRefresh <= DateTimeOffset.UtcNow || _provider.NeedsRefresh)
                await RefreshCoreAsync(false, token).ConfigureAwait(false);
            return GetStatus();
        }
        finally { _gate.Release(); }
    }

    private async Task RefreshCoreAsync(bool rescan, CancellationToken token)
    {
        _nextRefresh = DateTimeOffset.UtcNow.AddSeconds(_provider.IsAvailable ? 10 : 2);
        if (!_provider.IsAvailable && !_engine.EnsureReady(_settings.AutoStartEngine))
        {
            _status = new(RgbProviderState.NotConfigured, _engine.LastError, [], LastError: _engine.LastError);
            _lastFrames.Clear();
            _board = null;
            ForgetBoardMode();
            return;
        }

        if (rescan)
            _status = new(RgbProviderState.Connecting, "正在重新扫描灯效设备…", []);
        var previous = _status.Devices;
        var devices = await _provider.RefreshAsync(rescan, token).ConfigureAwait(false);
        foreach (var saved in _settings.ZoneSizes ?? [])
        {
            var candidates = devices.Where(item => item.Info.DeviceKey == saved.DeviceKey).Take(2).ToArray();
            if (candidates.Length != 1) continue;
            var zone = candidates[0].Info.Zones.ElementAtOrDefault(saved.ZoneIndex);
            if (zone is null || zone.Name != saved.ZoneName || zone.LedCount == saved.Size ||
                zone.Type != RgbZoneType.Linear || (zone.Flags & 3) == 0 ||
                saved.Size < zone.LedsMin || saved.Size > zone.LedsMax) continue;
            var result = await _provider.ResizeZoneAsync(candidates[0].Info.Address, saved.ZoneIndex, saved.Size, token).ConfigureAwait(false);
            if (result.Outcome == RgbWriteOutcome.ReadBack)
                devices = await _provider.RefreshAsync(token: token).ConfigureAwait(false);
        }
        if (!previous.Select(d => d.Info.Address).SequenceEqual(devices.Select(d => d.Info.Address)))
            _lastFrames.Clear();
        var boards = _msi is null ? [] : devices.Select(d => d.Info).Where(_msi.Matches).Take(2).ToArray();
        var board = boards.Length == 1 ? boards[0] : null;
        if (rescan || board?.Address != _board?.Address || board?.TopologyKey != _board?.TopologyKey)
            ForgetBoardMode();
        _board = board;
        _status = new(_provider.State, _provider.Message, devices, LastError: _provider.LastError);
        if (_settings is { Enabled: true, GroupEffect: not null, Targets: null } &&
            DiscoverGroupInputs() is { } discovered)
            Save(_settings with { ExternalControlPreferences = discovered });
        foreach (var target in EffectiveTargets().Where(target => target.HardwareMode is not null))
        {
            var device = UniqueDevice(entry => entry.Info.DeviceKey == target.DeviceKey);
            if (device is not null && device.Info.ActiveMode != target.HardwareMode)
                _lastFrames.Remove((device.Info.Address, null));
        }
        if (_provider.LastError is { } error && _diagnostic != error)
            _logger.LogWarning("RGB engine communication: {Error}", error);
        _diagnostic = _provider.LastError;
    }

    public async Task<RgbCommandResult> HandleCommandAsync(RgbCommandRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result = await ExecuteCommandAsync(request, token).ConfigureAwait(false);
            return new(result.Success, GetStatus(), result.Error, request.RequestId, _settings, result.Outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RGB command {Command} failed", request.Command);
            return new(false, GetStatus(), ex.Message, request.RequestId, _settings);
        }
        finally { _gate.Release(); }
    }

    private async Task<RgbWriteResult> ExecuteCommandAsync(RgbCommandRequest request, CancellationToken token)
    {
        switch (request.Command)
        {
            case "rescan":
                await RefreshCoreAsync(true, token).ConfigureAwait(false);
                return _provider.IsAvailable ? RgbWriteResult.ReadBack :
                    RgbWriteResult.Reject(GetStatus().LastError ?? "灯效引擎未就绪");
            case "refresh":
                await RefreshCoreAsync(false, token).ConfigureAwait(false);
                return _provider.IsAvailable ? RgbWriteResult.ReadBack : RgbWriteResult.Reject("灯效引擎未就绪");
            case "enable":
            case "disable":
                ResetPlayback();
                Save(_settings with { Enabled = request.Command == "enable" });
                return RgbWriteResult.Saved;
            case "effect":
                return SetEffect(request);
            case "groupEffect":
                return await ApplyGroupEffectAsync(request.GroupEffect, null, token).ConfigureAwait(false);
            case "saveGroupProfile":
                return SaveGroupProfile(request.ProfileName, request.GroupEffect);
            case "loadGroupProfile":
                var groupProfile = _settings.Profiles?.FirstOrDefault(profile => profile.Id == request.ProfileId);
                return groupProfile is not null && RgbEffects.IsGroupProfile(groupProfile)
                    ? await ApplyGroupEffectAsync(groupProfile.Effect, groupProfile.Id, token).ConfigureAwait(false)
                    : RgbWriteResult.Reject("请选择整机灯效配置");
            case "solidAll" when request.Colors is { Count: 1 } groupColor:
                return await SetSolidColorsAsync(_status.Devices, groupColor[0], token, wholeSystem: true).ConfigureAwait(false);
            case "profile":
            case "loadProfile":
                return await LoadProfileAsync(request.ProfileId, token).ConfigureAwait(false);
        }

        if (FindDevice(request) is not { } device)
            return RgbWriteResult.Reject("设备未选中或已经变化，请重新选择");
        var address = device.Info.Address;
        if (request.Command == "setZoneCalibration")
            return SetZoneCalibration(device.Info, request.ZoneIndex, request.Calibration);
        if (request.Command == "solid" && request.Colors is { Count: 1 } singleColor)
        {
            var solid = await SetSolidColorsAsync([device], singleColor[0], token).ConfigureAwait(false);
            if (solid.Success) RememberPreviewRoute(device.Info);
            return solid;
        }
        if (request.Command == "saveProfile")
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId) || string.IsNullOrWhiteSpace(request.EffectId))
                return RgbWriteResult.Reject("请输入配置名称");
            var profile = new RgbProfile(request.ProfileId, request.EffectId, device.Info.Name, ActiveProfile().Effect,
                request.ModeName, request.Colors, device.Info.DeviceKey, device.Info.TopologyKey, _settings.Targets);
            var profiles = (_settings.Profiles ?? []).Where(item => item.Id != profile.Id).Append(profile).ToArray();
            Save(_settings with { Profiles = profiles });
            return RgbWriteResult.Saved;
        }

        if (request.Command == "bootMode" && request.ModeValue is int modeIndex)
        {
            var mode = device.Info.Modes.ElementAtOrDefault(modeIndex);
            if (mode is null) return RgbWriteResult.Reject("硬件模式无效");
            if (_status.Devices.Count(entry => entry.Info.DeviceKey == device.Info.DeviceKey) != 1)
                return RgbWriteResult.Reject("设备匹配有歧义，无法保存为开机效果");
            var hardware = new RgbEffectTarget(device.Info.DeviceKey, device.Info.TopologyKey, null,
                new RgbEffectSettings(RgbEffects.Static, request.Colors ?? []), mode.Name,
                ToModeValue(request.Speed), ToModeValue(request.Brightness));
            var targets = EffectiveTargets().Where(entry => entry.DeviceKey != hardware.DeviceKey).Append(hardware).ToArray();
            Resume(device.Info);
            Save(_settings with
            {
                Enabled = true, Targets = targets, SchemaVersion = 2,
                ExternalControlPreferences = RgbEffects.ExternalInput(device.Info) is null
                    ? _settings.ExternalControlPreferences : WithExternalInput(device.Info.DeviceKey, RgbEffects.IsExternalInput(mode.Name))
            });
            return RgbWriteResult.Saved;
        }

        var result = request.Command switch
        {
            "colors" when device.Info.SupportsDirect && device.Info.LedCount > 0 && request.Colors is { Count: > 0 } colors =>
                await WithDirectAsync(device.Info, () => _provider.ApplyAsync(address, request.BypassCalibration ? colors :
                    CorrectFrame(device.Info, null, colors, request.PaintedLedIndices), token)).ConfigureAwait(false),
            "zone" when request.ZoneIndex is int zone && device.Info.Zones.ElementAtOrDefault(zone) is { LedCount: > 0 } &&
                device.Info.SupportsDirect && request.Colors is { Count: > 0 } colors =>
                await WithDirectAsync(device.Info, () => _provider.ApplyZoneAsync(address, zone, request.BypassCalibration ? colors :
                    CorrectFrame(device.Info, zone, colors), token)).ConfigureAwait(false),
            "led" when request.LedIndex is int led && (uint)led < device.Info.LedCount &&
                device.Info.SupportsDirect && request.Colors is { Count: > 0 } colors =>
                await WithDirectAsync(device.Info, () => _provider.ApplySingleLedAsync(address, led, request.BypassCalibration ? colors[0] :
                    CorrectLed(device.Info, led, colors[0]), token)).ConfigureAwait(false),
            "resize" when request.ZoneIndex is int zone && request.ResizeSize is int size =>
                await _provider.ResizeZoneAsync(address, zone, size, token).ConfigureAwait(false),
            "mode" when request.ModeValue is int mode && device.Info.Modes.ElementAtOrDefault(mode) is { } selected =>
                await SetDeviceModeAsync(device.Info, selected, ToModeValue(request.Speed), ToModeValue(request.Brightness), token,
                    request.Colors is { Count: > 0 } modeColors ? modeColors[0] : null).ConfigureAwait(false),
            _ => RgbWriteResult.Reject("灯效命令或参数无效")
        };
        if (result.Success)
        {
            if (request.Command == "resize" && IsBoard(device.Info)) ForgetBoardMode();
            if (request.Command == "mode" && request.ModeValue is int selectedMode &&
                device.Info.Modes.ElementAtOrDefault(selectedMode) is { } updatedMode)
                RememberPreviewRoute(device.Info, updatedMode.Name);
            if (request.Command == "resize" && result.Outcome == RgbWriteOutcome.ReadBack &&
                request.ZoneIndex is int changedZone && request.ResizeSize is int size &&
                device.Info.Zones.ElementAtOrDefault(changedZone) is { } zoneInfo)
            {
                var sizes = (_settings.ZoneSizes ?? []).Where(entry =>
                    entry.DeviceKey != device.Info.DeviceKey || entry.ZoneIndex != changedZone);
                Save(_settings with { ZoneSizes = sizes.Append(new(device.Info.DeviceKey, changedZone, zoneInfo.Name, size)).ToArray(),
                    SchemaVersion = 2 });
            }
            var zoneToPause = request.Command == "zone" ? request.ZoneIndex : null;
            _paused.Add((device.Info.DeviceKey, zoneToPause));
            ForgetFrames(address);
            await RefreshCoreAsync(false, token).ConfigureAwait(false);
        }
        return result;
    }

    private async Task<RgbWriteResult> SetSolidColorsAsync(IReadOnlyList<RgbDeviceState> devices, RgbColor color,
        CancellationToken token, bool wholeSystem = false)
    {
        var targets = devices.Select(device => (Info: device.Info,
                Input: wholeSystem ? RgbEffects.GroupExternalInput(device.Info, _settings) : null))
            .Where(target => target.Input is not null || target.Info.CanSetSolidColor ||
                wholeSystem && (IsBoard(target.Info) || RgbEffects.RequiresGroupExternalInput(target.Info))).ToArray();
        if (targets.Length == 0) return RgbWriteResult.Reject("没有可上色的设备，请检查灯珠配置或重新扫描");

        var successes = new List<(RgbDeviceInfo Info, RgbMode? Input)>();
        var failures = new List<string>();
        var leastCertain = RgbWriteOutcome.ReadBack;
        foreach (var (info, input) in targets)
        {
            RgbWriteResult result;
            if (input is not null)
                result = await _provider.SetModeAsync(info.Address, input.Index, null, null, token).ConfigureAwait(false);
            else if (wholeSystem && RgbEffects.RequiresGroupExternalInput(info))
                result = RgbWriteResult.Reject("缺少外部同步模式，请重新扫描设备");
            else if (wholeSystem && IsBoard(info) && !HasAdditionalBoardCalibrations())
                result = SelectBoardMode(new RgbEffectSettings(RgbEffects.Static, [color]));
            else if (info.SupportsDirect && info.LedCount > 0)
                result = await WithDirectAsync(info, () => _provider.ApplyAsync(info.Address, CorrectFrame(info, null, [color]), token)).ConfigureAwait(false);
            else
                result = await _provider.SetModeAsync(info.Address, info.SolidColorMode!.Index, null, null, token, color).ConfigureAwait(false);
            if (!result.Success)
            {
                failures.Add($"{info.Name}（{result.Error}）");
                continue;
            }

            _paused.Add((info.DeviceKey, null));
            ForgetFrames(info.Address);
            successes.Add((info, input));
            if (result.Outcome < leastCertain) leastCertain = result.Outcome;
        }

        await RefreshCoreAsync(false, token).ConfigureAwait(false);
        if (failures.Count > 0)
            return RgbWriteResult.Reject($"已写入 {successes.Count}/{targets.Length} 个设备。失败：{string.Join("、", failures)}");

        var readBack = successes.All(target => MatchesSolidReadback(target.Info, target.Input, color));
        return new RgbWriteResult(readBack ? RgbWriteOutcome.ReadBack : leastCertain);
    }

    private bool MatchesSolidReadback(RgbDeviceInfo info, RgbMode? input, RgbColor color)
    {
        var device = _status.Devices.FirstOrDefault(entry => entry.Info.Address == info.Address);
        if (device is null) return false;
        if (input is not null) return device.Info.ActiveModeIndex == input.Index;
        if (IsBoard(info) && _boardEffect?.EffectId == RgbEffects.Static && _boardResult == RgbWriteResult.ReadBack)
            return _boardEffect.Colors.Count > 0 && _boardEffect.Colors[0] == color;
        if (info.SupportsDirect && info.LedCount > 0)
            return device.Colors.Count == info.LedCount &&
                device.Colors.Select((entry, led) => entry == CorrectLed(info, led, color)).All(matches => matches);

        var mode = device.Info.Modes.ElementAtOrDefault(device.Info.ActiveModeIndex);
        if (mode is null || mode.Index != info.SolidColorMode?.Index) return false;
        return mode.ColorMode == 2 ? mode.Colors.Count > 0 && mode.Colors.All(entry => entry == color)
            : device.Colors.Count == info.LedCount && device.Colors.All(entry => entry == color);
    }

    private static int? ToModeValue(double? value) =>
        value is >= 0 and <= int.MaxValue ? (int)Math.Round(value.Value) : null;

    private RgbWriteResult SetZoneCalibration(RgbDeviceInfo device, int? index, RgbColorCalibration? colors)
    {
        if (!device.SupportsDirect || index is not int zoneIndex ||
            device.Zones.ElementAtOrDefault(zoneIndex) is not { Type: RgbZoneType.Linear, LedCount: > 0 } zone ||
            string.IsNullOrWhiteSpace(device.DeviceKey) || UniqueDevice(item => item.Info.DeviceKey == device.DeviceKey) is null)
            return RgbWriteResult.Reject("请选择可唯一识别、支持直接控制的线性分区");

        var stored = _settings.ZoneCalibrations ?? [];
        if (colors is not null)
        {
            // A legacy client can replace the cube anchors without knowing about measured samples.
            // An explicit empty list clears those samples; a null calibration clears the entire zone.
            if (colors.Samples is null)
            {
                var previous = stored.Where(item => item?.DeviceKey == device.DeviceKey &&
                    item.ZoneIndex == zoneIndex && item.ZoneName == zone.Name).Take(2).ToArray();
                if (previous.Length > 1)
                    return RgbWriteResult.Reject("该分区有多份颜色校正记录，请明确提交完整样点列表后再保存");
                if (previous.Length == 1) colors = colors with { Samples = previous[0].Colors?.Samples };
            }
            if (colors.ValidateSamples() is { } error)
                return RgbWriteResult.Reject(error);
            if (colors.Samples is not null) colors = colors with { Samples = colors.Samples.ToArray() };
        }
        var calibrations = stored.Where(item => item?.DeviceKey != device.DeviceKey || item.ZoneIndex != zoneIndex).ToList();
        if (colors is not null)
            calibrations.Add(new(device.DeviceKey, zoneIndex, zone.Name, colors));
        Save(_settings with { ZoneCalibrations = calibrations.Count == 0 ? null : calibrations });
        if (IsBoard(device)) ForgetBoardMode();
        _paused.Remove((device.DeviceKey, zoneIndex));
        ForgetFrames(device.Address);
        return RgbWriteResult.Saved;
    }

    private RgbColorCalibration? FindCalibration(RgbDeviceInfo device, RgbZoneInfo zone) =>
        _calibrations.GetValueOrDefault((device.DeviceKey, zone.Index, zone.Name));

    private void IndexCalibrations(IReadOnlyList<RgbZoneCalibration>? stored)
    {
        _calibrations.Clear();
        _calibrationError = null;
        if (stored is null) return;
        if (stored.Any(item => item is null)) _calibrationError = "存在空的分区颜色校正记录";
        foreach (var group in stored.Where(item => item is not null).GroupBy(item => (item.DeviceKey, item.ZoneIndex, item.ZoneName)))
        {
            var colors = group.First().Colors;
            if (group.Skip(1).Any() || colors is null)
            {
                _calibrationError ??= $"{group.Key.ZoneName} 的分区颜色校正记录缺失或重复";
                continue;
            }
            if (colors.ValidateSamples() is { } error)
            {
                _calibrationError ??= $"{group.Key.ZoneName} 额外校色样点未加载，正在使用原有校正：{error}";
                colors = colors with { Samples = null };
            }
            _calibrations[group.Key] = colors;
        }
    }

    private RgbColor CorrectLed(RgbDeviceInfo device, int led, RgbColor color)
    {
        if (_settings.ZoneCalibrations is not { Count: > 0 } || led < 0 ||
            device.Zones.Sum(zone => zone.LedCount) != device.LedCount) return color;
        foreach (var zone in device.Zones)
        {
            if (led < zone.LedCount)
                return FindCalibration(device, zone)?.Correct(color) ?? color;
            led -= zone.LedCount;
        }
        return color;
    }

    private IReadOnlyList<RgbColor> CorrectFrame(RgbDeviceInfo device, int? zoneIndex,
        IReadOnlyList<RgbColor> colors, IReadOnlyList<int>? painted = null)
    {
        if (_settings.ZoneCalibrations is not { Count: > 0 } || colors.Count == 0)
            return colors;
        if (zoneIndex is int index)
        {
            var zone = device.Zones.ElementAtOrDefault(index);
            return zone is not null && FindCalibration(device, zone) is { } calibration
                ? colors.Select(calibration.Correct).ToArray() : colors;
        }
        if (device.Zones.Sum(zone => zone.LedCount) != device.LedCount)
            return colors;
        RgbColor[]? frame = null;
        var offset = 0;
        foreach (var zone in device.Zones)
        {
            if (FindCalibration(device, zone) is { } calibration)
            {
                frame ??= Enumerable.Range(0, device.LedCount)
                    .Select(led => colors[Math.Min(led, colors.Count - 1)]).ToArray();
                for (var led = offset; led < offset + zone.LedCount; led++)
                    if (painted is null || painted.Contains(led))
                        frame[led] = calibration.Correct(frame[led]);
            }
            offset += zone.LedCount;
        }
        return frame ?? colors;
    }

    private async Task<RgbWriteResult> ApplyGroupEffectAsync(RgbEffectSettings? effect, string? profileId, CancellationToken token)
    {
        if (RgbEffects.NormalizeGroupEffect(effect) is not { } normalized)
            return RgbWriteResult.Reject("灯效参数无效，请检查控件中的设置");
        Save(_settings with
        {
            Enabled = true,
            GroupEffect = normalized,
            Targets = null,
            ActiveProfileId = profileId ?? _settings.ActiveProfileId,
            ExternalControlPreferences = DiscoverGroupInputs() ?? _settings.ExternalControlPreferences
        });
        ResetPlayback();
        if (_board is not null && _provider.IsAvailable && CanUseBoardEffect(normalized))
        {
            await RenderAsync(token).ConfigureAwait(false);
            if (_matchError is not null)
                return RgbWriteResult.Reject($"配置已保存，部分设备尚未确认。SEEIN 将重试。{_matchError}");
        }
        return RgbWriteResult.Saved;
    }

    private IReadOnlyDictionary<string, bool>? DiscoverGroupInputs()
    {
        var known = _settings.ExternalControlPreferences;
        Dictionary<string, bool>? discovered = null;
        foreach (var device in _status.Devices.Select(entry => entry.Info))
        {
            if (string.IsNullOrWhiteSpace(device.DeviceKey) || known?.ContainsKey(device.DeviceKey) == true ||
                !RgbEffects.IsExternalInput(device.ActiveMode) || RgbEffects.ExternalInput(device) is null ||
                UniqueDevice(entry => entry.Info.DeviceKey == device.DeviceKey) is null) continue;
            discovered ??= known is null ? [] : new(known);
            discovered.Add(device.DeviceKey, true);
        }
        return discovered;
    }

    private IReadOnlyDictionary<string, bool> WithExternalInput(string deviceKey, bool external)
    {
        var preferences = new Dictionary<string, bool>(_settings.ExternalControlPreferences ?? new Dictionary<string, bool>())
        {
            [deviceKey] = external
        };
        return preferences;
    }

    private void RememberPreviewRoute(RgbDeviceInfo beforePreview, string? selectedMode = null)
    {
        if (RgbEffects.ExternalInput(beforePreview) is null || string.IsNullOrWhiteSpace(beforePreview.DeviceKey) ||
            UniqueDevice(entry => entry.Info.DeviceKey == beforePreview.DeviceKey) is null) return;
        var preferences = _settings.ExternalControlPreferences;
        if (preferences?.GetValueOrDefault(beforePreview.DeviceKey) == true) return;
        // A native preview can temporarily replace an external input discovered before the first group apply.
        if (RgbEffects.IsExternalInput(selectedMode) || RgbEffects.PrefersExternalInput(beforePreview, _settings))
            Save(_settings with { ExternalControlPreferences = WithExternalInput(beforePreview.DeviceKey, true) });
    }

    private RgbWriteResult SaveGroupProfile(string? name, RgbEffectSettings? effect)
    {
        name = name?.Trim();
        if (name is not { Length: > 0 and <= 64 })
            return RgbWriteResult.Reject("请输入 1 到 64 个字符的配置名称");
        if (RgbEffects.NormalizeGroupEffect(effect) is not { } normalized)
            return RgbWriteResult.Reject("灯效参数无效");
        var profiles = _settings.Profiles ?? [];
        if (profiles.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            return RgbWriteResult.Reject("此配置名称已存在，请换一个名称");
        var profile = new RgbProfile("group-" + Guid.NewGuid().ToString("N"), name, null, normalized, WholeSystem: true);
        Save(_settings with { Profiles = profiles.Append(profile).ToArray() });
        return RgbWriteResult.Saved;
    }

    private RgbWriteResult SetEffect(RgbCommandRequest request)
    {
        if (!RgbEffects.All.Any(effect => effect.Id == request.EffectId))
            return RgbWriteResult.Reject("未知软件灯效");
        var requested = request.GroupEffect ?? new RgbEffectSettings(request.EffectId!, request.Colors ?? [],
            request.Speed ?? 50, request.Brightness ?? 100, request.Direction ?? "forward",
            UseRainbowPalette: request.UseRainbowPalette);
        if (requested.EffectId != request.EffectId || RgbEffects.NormalizeGroupEffect(requested) is not { } effectSettings)
            return RgbWriteResult.Reject("灯效参数无效，请检查所选设备的设置");
        RgbDeviceState[] selected = request.DeviceAddress is null && request.DeviceName is null
            ? _status.Devices.Where(device => device.Info.SupportsDirect && device.Info.LedCount > 0).ToArray()
            : FindDevice(request) is { } target ? [target] : [];
        if (selected.Length == 0 || selected.Any(device => !device.Info.SupportsDirect || device.Info.LedCount == 0 ||
                _status.Devices.Count(other => other.Info.DeviceKey == device.Info.DeviceKey) != 1))
            return RgbWriteResult.Reject("请选择可唯一识别且支持直接控制的设备");

        if (request.ZoneIndex is int index && (selected.Length != 1 ||
                selected[0].Info.Zones.ElementAtOrDefault(index) is not { LedCount: > 0 }))
            return RgbWriteResult.Reject("所选分区没有可用灯珠");

        var targets = EffectiveTargets().ToList();
        foreach (var device in selected)
        {
            var info = device.Info;
            var previous = targets.FirstOrDefault(target => target.DeviceKey == info.DeviceKey && target.ZoneIndex is null);
            targets.RemoveAll(target => target.DeviceKey == info.DeviceKey &&
                (request.ZoneIndex is null || target.ZoneIndex is null || target.ZoneIndex == request.ZoneIndex));
            if (request.ZoneIndex is int zone && previous is { HardwareMode: null } && previous.TopologyKey == info.TopologyKey)
            {
                targets.AddRange(info.Zones.Where(item => item.Index != zone && item.LedCount > 0)
                    .Select(item => previous with { ZoneIndex = item.Index }));
            }
            targets.Add(new RgbEffectTarget(info.DeviceKey, info.TopologyKey, request.ZoneIndex, effectSettings));
            Resume(info);
        }
        Save(_settings with { Enabled = true, Targets = targets.ToArray(), SchemaVersion = 2 });
        return RgbWriteResult.Saved;
    }

    private async Task<RgbWriteResult> LoadProfileAsync(string? id, CancellationToken token)
    {
        var profile = _settings.Profiles?.FirstOrDefault(entry => entry.Id == id);
        if (profile is null) return RgbWriteResult.Reject("找不到灯效配置");
        if (profile.WholeSystem) return await ApplyGroupEffectAsync(profile.Effect, profile.Id, token).ConfigureAwait(false);
        if (profile.Targets is not null || (profile.HardwareMode is null && profile.LedColors is null))
        {
            ResetPlayback();
            Save(_settings with { ActiveProfileId = profile.Id, Targets = profile.Targets, GroupEffect = null });
            return RgbWriteResult.Saved;
        }

        var device = MatchProfile(profile);
        if (device is null) return RgbWriteResult.Reject("配置的设备缺失、匹配有歧义或接线有变化");
        var mode = device.Info.Modes.FirstOrDefault(item => item.Name == profile.HardwareMode);
        RgbWriteResult result;
        if (mode is { IsDirect: false })
            result = await SetDeviceModeAsync(device.Info, mode, null, null, token).ConfigureAwait(false);
        else if (profile.LedColors is { Count: > 0 } colors)
            result = await WithDirectAsync(device.Info, () => _provider.ApplyAsync(device.Info.Address, CorrectFrame(device.Info, null, colors), token)).ConfigureAwait(false);
        else if (mode is not null)
            result = await SetDeviceModeAsync(device.Info, mode, null, null, token).ConfigureAwait(false);
        else
            return RgbWriteResult.Reject("设备缺少配置中的模式或颜色");
        if (!result.Success) return result;

        var migrated = profile with { DeviceKey = device.Info.DeviceKey, TopologyKey = device.Info.TopologyKey };
        _paused.Add((device.Info.DeviceKey, null));
        ForgetFrames(device.Info.Address);
        Save(_settings with
        {
            ActiveProfileId = profile.Id, SchemaVersion = 2, GroupEffect = null,
            Profiles = _settings.Profiles!.Select(entry => entry.Id == profile.Id ? migrated : entry).ToArray()
        });
        await RefreshCoreAsync(false, token).ConfigureAwait(false);
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long nextGradientFrame = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var frameStart = Stopwatch.GetTimestamp();
            await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _initialized))
                {
                    if (_provider.NeedsRefresh || _nextRefresh <= DateTimeOffset.UtcNow)
                        await RefreshCoreAsync(false, stoppingToken).ConfigureAwait(false);
                    if (_settings.Enabled && _provider.IsAvailable)
                        await RenderAsync(stoppingToken).ConfigureAwait(false);
                    else { _matchError = null; ReleaseAudio(); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "RGB service update failed");
                _status = new(RgbProviderState.Error, "灯效服务更新失败", [], LastError: ex.Message);
            }
            finally { _gate.Release(); }
            var interval = TimeSpan.FromSeconds(1);
            if (_settings.Enabled && _provider.IsAvailable && _status.Devices.Count > 0)
            {
                if ((_uniformGroup?.EffectId == RgbEffects.ColorCycle || _ringActive) && _boardResult is { Success: true })
                {
                    // Pace from frame starts so HID writes and readbacks don't extend every frame.
                    var period = Stopwatch.Frequency / _settings.Fps;
                    var now = Stopwatch.GetTimestamp();
                    nextGradientFrame = (nextGradientFrame == 0 ? frameStart : nextGradientFrame) + period;
                    if (nextGradientFrame <= now)
                        nextGradientFrame += ((now - nextGradientFrame) / period + 1) * period;
                    await Task.Delay(Stopwatch.GetElapsedTime(now, nextGradientFrame), stoppingToken).ConfigureAwait(false);
                    continue;
                }
                interval = _firmwareOnly
                    ? TimeSpan.FromMilliseconds(_uniformGroup is not null ? 125 : _audio is null ? 500 : 200)
                    : TimeSpan.FromSeconds(1d / _settings.Fps);
            }
            nextGradientFrame = 0;
            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RenderAsync(CancellationToken token)
    {
        _firmwareOnly = false;
        _ringActive = false;
        var profile = ActiveProfile();
        var legacyProfile = _settings.Targets is null && _settings.GroupEffect is null;
        if (legacyProfile && (profile.HardwareMode is not null || profile.LedColors is not null))
        {
            ReleaseAudio();
            var device = MatchProfile(profile);
            if (device is null) { _matchError = "默认配置的设备缺失或匹配有歧义"; return; }
            if (!_paused.Contains((device.Info.DeviceKey, null)))
            {
                var result = await LoadProfileAsync(profile.Id, token).ConfigureAwait(false);
                _matchError = result.Error;
            }
            return;
        }

        _matchError = null;
        if (legacyProfile && profile.DeviceName is not null && MatchProfile(profile) is null)
        {
            _matchError = "默认配置的设备缺失或匹配有歧义";
            return;
        }

        var targets = EffectiveTargets();
        var group = _settings.Targets is null ? _settings.GroupEffect ?? (RgbEffects.IsGroupProfile(profile) ? profile.Effect : null) : null;
        var nativeBoard = _board is not null && group is not null && CanUseBoardEffect(group) &&
            !_paused.Any(item => item.Device == _board.DeviceKey);
        if (!nativeBoard) _groupPlayback = null;
        var nativeMusic = nativeBoard && group!.EffectId == RgbEffects.Music;
        var softwareMusic = targets.Any(t => t.HardwareMode is null && t.Effect.EffectId == RgbEffects.Music &&
            (!nativeBoard || t.DeviceKey != _board!.DeviceKey) && !_paused.Contains((t.DeviceKey, null)) &&
            !_paused.Contains((t.DeviceKey, t.ZoneIndex)));
        var nextAudioFrame = ReadAudio(nativeMusic || softwareMusic);
        _firmwareOnly = nativeBoard && targets.All(t => t.HardwareMode is not null || t.DeviceKey == _board!.DeviceKey);
        var time = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        var temperature = group?.EffectId == RgbEffects.Temperature || targets.Any(t => t.Effect.EffectId == RgbEffects.Temperature)
            ? _state.Snapshot().Sensors.Get(SensorKeys.CpuTemperature) : null;
        foreach (var target in targets)
        {
            var device = UniqueDevice(entry => entry.Info.DeviceKey == target.DeviceKey);
            if (device is null || device.Info.TopologyKey != target.TopologyKey)
            {
                _matchError = "部分灯效目标离线、匹配有歧义或分区已变化，请重新应用场景";
                continue;
            }
            if (_paused.Contains((target.DeviceKey, null)) || _paused.Contains((target.DeviceKey, target.ZoneIndex))) continue;
            if (nativeBoard && IsBoard(device.Info)) continue;
            if (target.HardwareMode is { } name)
            {
                var key = (device.Info.Address, (int?)null);
                if (_lastFrames.ContainsKey(key)) continue;
                // SDK readback can still say external after another controller changed the strip.
                // A fresh playback session must send the input selection to the controller itself.
                var mode = device.Info.Modes.FirstOrDefault(entry => entry.Name == name);
                if (mode is null) { _matchError = $"设备 {device.Info.Name} 缺少模式 {name}"; continue; }
                var color = target.Effect.Colors.Count > 0 ? target.Effect.Colors[0] : (RgbColor?)null;
                var result = await SetDeviceModeAsync(device.Info, mode, target.HardwareSpeed,
                    target.HardwareBrightness, token, color).ConfigureAwait(false);
                if (result.Success) _lastFrames[key] = 0;
                else _matchError = result.Error;
                continue;
            }

            if (target.ZoneIndex is null && _paused.Any(item => item.Device == target.DeviceKey))
            {
                foreach (var zone in device.Info.Zones)
                    if (!_paused.Contains((target.DeviceKey, zone.Index)))
                        await RenderFrameAsync(device, target with { ZoneIndex = zone.Index }, time, temperature, token).ConfigureAwait(false);
            }
            else
                await RenderFrameAsync(device, target, time, temperature, token).ConfigureAwait(false);
        }
        if (nativeBoard)
        {
            var result = UsesUniformFrames(group!) ? RenderUniformBoard(group!, time, temperature) : SelectBoardMode(group);
            if(result.Success&&RingZone(_board!,group) is int ring)
            {
                var now=DateTimeOffset.UtcNow;var verify=now>=_nextRingVerification;
                result=_msi!.UpdateRing(_board!,ring,RgbRingWaveRenderer.Render(group!,68,time),verify);
                if(result.Success)
                {
                    _ringActive=true;_firmwareOnly=false;
                    if(verify)_nextRingVerification=now.AddSeconds(2);
                }
                else
                {
                    // Do not fall through to SDK-wide Direct or flood the controller on failure.
                    _boardResult=result;_retryBoardAfter=now.AddSeconds(2);_groupPlayback=null;
                }
            }
            if (result.Success && nativeMusic && nextAudioFrame)
            {
                var levels = _levels.GetValueOrDefault();
                result = _msi!.UpdateAudio(Percent(levels.Master), Percent(levels.Left), Percent(levels.Right));
            }
            if (!result.Success) _matchError = result.Error;
        }
    }

    private static byte Percent(double level) => (byte)Math.Round(Math.Clamp(level, 0, 1) * 100);

    private bool ReadAudio(bool needed)
    {
        if (!needed) { ReleaseAudio(); return false; }
        var now = DateTimeOffset.UtcNow;
        if (now < _nextAudio) return false;
        _nextAudio = now.AddMilliseconds(200);
        _levels = (_audio ??= new PlaybackPeakReader()).ReadLevels();
        return true;
    }

    private void ReleaseAudio()
    {
        _audio?.Dispose();
        _audio = null;
        _levels = null;
        _nextAudio = default;
    }

    private bool IsBoard(RgbDeviceInfo device) => _board is not null && _board.Address == device.Address;

    private static int? RingZone(RgbDeviceInfo device,RgbEffectSettings? effect)
    {
        var zone=device.Zones.FirstOrDefault(z=>z.Name=="JARGB 3");
        return effect?.EffectId==RgbEffects.Wave&&zone is not null&&MsiMysticLightBoard.IsRingZone(device.Zones,zone.Index)?zone.Index:null;
    }

    private bool CanUseBoardEffect(RgbEffectSettings effect) =>
        (MsiMysticLightBoard.SupportsEffect(effect.EffectId) || UsesUniformFrames(effect)) &&
        (!effect.UseZoneCalibration || !HasAdditionalBoardCalibrations());

    private static bool UsesUniformFrames(RgbEffectSettings effect) =>
        effect.EffectId is RgbEffects.Temperature or RgbEffects.ColorCycle ||
        effect.EffectId == RgbEffects.Breathing && effect.Colors is { Count: > 1 } colors && colors.Any(c => c != colors[0]);

    // The GPU header follows SELECT ALL. Other headers use the original palette when GPU correction is active.
    // Additional user calibrations on those headers require the per-zone renderer.
    private bool HasAdditionalBoardCalibrations() => _board is { } board &&
        _calibrations.Keys.Any(c => c.Device == board.DeviceKey && c.Name != "JARGB 2" &&
            board.Zones.Any(z => z.Index == c.Zone && z.Name == c.Name));

    private RgbWriteResult RenderUniformBoard(RgbEffectSettings group, double time, double? temperature)
    {
        var now = DateTimeOffset.UtcNow;
        var sameGroup = ReferenceEquals(_uniformGroup, group);
        if (sameGroup && _boardResult is { } last &&
            (now < _nextBoardFrame || (!last.Success && now < _retryBoardAfter))) return last;
        _uniformGroup = group;
        // ColorCycle is paced by the render loop. Temperature and breathing use the slower board cadence.
        _nextBoardFrame = group.EffectId == RgbEffects.ColorCycle ? default : now.AddMilliseconds(125);
        var color = RgbEffectRenderer.Render(group, 1, time, temperature)[0];
        if (sameGroup && _boardResult is { Success: true } previous && color == _uniformColor) return previous;
        _uniformColor = color;
        var frame = new RgbEffectSettings(RgbEffects.Static, [color], UseZoneCalibration: group.UseZoneCalibration);
        return SelectBoardMode(frame, group.EffectId);
    }

    private RgbWriteResult SelectBoardMode(RgbEffectSettings? effect, string? uniformEffectId = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (uniformEffectId is null) _uniformGroup = null;
        if (_boardResult is { } previous && ReferenceEquals(_boardEffect, effect) && (previous.Success || now < _retryBoardAfter))
            return previous;
        var board = _board!;
        // Legacy built-in presets can contain one color for a four-slot mode.
        var normalized = RgbEffects.NormalizeGroupEffect(effect);
        if (effect is not null && normalized is null) return RgbWriteResult.Reject("主板灯效参数无效");
        var gpuZone = board.Zones.FirstOrDefault(z => z.Name == "JARGB 2");
        IReadOnlyList<RgbColor>? palette = null;
        if (normalized is { UseZoneCalibration: true, UseRainbowPalette: false, Colors.Count: > 0 } &&
            gpuZone is not null && FindCalibration(board, gpuZone) is { } calibration)
        {
            var corrected = normalized.Colors.Select(calibration.Correct).ToArray();
            if (!corrected.SequenceEqual(normalized.Colors)) palette = corrected;
        }
        _boardEffect = effect;
        _boardResult = null;
        _groupPlayback = null;
        ForgetFrames(board.Address);
        var ring=RingZone(board,normalized);
        var result = normalized is null ? _msi!.EnterDirect(board) : _msi!.Apply(board, normalized, palette,ring);
        _nextRingVerification=default;
        _boardResult = result;
        _retryBoardAfter = now.AddSeconds(2);
        if (effect is not null && result.Success)
        {
            var playback = ring is not null ? "水波 · 冷排连续流光" : uniformEffectId is not null ? $"{RgbEffects.NameOf(uniformEffectId)} · 主板逐帧更新" :
                effect.EffectId == RgbEffects.BoardDefault ? RgbEffects.NameOf(effect.EffectId) : $"原生{RgbEffects.NameOf(effect.EffectId)}";
            var route = palette is null ? "显卡与机箱跟随整机" : "显卡跟随校正色，机箱使用原色";
            _groupPlayback = $"{playback} · {route}";
        }
        return result;
    }

    private Task<RgbWriteResult> WithDirectAsync(RgbDeviceInfo info, Func<Task<RgbWriteResult>> write) =>
        IsBoard(info) && SelectBoardMode(null) is { Success: false } failure ? Task.FromResult(failure) : write();

    private Task<RgbWriteResult> SetDeviceModeAsync(RgbDeviceInfo info, RgbMode mode, int? speed, int? brightness,
        CancellationToken token, RgbColor? color = null)
    {
        if (IsBoard(info))
        {
            if (mode.IsDirect && SelectBoardMode(null) is { Success: false } failure) return Task.FromResult(failure);
            if (!mode.IsDirect) ForgetBoardMode();
        }
        return _provider.SetModeAsync(info.Address, mode.Index, speed, brightness, token, color);
    }

    private void ForgetBoardMode()
    {
        _uniformGroup = null;
        _uniformColor = null;
        _nextBoardFrame = default;
        _boardResult = null;
        _boardEffect = null;
        _groupPlayback = null;
        _retryBoardAfter = default;
        _ringActive=false;_nextRingVerification=default;
    }

    private IReadOnlyList<RgbEffectTarget> EffectiveTargets()
    {
        if (_settings.Targets is { } targets) return targets;
        if (_settings.GroupEffect is { } group)
            return BuildGroupTargets(_status.Devices.Select(device => device.Info), _settings, group);
        var profile = ActiveProfile();
        if (RgbEffects.IsGroupProfile(profile))
            return BuildGroupTargets(_status.Devices.Select(device => device.Info), _settings, profile.Effect);
        if (profile.HardwareMode is not null || profile.LedColors is not null) return [];
        var fallback = profile.DeviceName is not null ? MatchProfile(profile) : null;
        return _status.Devices.Where(device => device.Info.SupportsDirect && device.Info.LedCount > 0 &&
                (profile.DeviceName is null || device.Info.Address == fallback?.Info.Address))
            .Select(device => new RgbEffectTarget(device.Info.DeviceKey, device.Info.TopologyKey, null, profile.Effect)).ToArray();
    }

    internal static IReadOnlyList<RgbEffectTarget> BuildGroupTargets(IEnumerable<RgbDeviceInfo> devices, RgbSettings settings, RgbEffectSettings group)
    {
        var unique = devices.GroupBy(device => device.DeviceKey).Where(matches => matches.Count() == 1)
            .Select(matches => matches.First()).ToArray();
        var external = unique.Any(info => RgbEffects.RequiresGroupExternalInput(info) ||
            RgbEffects.GroupExternalInput(info, settings) is not null);
        var solid = new RgbEffectSettings(RgbEffects.Static, RgbEffectRenderer.Render(group, 1, 0));
        var targets = new List<RgbEffectTarget>();
        foreach (var info in unique)
        {
            if (RgbEffects.GroupExternalInput(info, settings) is { } input)
            {
                targets.Add(new(info.DeviceKey, info.TopologyKey, null, solid with { Colors = [] }, input.Name));
            }
            else if (RgbEffects.RequiresGroupExternalInput(info))
                continue;
            else if (info.SupportsDirect && info.LedCount > 0)
            {
                var zones = info.Zones.Where(zone => zone.LedCount > 0).ToArray();
                if (external && info.Type == "主板" && zones.Length > 1 && zones.Sum(zone => zone.LedCount) == info.LedCount &&
                    zones.All(zone => zone.Type == RgbZoneType.Linear))
                    targets.AddRange(zones.Select(zone => new RgbEffectTarget(info.DeviceKey, info.TopologyKey, zone.Index, group)));
                else
                    targets.Add(new(info.DeviceKey, info.TopologyKey, null, group));
            }
            else if (RgbEffects.NativeAnimation(info, group.EffectId) is { } native)
            {
                int? speed = native.HasSpeed
                    ? (int)Math.Round(native.SpeedMin + ((double)native.SpeedMax - native.SpeedMin) * group.Speed / 100)
                    : null;
                targets.Add(new(info.DeviceKey, info.TopologyKey, null, solid with { Colors = [] }, native.Name, speed));
            }
            else if (info.SolidColorMode is { } mode)
            {
                targets.Add(new(info.DeviceKey, info.TopologyKey, null, solid, mode.Name));
            }
        }
        return targets;
    }

    private async Task RenderFrameAsync(RgbDeviceState device, RgbEffectTarget target, double time, double? temperature,
        CancellationToken token)
    {
        var count = target.ZoneIndex is int zone ? device.Info.Zones.ElementAtOrDefault(zone)?.LedCount ?? 0 : device.Info.LedCount;
        if (count == 0) return;
        if (IsBoard(device.Info) && SelectBoardMode(null) is { Success: false } failure)
        {
            _matchError = failure.Error;
            return;
        }
        var key = (device.Info.Address, target.ZoneIndex);
        var waveAccent = target.Effect.UseZoneCalibration && target.Effect.EffectId == RgbEffects.Wave && target.ZoneIndex is int accentIndex &&
            device.Info.Zones.ElementAtOrDefault(accentIndex) is { } accentZone
                ? FindCalibration(device.Info, accentZone)?.InteriorPoint : null;
        var frame = IsBoard(device.Info)&&RingZone(device.Info,target.Effect)==target.ZoneIndex&&target.ZoneIndex is not null
            ? RgbRingWaveRenderer.Render(target.Effect,count,time)
            : RgbEffectRenderer.Render(target.Effect, count, time, temperature, waveAccent: waveAccent, audioLevel: _levels?.Master);
        if (target.Effect.UseZoneCalibration) frame = CorrectFrame(device.Info, target.ZoneIndex, frame);
        var signature = FrameSignature(frame);
        if (_lastFrames.TryGetValue(key, out var last) && last == signature) return;
        var result = target.ZoneIndex is int index
            ? await _provider.ApplyZoneAsync(key.Address, index, frame, token).ConfigureAwait(false)
            : await _provider.ApplyAsync(key.Address, frame, token).ConfigureAwait(false);
        if (result.Success) _lastFrames[key] = signature;
        else _matchError = result.Error;
    }

    private void Resume(RgbDeviceInfo device)
    {
        _paused.RemoveWhere(item => item.Device == device.DeviceKey);
        ForgetFrames(device.Address);
    }

    private RgbDeviceState? FindDevice(RgbCommandRequest request) => request.DeviceAddress is { } address
        ? _status.Devices.FirstOrDefault(entry => entry.Info.Address == address)
        : UniqueDevice(entry => string.Equals(entry.Info.Name, request.DeviceName, StringComparison.OrdinalIgnoreCase));

    private RgbDeviceState? MatchProfile(RgbProfile profile)
    {
        var device = profile.DeviceKey is { } key
            ? UniqueDevice(entry => entry.Info.DeviceKey == key)
            : UniqueDevice(entry => string.Equals(entry.Info.Name, profile.DeviceName, StringComparison.OrdinalIgnoreCase));
        return profile.TopologyKey is null || profile.TopologyKey == device?.Info.TopologyKey ? device : null;
    }

    private RgbDeviceState? UniqueDevice(Func<RgbDeviceState, bool> predicate)
    {
        var candidates = _status.Devices.Where(predicate).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static int FrameSignature(IReadOnlyList<RgbColor> frame)
    {
        var hash = new HashCode();
        foreach (var color in frame) hash.Add(color);
        return hash.ToHashCode();
    }

    private void ForgetFrames(string address)
    {
        foreach (var key in _lastFrames.Keys.Where(key => key.Address == address).ToArray())
            _lastFrames.Remove(key);
    }

    private void ResetPlayback()
    {
        _paused.Clear();
        _lastFrames.Clear();
        _matchError = null;
        ForgetBoardMode();
        ReleaseAudio();
    }

    private RgbProfile ActiveProfile() => _settings.Profiles?.FirstOrDefault(profile => profile.Id == _settings.ActiveProfileId)
        ?? RgbEffects.DefaultSettings().Profiles![0];

    private void Save(RgbSettings settings)
    {
        SettingsChanged?.Invoke(settings);
        if (!ReferenceEquals(_settings.ZoneCalibrations, settings.ZoneCalibrations))
            IndexCalibrations(settings.ZoneCalibrations);
        _settings = settings;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        ReleaseAudio();
        await _provider.DisposeAsync().ConfigureAwait(false);
        _engine.Stop();
    }
}
