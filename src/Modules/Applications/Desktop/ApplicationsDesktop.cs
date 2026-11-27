using Seein.Core;
using Seein.Core.Ipc;
using Seein.Contracts;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button=System.Windows.Controls.Button;
using WpfMessageBox=System.Windows.MessageBox;
namespace Seein.Tray;

public sealed class ApplicationsDesktop : DesktopModule
{
private readonly LatestRequestTracker _appUsageRequest=new(),_appLoadRequest=new(),_summaryRequest=new(),_appPowerRequest=new(),_overviewDayRequest=new();
private AfkSampler? _afk;
    private AppUsageView? _AppUsagePage;
    private AppUsageView AppUsagePage => _AppUsagePage ??= CreateAppUsagePage();
    private AppUsageView CreateAppUsagePage() { var view=new AppUsageView();view.Visibility=Visibility.Collapsed;view.DataRequested+=()=>_=RequestAppStatsAsync(view.Kind,view.LocalDate,view.IncludeHidden,_appUsageRequest);view.SummaryRequested+=()=>_=RequestSummaryAsync();return view; }
    private AppPowerView? _AppPowerPage;
    private AppPowerView AppPowerPage => _AppPowerPage ??= CreateAppPowerPage();
    private AppPowerView CreateAppPowerPage() { var view=new AppPowerView();view.Visibility=Visibility.Collapsed;view.DataRequested+=()=>_=RequestAppStatsAsync(view.Kind,view.LocalDate,false,_appPowerRequest);return view; }
    private AppLoadView? _AppLoadPage;
    private AppLoadView AppLoadPage => _AppLoadPage ??= CreateAppLoadPage();
    private AppLoadView CreateAppLoadPage() { var view=new AppLoadView();view.Visibility=Visibility.Collapsed;view.DataRequested+=()=>_=RequestAppLoadAsync();return view; }
    private readonly LatestRequestTracker _catalogRequest = new();
    private CancellationTokenSource? _catalogWait;
    private AppListView? _AppListPage;
    private AppListView AppListPage => _AppListPage ??= CreateAppListPage();
    private AppListView CreateAppListPage() { var view=new AppListView();view.Visibility=Visibility.Collapsed;view.DataRequested+=()=>_=RequestCatalogAsync();view.MetaRequested+=(kind,key,value)=>_=SendMetaAsync(kind,key,value);return view; }
    private async Task SendMetaAsync(string kind, string key, string? value)
    {
        try
        {
            if (!_pipe.IsConnected) { StatusText.Text = "后台服务未连接，设置未保存。"; return; }
            await _pipe.SendAsync(PipeMessageType.AppMetaRequest, new AppMetaRequest(kind, key, value), _shutdown.Token);
        }
        catch { StatusText.Text = "暂时无法保存设置，请稍后重试。"; }
    }
    private async Task RequestCatalogAsync()
    {
        var requestId = _catalogRequest.Begin();
        var previous = _catalogWait;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _catalogWait = waiting;
        previous?.Cancel();
        waiting.CancelAfter(TimeSpan.FromSeconds(20));
        AppListPage.BeginLoad();
        try
        {
            if (!_pipe.IsConnected)
            {
                AppListPage.ShowLoadError("后台服务未连接。连接恢复后会自动重试，也可以点击「刷新」。");
                return;
            }
            await _pipe.SendAsync(PipeMessageType.AppCatalogRequest, new AppCatalogRequest(requestId), waiting.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, waiting.Token);
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested)
        {
            if (ReferenceEquals(_catalogWait, waiting) && !_shutdown.IsCancellationRequested)
                AppListPage.ShowLoadError("应用列表加载超时，请点击「刷新」重试。");
        }
        catch (Exception)
        {
            if (_catalogRequest.IsCurrent(requestId)) AppListPage.ShowLoadError("暂时无法读取应用记录，请点击「刷新」重试。");
        }
        finally
        {
            if (ReferenceEquals(_catalogWait, waiting)) _catalogWait = null;
        }
    }
    private void ShowAppListPage() { ShowPage(AppListPage, null, "apps", "应用列表", ""); _ = RequestCatalogAsync(); }
    protected override void Initialize(){_afk=new AfkSampler(_pipe);_=Task.Run(()=>_afk.LoopAsync(_shutdown.Token));}
    public override IEnumerable<ModulePage> Pages => [Page("usage","应用时长","应用监控",()=>AppUsagePage, ShowAppUsagePage),Page("power","应用功耗","应用监控",()=>AppPowerPage, ShowAppPowerPage),Page("load","应用占用","应用监控",()=>AppLoadPage, ShowAppLoadPage),Page("apps","应用列表","应用设置",()=>AppListPage,ShowAppListPage)];
    protected override void HandleMessage(PipeEnvelope message){switch(message.Type){
            case PipeMessageType.AppMetaResult:
                var metaResult = message.Read<AppMetaResult>();
                if (metaResult is null) return;
                StatusText.Text = metaResult.Success ? "设置已保存" : $"保存失败：{metaResult.Error}";
                RefreshCurrentPageData();
                break;
            case PipeMessageType.AppCatalogResult:
                var catalog = message.Read<AppCatalogResult>();
                if (catalog is null || _catalogWait is null || !_catalogRequest.IsCurrent(catalog.RequestId)) return;
                var completed = _catalogWait;
                _catalogWait = null;
                completed.Cancel();
                if (catalog.Error is not null) AppListPage.ShowLoadError(catalog.Error);
                else AppListPage.Load(catalog);
                break;

            case PipeMessageType.AppSummaryResult:
                var summaryResult = message.Read<AppSummaryResult>();
                if (summaryResult is null) return;
                if (summaryResult.Success && summaryResult.Path is not null)
                {
                    StatusText.Text = $"摘要已生成：{summaryResult.Path}";
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(summaryResult.Path)
                        {
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
                else StatusText.Text = $"摘要生成失败：{summaryResult.Error}";
                break;
            case PipeMessageType.AppLoadResult:
                var load = message.Read<AppLoadResult>();
                if (load is null) return;
                if (_appLoadRequest.IsCurrent(load.RequestId))
                {
                    AppLoadPage.Load(load);
                    if (_page == "load") StatusText.Text = $"应用占用已更新 · {AppLoadView.FormatBytes(load.Machine.NetUpBytes + load.Machine.NetDownBytes)} 流量";
                }
                break;
            case PipeMessageType.AppStatsResult:
                var stats = message.Read<AppStatsResult>();
                if (stats is null) return;
                if (_appUsageRequest.IsCurrent(stats.RequestId))
                {
                    AppUsagePage.Load(stats);
                    if (_page == "usage") StatusText.Text = stats.Error is null ? "应用时长已更新" : $"应用时长读取失败：{stats.Error}";
                }
                if (_overviewDayRequest.IsCurrent(stats.RequestId))
                {
                }
                if (_appPowerRequest.IsCurrent(stats.RequestId))
                {
                    AppPowerPage.Load(stats);
                    if (_page == "power") StatusText.Text = stats.Error is null ? "应用功耗已更新" : $"应用功耗读取失败：{stats.Error}";
                }
                break;
}}
    private void ShowAppUsagePage()
    {
        ShowPage(AppUsagePage, null, "usage", "应用时长", "按进程运行时长统计；灰白交替的行便于对齐每行应用。");
        _ = RequestAppStatsAsync(AppUsagePage.Kind, AppUsagePage.LocalDate, AppUsagePage.IncludeHidden, _appUsageRequest);
    }

    private void ShowAppPowerPage()
    {
        ShowPage(AppPowerPage, null, "power", "应用功耗", "按 CPU 时间与 GPU 占用分摊实测功耗。");
        _ = RequestAppStatsAsync(AppPowerPage.Kind, AppPowerPage.LocalDate, false, _appPowerRequest);
    }
    private void ShowAppLoadPage()
    {
        ShowPage(AppLoadPage, null, "load", "应用占用", "每台应用的平均 CPU/GPU 占用与网络流量，按使用时长摊平。");
        _ = RequestAppLoadAsync();
    }
    private async Task RequestAppStatsAsync(string kind, string date, bool includeHidden, LatestRequestTracker tracker)
    {
        try
        {
            if (!_pipe.IsConnected)
            {
                StatusText.Text = "后台服务未连接，应用统计暂不可用。";
                var empty = new AppStatsResult(kind, date, new AppStatsSummary(0, 0, 0, 0, 0, 0, 0), [], [], [], [],
                    null, tracker.Begin(), "后台服务未连接。");
                if (ReferenceEquals(tracker, _appUsageRequest)) AppUsagePage.Load(empty); else AppPowerPage.Load(empty);
                return;
            }
            var requestId = tracker.Begin();
            await _pipe.SendAsync(PipeMessageType.AppStatsRequest, new AppStatsRequest(kind, date, includeHidden, requestId), _shutdown.Token);
        }
        catch (Exception)
        {
            StatusText.Text = "暂时无法读取应用统计，请稍后重试。";
        }
    }
    private async Task RequestAppLoadAsync()
    {
        try
        {
            if (!_pipe.IsConnected)
            {
                StatusText.Text = "后台服务未连接，应用占用暂不可用。";
                return;
            }
            var requestId = _appLoadRequest.Begin();
            await _pipe.SendAsync(PipeMessageType.AppLoadRequest,
                new AppLoadRequest(AppLoadPage.LocalDate, requestId), _shutdown.Token);
        }
        catch (Exception)
        {
            StatusText.Text = "暂时无法读取应用占用，请稍后重试。";
        }
    }


    private async Task RequestSummaryAsync()
    {
        try
        {
            if (!_pipe.IsConnected) { StatusText.Text = "后台服务未连接，无法生成摘要。"; return; }
            var requestId = _summaryRequest.Begin();
            await _pipe.SendAsync(PipeMessageType.AppSummaryRequest,
                new AppSummaryRequest(AppUsagePage.Kind, AppUsagePage.LocalDate, requestId), _shutdown.Token);
            StatusText.Text = "正在生成摘要…";
        }
        catch { StatusText.Text = "暂时无法生成摘要，请稍后重试。"; }
    }
protected override void RefreshCurrentPageData(){switch(_page){case "usage":ShowAppUsagePage();break;case "power":ShowAppPowerPage();break;case "load":ShowAppLoadPage();break;case "apps":ShowAppListPage();break;}}
}
