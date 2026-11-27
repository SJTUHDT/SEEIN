using Seein.Core;
using Seein.Core.Ipc;
using Seein.Contracts;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button=System.Windows.Controls.Button;
using WpfMessageBox=System.Windows.MessageBox;
namespace Seein.Tray;
using Seein.Core.Thermal;
using Seein.Core.Lighting;
using Seein.Core.Tools;
using Seein.SmartHome;
using Seein.Core.Displays;

public sealed class DevicesDesktop : DesktopModule
{
private readonly DisplayShortcutController _displayShortcut=new();
private readonly LampShortcutController _lampShortcut=new(new SmartHomeStore(AppPaths.UserDefault.DataRoot));
private bool _restoringDisplay;
private readonly LatestRequestTracker _fanRequest=new(),_fanCommand=new(),_rgbRequest=new();
    private DisplayControlView? _DisplayControlPage;
    private DisplayControlView DisplayControlPage => _DisplayControlPage ??= CreateDisplayControlPage();
    private DisplayControlView CreateDisplayControlPage() { var view=new DisplayControlView(_displayShortcut);view.ShortcutActionCompleted+=(text,failed)=>Host.Notify("SEEIN · DP1快捷切换",text,failed);view.Visibility=Visibility.Collapsed;return view; }
    private XiaomiLampView? _XiaomiLampPage;
    private XiaomiLampView XiaomiLampPage => _XiaomiLampPage ??= CreateXiaomiLampPage();
    private XiaomiLampView CreateXiaomiLampPage() { var view=new XiaomiLampView(_lampShortcut);view.Visibility=Visibility.Collapsed;return view; }
    private RouterControlView? _RouterControlPage;
    private RouterControlView RouterControlPage => _RouterControlPage ??= CreateRouterControlPage();
    private RouterControlView CreateRouterControlPage() { var view=new RouterControlView();view.Visibility=Visibility.Collapsed;return view; }
    private FanControlView? _FanControlPage;
    private FanControlView FanControlPage => _FanControlPage ??= CreateFanControlPage();
    private FanControlView CreateFanControlPage() { var view=new FanControlView();view.Visibility=Visibility.Collapsed;view.StateRequested+=()=>_=RequestFanStateAsync();view.ConfigRequested+=(settings,command,gpu)=>_=SendFanConfigAsync(settings,command,gpu);view.RestoreRequested+=()=>_=SendFanRestoreAsync();return view; }
    private RgbControlView? _RgbControlPage;
    private CapturePlaybackView? _CapturePlaybackPage;
    private CapturePlaybackView CapturePlaybackPage => _CapturePlaybackPage ??= new CapturePlaybackView { Visibility = Visibility.Collapsed };
    private RgbControlView RgbControlPage => _RgbControlPage ??= CreateRgbControlPage();
    private RgbControlView CreateRgbControlPage() { var view=new RgbControlView();view.Visibility=Visibility.Collapsed;view.StateRequested+=()=>_=RequestRgbDevicesAsync();view.CommandRequested+=request=>_=SendRgbCommandAsync(request);return view; }
    protected override void Initialize()
    {
        _displayShortcut.Initialize(Host.Handle);_lampShortcut.Initialize(Host.Handle);
        Host.AddHotkeyHandler(HandleHotkey);Host.AddShortcutProvider("devices",new(ShortcutItems,SaveShortcut));
        _lampShortcut.ActionCompleted+=(text,failed)=>Host.Notify("SEEIN · 米家挂灯",text,failed);
    }
    private bool HandleHotkey(nint id)
    {
        if(_lampShortcut.Matches(id)){_lampShortcut.Execute(id);return true;}
        if(!_displayShortcut.Matches(id))return false;
        if(_DisplayControlPage is not null)return _DisplayControlPage.HandleShortcut(id);
        if(!_restoringDisplay)_=RestoreDisplayAsync();return true;
    }
    private async Task RestoreDisplayAsync()
    {
        _restoringDisplay=true;
        try{var result=await _displayShortcut.RestoreDp1Async();Host.Notify("SEEIN · DP1快捷切换",result.Success?"已切回 DP1，并回读确认":result.Error??"DP1切换失败",!result.Success);}
        finally{_restoringDisplay=false;}
    }
    private IEnumerable<ShortcutItem> ShortcutItems()
    {
        var settings=_displayShortcut.Settings;yield return new("display.dp1","切回 DP1","显示器",new("display.dp1",settings.Modifiers,settings.Key,settings.Enabled));
        static ShortcutItem Item(string id,string name,LampShortcut? key)=>new(id,name,"米家挂灯",new(id,key?.Modifiers??0,key?.Key??0));
        yield return Item("lamp.on","开灯",_lampShortcut.Profile.PowerOn);yield return Item("lamp.off","关灯",_lampShortcut.Profile.PowerOff);
        foreach(var preset in _lampShortcut.Profile.Items)yield return Item("lamp.preset."+preset.Id,preset.Name,preset.Shortcut);
    }
    private (bool Success,string Message) SaveShortcut(ShortcutBinding binding)
    {
        if(binding.ActionId=="display.dp1")
        {
            if(_DisplayControlPage is not null)return _DisplayControlPage.SaveManagedShortcut(binding);
            var previous=_displayShortcut.Settings;var saved=_displayShortcut.Save(binding.Empty?previous with{Enabled=false}:new(previous.TargetId,binding.Modifiers,binding.Key,binding.Enabled));return(saved,_displayShortcut.Status);
        }
        if(_XiaomiLampPage is not null)return _XiaomiLampPage.SaveManagedShortcut(binding);
        LampShortcut? key=binding.Empty||!binding.Enabled?null:new(binding.Modifiers,binding.Key);var profile=_lampShortcut.Profile;
        profile=binding.ActionId switch{"lamp.on"=>profile with{PowerOn=key},"lamp.off"=>profile with{PowerOff=key},_=>profile with{Presets=profile.Items.Select(p=>"lamp.preset."+p.Id==binding.ActionId?p with{Shortcut=key}:p).ToArray()}};
        return(_lampShortcut.Save(profile),_lampShortcut.Status);
    }
    public override IEnumerable<ModulePage> Pages => [Page("display","显示器设置","设备控制",()=>DisplayControlPage),Page("input-playback","采集播放","设备控制",()=>CapturePlaybackPage),Page("rgb","灯效控制","设备控制",()=>RgbControlPage, ShowRgbPage),Page("fan","风扇控制","设备控制",()=>FanControlPage, ShowFanPage),Page("router","中兴路由器","设备控制",()=>RouterControlPage),Page("lamp","米家挂灯","设备控制",()=>XiaomiLampPage)];
    protected override void HandleMessage(PipeEnvelope message){switch(message.Type){
            case PipeMessageType.FanStateResult:
                var fanState = message.Read<FanStateResult>();
                if (fanState is null) return;
                if (_fanRequest.IsCurrent(fanState.RequestId))
                {
                    FanControlPage.Load(fanState);
                    if (_page == "fan") StatusText.Text = "风扇状态已更新";
                }
                break;
            case PipeMessageType.FanConfigResult:
                var fanConfig = message.Read<FanConfigResult>();
                if (fanConfig is not null && _fanCommand.IsCurrent(fanConfig.RequestId))
                    FanControlPage.ShowResult(fanConfig.Status, fanConfig.Settings, fanConfig.Success, fanConfig.Error);
                break;
            case PipeMessageType.FanRestoreResult:
                var fanRestore = message.Read<FanRestoreResult>();
                if (fanRestore is not null && _fanCommand.IsCurrent(fanRestore.RequestId))
                    FanControlPage.ShowResult(fanRestore.Status, fanRestore.Settings, fanRestore.Success, fanRestore.Error);
                break;
            case PipeMessageType.RgbDevicesResult:
                var rgbDevices = message.Read<RgbDevicesResult>();
                if (rgbDevices is null) return;
                if (_rgbRequest.IsCurrent(rgbDevices.RequestId))
                {
                    RgbControlPage.Load(rgbDevices);
                    if (_page == "rgb") StatusText.Text = "灯效设备已更新";
                }
                break;
            case PipeMessageType.RgbCommandResult:
                var rgbResult = message.Read<RgbCommandResult>();
                if (rgbResult is null || !_rgbRequest.IsCurrent(rgbResult.RequestId)) return;
                if (rgbResult.Status is not null && rgbResult.Settings is not null)
                    RgbControlPage.Load(new RgbDevicesResult(rgbResult.Status, rgbResult.Settings, rgbResult.RequestId));
                RgbControlPage.ShowResult(rgbResult);
                break;
}}
    private async Task RequestFanStateAsync()
    {
        try
        {
            if (!_pipe.IsConnected) { StatusText.Text = "后台服务未连接，风扇控制暂不可用。"; return; }
            var requestId = _fanRequest.Begin();
            await _pipe.SendAsync(PipeMessageType.FanStateRequest, new FanStateRequest(requestId), _shutdown.Token);
        }
        catch { StatusText.Text = "暂时无法读取风扇状态，请稍后重试。"; }
    }
    private async Task SendFanConfigAsync(FanSettings settings, string? command, GpuFanCommand? gpu)
    {
        try
        {
            if (!_pipe.IsConnected) { FanControlPage.ShowTransportError("后台服务离线，设置尚未发送。"); return; }
            _fanRequest.Begin();
            var requestId = _fanCommand.Begin();
            await _pipe.SendAsync(PipeMessageType.FanConfigRequest,
                new FanConfigRequest(settings, command, requestId, gpu), _shutdown.Token);
        }
        catch { FanControlPage.ShowTransportError("风扇设置发送失败，草稿已保留。"); }
    }
    private async Task SendFanRestoreAsync()
    {
        try
        {
            if (!_pipe.IsConnected) { FanControlPage.ShowTransportError("后台服务离线，无法确认硬件控制权。"); return; }
            _fanRequest.Begin();
            var requestId = _fanCommand.Begin();
            await _pipe.SendAsync(PipeMessageType.FanRestoreRequest, new FanRestoreRequest(requestId), _shutdown.Token);
        }
        catch { FanControlPage.ShowTransportError("交还命令发送失败。"); }
    }
    private async Task RequestRgbDevicesAsync()
    {
        try
        {
            if (!_pipe.IsConnected) { StatusText.Text = "后台服务未连接，灯效控制暂不可用。"; return; }
            var requestId = _rgbRequest.Begin();
            await _pipe.SendAsync(PipeMessageType.RgbDevicesRequest, new RgbDevicesRequest(requestId), _shutdown.Token);
        }
        catch { StatusText.Text = "暂时无法读取灯效设备，请稍后重试。"; }
    }
    private async Task SendRgbCommandAsync(RgbCommandRequest request)
    {
        try
        {
            if (!_pipe.IsConnected) throw new IOException("后台服务未连接");
            var requestId = _rgbRequest.Begin();
            await _pipe.SendAsync(PipeMessageType.RgbCommandRequest, request with { RequestId = requestId }, _shutdown.Token);
        }
        catch
        {
            RgbControlPage.ShowResult(new RgbCommandResult(false, Error: "暂时无法发送灯效操作"));
        }
    }
    private void ShowRgbPage()
    {
        ShowPage(RgbControlPage, null, "rgb", "灯效控制", "通过 OpenRGB 驱动机箱灯光；微星主板 ARGB 仍由 MSI Center 管理。");
        _ = RequestRgbDevicesAsync();
    }
    private void ShowFanPage()
    {
        ShowPage(FanControlPage, null, "fan", "风扇控制", "查看温度、转速与控制权，编辑主板和显卡风扇策略。");
        _ = RequestFanStateAsync();
    }
protected override void RefreshCurrentPageData(){_RgbControlPage?.ResetPendingCommand();if(_page=="fan")_=RequestFanStateAsync();if(_page=="rgb")_=RequestRgbDevicesAsync();}
public override Task PrepareStopAsync(CancellationToken token) => _CapturePlaybackPage?.StopPlaybackAsync() ?? Task.CompletedTask;
public override Task StopAsync(CancellationToken token) => _CapturePlaybackPage?.StopPlaybackAsync() ?? Task.CompletedTask;
public override void Dispose(){_CapturePlaybackPage?.Dispose();_DisplayControlPage?.Dispose();_XiaomiLampPage?.Dispose();_RouterControlPage?.Dispose();_displayShortcut.Dispose();_lampShortcut.Dispose();base.Dispose();}
}
