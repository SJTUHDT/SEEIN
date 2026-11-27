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
public sealed class ApplicationsBackend : BackendModule
{
    private ModuleMessageBus _pipe=null!;
    private PanelSettings _settings=>Settings.Current;
    private readonly object _settingsGate=new();
    private ILogger<ApplicationsBackend> logger=null!;
    private AppUsageService appUsage=null!;
    protected override void Configure(IServiceCollection services)
    {
        services.AddSingleton(_=>new AppUsageRepository(AppPaths.SystemDefault.AppDatabase));
        services.AddSingleton<AppUsageService>();services.AddHostedService<AppUsageWorker>();
    }
    protected override async Task InitializeAsync(CancellationToken token)
    {
        logger=Context.Required<ILoggerFactory>().CreateLogger<ApplicationsBackend>();
        _pipe=new(Context.Required<IModuleMessageBus>());
        appUsage=Required<AppUsageService>();
        await Task.CompletedTask;
    }
    public override bool CanHandle(string type)=>new[]{"afkSample","appSummaryRequest","appMetaRequest","appCatalogRequest","appLoadRequest","appStatsRequest"}.Contains(type,StringComparer.Ordinal);
    public override async Task HandleAsync(string messageType,JsonElement payload,CancellationToken token)
    {
        var envelope=new PipeEnvelope(messageType,payload);
        switch(envelope.Type)
        {
            case PipeMessageType.AfkSample:
                var afk = envelope.Read<AfkSample>();
                if (afk is not null) appUsage.OnAfkSample(afk);
                break;
                case PipeMessageType.AppMetaRequest:
                    if (envelope.Read<AppMetaRequest>() is {} meta && _pipe is not null)
                        await _pipe.BroadcastAsync(PipeMessageType.AppMetaResult, await appUsage.ApplyMetaAsync(meta, CancellationToken.None));
                    break;
                case PipeMessageType.AppCatalogRequest:
                    if (envelope.Read<AppCatalogRequest>() is {} request && _pipe is not null)
                    {
                        AppCatalogResult result;
                        try { result = await appUsage.QueryCatalogAsync(request, CancellationToken.None); }
                        catch (Exception ex) { logger.LogWarning(ex, "Catalog request failed"); result = new([], request.RequestId, Error: "暂时无法读取应用记录，请重试。"); }
                        await _pipe.BroadcastAsync(PipeMessageType.AppCatalogResult, result);
                    }
                    break;
            case PipeMessageType.AppLoadRequest:
                var loadRequest = envelope.Read<AppLoadRequest>();
                if (loadRequest is null || _pipe is null) return;
                try
                {
                    var load = await appUsage.QueryLoadAsync(loadRequest, CancellationToken.None).ConfigureAwait(false);
                    await _pipe.BroadcastAsync(PipeMessageType.AppLoadResult, load).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Application load request failed");
                }
                break;            case PipeMessageType.AppSummaryRequest:
                var summaryRequest = envelope.Read<AppSummaryRequest>();
                if (summaryRequest is null || _pipe is null) return;
                var summaryResult = await appUsage.BuildSummaryAsync(summaryRequest, CancellationToken.None).ConfigureAwait(false);
                await _pipe.BroadcastAsync(PipeMessageType.AppSummaryResult, summaryResult).ConfigureAwait(false);
                break;
            case PipeMessageType.AppStatsRequest:
                var appRequest = envelope.Read<AppStatsRequest>();
                if (appRequest is null || _pipe is null) return;
                try
                {
                    var stats = await appUsage.QueryAsync(appRequest, CancellationToken.None).ConfigureAwait(false);
                    await _pipe.BroadcastAsync(PipeMessageType.AppStatsResult, stats).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Application statistics request failed");
                    await _pipe.BroadcastAsync(PipeMessageType.AppStatsResult,
                        new AppStatsResult(appRequest.Kind, appRequest.LocalDate,
                            new AppStatsSummary(0, 0, 0, 0, 0, 0, 0), [], [], [], [], null, appRequest.RequestId, ex.Message))
                        .ConfigureAwait(false);
                }
                break;
        }
    }
    private void UpdateSettings(Func<PanelSettings,PanelSettings> update)=>Settings.Update(update);

}
