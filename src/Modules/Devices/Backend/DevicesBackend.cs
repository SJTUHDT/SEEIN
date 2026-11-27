using Seein.Core;
using Seein.Core.Hosting;
using Seein.Core.Ipc;
using Seein.Core.Storage;
using Seein.Core.Lighting;
using Seein.Core.Thermal;
using Seein.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Seein.Service;
namespace Seein.Modules;
public sealed class DevicesBackend : BackendModule
{
    private ModuleMessageBus _pipe=null!;
    private PanelSettings _settings=>Settings.Current;
    private readonly object _settingsGate=new();
    private ILogger<DevicesBackend> logger=null!;
    private FanControlService fanControl=null!;
    private GpuFanService gpuFan=null!;
    private RgbService rgb=null!;
    protected override void Configure(IServiceCollection services)
    {
        services.AddSingleton<FanControlService>();services.AddSingleton<GpuFanService>();
        services.AddSingleton<RgbService>();services.AddSingleton<RgbEngineHost>();
        services.AddHostedService(p=>p.GetRequiredService<FanControlService>());
        services.AddHostedService(p=>p.GetRequiredService<GpuFanService>());
        services.AddHostedService(p=>p.GetRequiredService<RgbService>());
    }
    protected override async Task InitializeAsync(CancellationToken token)
    {
        logger=Context.Required<ILoggerFactory>().CreateLogger<DevicesBackend>();
        _pipe=new(Context.Required<IModuleMessageBus>());
        fanControl=Required<FanControlService>();gpuFan=Required<GpuFanService>();rgb=Required<RgbService>();
        fanControl.Attach(Context.Required<SharedHardware>().Sensors!);fanControl.Initialize(_settings.Fans);
        rgb.SettingsChanged+=value=>UpdateSettings(current=>current with {Rgb=value});rgb.Initialize(_settings.Rgb);
        await Task.CompletedTask;
    }
    public override bool CanHandle(string type)=>new[]{"fanStateRequest","fanConfigRequest","fanRestoreRequest","rgbDevicesRequest","rgbCommandRequest"}.Contains(type,StringComparer.Ordinal);
    public override async Task HandleAsync(string messageType,JsonElement payload,CancellationToken token)
    {
        var envelope=new PipeEnvelope(messageType,payload);
        switch(envelope.Type)
        {
            case PipeMessageType.FanStateRequest:
                if (_pipe is null) return;
                await _pipe.BroadcastAsync(PipeMessageType.FanStateResult,
                    new FanStateResult(GetFanStatus(), fanControl.Settings, envelope.Read<FanStateRequest>()?.RequestId)).ConfigureAwait(false);
                break;
            case PipeMessageType.FanConfigRequest:
            {
                var configRequest = envelope.Read<FanConfigRequest>();
                if (configRequest is null || _pipe is null) return;
                try
                {
                    string? commandError = null;
                    switch (configRequest.Command)
                    {
                        case "stopMsiServices":
                            fanControl.TrySetMsiFanServices(true, out commandError);
                            break;
                        case "startMsiServices":
                            fanControl.TrySetMsiFanServices(false, out commandError);
                            break;
                        case "applyGpu" when configRequest.Gpu is { } command:
                            commandError = await gpuFan.ApplyAsync(command).ConfigureAwait(false);
                            break;
                        case null:
                            var settings = FanDefaults.Normalize(configRequest.Settings);
                            lock (_settingsGate)
                            {
                                UpdateSettings(current => current with { Fans = settings });
                                fanControl.ApplySettings(settings);
                                commandError = fanControl.RestoreError;
                            }
                            break;
                        default:
                            commandError = "不支持的风扇命令。";
                            break;
                    }
                    await _pipe.BroadcastAsync(PipeMessageType.FanConfigResult,
                        new FanConfigResult(commandError is null, GetFanStatus(), commandError, configRequest.RequestId, fanControl.Settings)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Fan configuration failed");
                    await _pipe.BroadcastAsync(PipeMessageType.FanConfigResult,
                        new FanConfigResult(false, GetFanStatus(), ex.Message, configRequest.RequestId, fanControl.Settings)).ConfigureAwait(false);
                }
                break;
            }
            case PipeMessageType.FanRestoreRequest:
            {
                var restoreRequest = envelope.Read<FanRestoreRequest>();
                if (_pipe is null) return;
                string? saveError = null;
                string? boardError;
                lock (_settingsGate)
                {
                    var restoredSettings = fanControl.Settings with { Enabled = false };
                    // Hand back physical control even if settings cannot be persisted.
                    fanControl.ApplySettings(restoredSettings);
                    boardError = fanControl.RestoreError;
                    try { UpdateSettings(current => current with { Fans = restoredSettings }); }
                    catch (Exception ex) { saveError = $"交还状态保存失败：{ex.Message}"; }
                }
                var gpuError = await gpuFan.RestoreAsync().ConfigureAwait(false);
                var errors = new[] { boardError, saveError, gpuError }.OfType<string>().ToArray();
                await _pipe.BroadcastAsync(PipeMessageType.FanRestoreResult,
                    new FanRestoreResult(errors.Length == 0, GetFanStatus(),
                        errors.Length == 0 ? null : string.Join(" · ", errors),
                        restoreRequest?.RequestId, fanControl.Settings)).ConfigureAwait(false);
                break;
            }
            case PipeMessageType.RgbDevicesRequest:
                if (_pipe is null) return;
                await _pipe.BroadcastAsync(PipeMessageType.RgbDevicesResult,
                    new RgbDevicesResult(await rgb.RefreshAsync(CancellationToken.None).ConfigureAwait(false), rgb.Settings,
                        envelope.Read<RgbDevicesRequest>()?.RequestId)).ConfigureAwait(false);
                break;
            case PipeMessageType.RgbCommandRequest:
            {
                if (_pipe is null) return;
                RgbCommandResult result;
                try
                {
                    var rgbRequest = envelope.Read<RgbCommandRequest>();
                    result = rgbRequest is null
                        ? new(false, Error: "缺少灯效命令")
                        : await rgb.HandleCommandAsync(rgbRequest, CancellationToken.None).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    var id = envelope.Payload.ValueKind == JsonValueKind.Object &&
                        envelope.Payload.TryGetProperty("requestId", out var requestId) && requestId.ValueKind == JsonValueKind.String
                        ? requestId.GetString() : null;
                    result = new(false, Error: $"灯效参数格式无效（{ex.Path ?? "$"}）", RequestId: id);
                }
                await _pipe.BroadcastAsync(PipeMessageType.RgbCommandResult, result).ConfigureAwait(false);
                break;
            }
        }
    }
    private void UpdateSettings(Func<PanelSettings,PanelSettings> update)=>Settings.Update(update);
    private FanStatus GetFanStatus() => fanControl.GetStatus() with { Gpu = gpuFan.Status };
}
