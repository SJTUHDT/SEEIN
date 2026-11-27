using Seein.Core.Lighting;using Seein.Core;using Seein.Core.Hosting;using Seein.Core.Ipc;using Seein.Core.Storage;using Microsoft.Extensions.DependencyInjection;using Microsoft.Extensions.Logging;using System.Text.Json;using Seein.Service;namespace Seein.Modules;
public sealed class FeatureBackend:BackendModule {
private ModuleMessageBus _pipe=null!;private PanelSettings _settings=>Settings.Current;private readonly object _settingsGate=new();
private void UpdateSettings(Func<PanelSettings,PanelSettings> update)=>Settings.Update(update);

private RgbService rgb=null!;
protected override void Configure(IServiceCollection services){services.AddSingleton<RgbService>();services.AddSingleton<RgbEngineHost>();services.AddHostedService(p=>p.GetRequiredService<RgbService>());}
protected override Task InitializeAsync(CancellationToken token){_pipe=new(Context.Required<IModuleMessageBus>());rgb=Required<RgbService>();rgb.SettingsChanged+=value=>UpdateSettings(current=>current with{Rgb=value});rgb.Initialize(_settings.Rgb);return Task.CompletedTask;}
public override bool CanHandle(string type)=>new[]{"rgbDevicesRequest","rgbCommandRequest"}.Contains(type,StringComparer.Ordinal);
public override async Task HandleAsync(string messageType,JsonElement payload,CancellationToken token){var envelope=new PipeEnvelope(messageType,payload);switch(envelope.Type){
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
}}}
