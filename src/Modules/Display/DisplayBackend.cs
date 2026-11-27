using Seein.Contracts;
using Seein.Core;
using Seein.Core.Hosting;
using Seein.Core.Protocol;
using Seein.Core.Rendering;
using Seein.Core.Sensors;
using Seein.Core.Themes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
namespace Seein.Modules;

public sealed class DisplayBackend : BackendModule
{
    private readonly AppPaths _paths=AppPaths.SystemDefault;
    private readonly object _stateLock=new();
    private ModuleMessageBus _pipe=null!;
    private ILogger<DisplayBackend> logger=null!;
    private PanelSettings _settings=>Settings.Current;
    private Ds916Transport? _transport;
    private ThemeRenderer? _renderer;
    private PresentMonFpsProvider? _fps;
    private LoadedTheme? _theme;
    private MediaSnapshot? _media;
    private DeviceStatus? _deviceStatus;
    private SensorSnapshot _snapshot=SensorSnapshot.Empty;
    private PowerSnapshot? _power;
    private byte[]? _lastJpeg;
    private DateTimeOffset _lastRenderedSensorTimestamp;
    private DateTimeOffset? _lastRenderedMediaTimestamp;
    private string? _lastRenderedThemeId;
    private int _themeRevision, _rendererRevision;
    private readonly CancellationTokenSource _stop=new();
    private Task? _loop;
    private string AssetRoot=>Directory.Exists(Path.Combine(Context.ModuleRoot,"themes"))?Context.ModuleRoot:AppContext.BaseDirectory;
    protected override void Configure(IServiceCollection services) { }
    protected override Task InitializeAsync(CancellationToken token)
    {
        logger=Context.Required<ILoggerFactory>().CreateLogger<DisplayBackend>();
        _pipe=new(Context.Required<IModuleMessageBus>());
        EnsureBundledThemesImported();_theme=LoadActiveTheme();_renderer=new();_fps=new();
        if(_settings.EnablePresentMon)_fps.Start();
        Context.Required<SharedHardware>().Register("display",snapshot=>
        {
            if(_fps?.CurrentFps is not double fps)return snapshot;
            var values=snapshot.Values.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);values[SensorKeys.Fps]=fps;
            return snapshot with {Values=values};
        });
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SEEIN_DISABLE_DEVICE")))
        {
            _transport=new(new WindowsUsbSerialLocator(),status=>
            {
                lock(_stateLock)_deviceStatus=status;
                _=_pipe.BroadcastAsync(PipeMessageType.DeviceStatus,status);
            },message=>logger.LogWarning("{Diagnostic}",message));_transport.Start();
        }
        else _deviceStatus=new(DeviceConnectionState.Disconnected,null,"开发模式：未连接副屏",DateTimeOffset.Now);
        _loop=Task.Run(()=>RenderAsync(_stop.Token));return Task.CompletedTask;
    }
    private async Task RenderAsync(CancellationToken token)
    {
        try
        {
            while(!token.IsCancellationRequested)
            {
                var start=Stopwatch.GetTimestamp();var reading=Context.Required<Seein.Service.PanelState>().Snapshot();
                _snapshot=reading.Sensors;_power=reading.Power;
                LoadedTheme? theme;MediaSnapshot? media;bool connected;int revision;
                lock(_stateLock){theme=_theme;media=_media;connected=_deviceStatus?.State==DeviceConnectionState.Connected;revision=_themeRevision;}
                try
                {
                    if(connected&&theme is not null&&_transport is not null)
                    {
                        if(_rendererRevision!=revision){_renderer?.Dispose();_renderer=new();_rendererRevision=revision;_lastJpeg=null;}
                        var changed=IsAnimatedBackground(theme.Manifest.Background)||_lastJpeg is null||_lastRenderedThemeId!=theme.Manifest.Id
                            ||_lastRenderedSensorTimestamp!=_snapshot.Timestamp||_lastRenderedMediaTimestamp!=media?.Timestamp;
                        if(changed)
                        {
                            _lastJpeg=_renderer!.RenderJpeg(theme,_snapshot,_power,media);
                            _lastRenderedSensorTimestamp=_snapshot.Timestamp;_lastRenderedMediaTimestamp=media?.Timestamp;_lastRenderedThemeId=theme.Manifest.Id;
                        }
                        _transport.SubmitLatest(_lastJpeg!);
                    }
                }
                catch(Exception ex){logger.LogWarning(ex,"Display render failed; telemetry remains independent");}
                var delay=(connected?1000d/Math.Clamp(theme?.Manifest.FrameRate??1,1,30):1000)-Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if(delay>0)await Task.Delay(TimeSpan.FromMilliseconds(delay),token);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
    }
    public override bool CanHandle(string type)=>type is "mediaSnapshot" or "applyThemeRequest" or "deleteThemeRequest" or "hello";
    public override async Task HandleAsync(string type,JsonElement payload,CancellationToken token)
    {
        var envelope=new PipeEnvelope(type,payload);
        switch(envelope.Type)
        {
            case PipeMessageType.MediaSnapshot:
                lock (_stateLock) _media = envelope.Read<MediaSnapshot>();
                break;
            case PipeMessageType.ApplyThemeRequest:
                var request = envelope.Read<ApplyThemeRequest>();
                if (request is null) return;
                try
                {
                    var stagedPackage = ValidateStagedThemePath(request.PackagePath);
                    var imported = ThemePackage.Import(stagedPackage, _paths.Themes);
                    if (request.Activate)
                    {
                        lock (_stateLock) { _theme = imported; _themeRevision++; }
                        UpdateSettings(current => current with { ActiveThemeId = imported.Manifest.Id });
                    }
                    if (_pipe is not null) await _pipe.BroadcastAsync(PipeMessageType.ApplyThemeResult, new ApplyThemeResult(true, imported.Manifest.Name, null));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Theme import failed");
                    if (_pipe is not null) await _pipe.BroadcastAsync(PipeMessageType.ApplyThemeResult, new ApplyThemeResult(false, null, ex.Message));
                }
                finally { DeleteStagedThemePackage(request.PackagePath); }
                break;
            case PipeMessageType.DeleteThemeRequest:
                var deleteRequest = envelope.Read<DeleteThemeRequest>();
                if (deleteRequest is null || _pipe is null) return;
                try
                {
                    ThemePackage.DeleteImported(_paths.Themes, deleteRequest.ThemeId, _settings.ActiveThemeId,
                        Path.Combine(AssetRoot, "themes"));
                    await _pipe.BroadcastAsync(PipeMessageType.DeleteThemeResult,
                        new DeleteThemeResult(true, deleteRequest.ThemeId, RequestId: deleteRequest.RequestId));
                }
                catch (Exception ex)
                {
                    await _pipe.BroadcastAsync(PipeMessageType.DeleteThemeResult,
                        new DeleteThemeResult(false, deleteRequest.ThemeId, ex.Message, deleteRequest.RequestId));
                }
                break;
            case PipeMessageType.Hello:
                if (_pipe is not null)
                {
                    DeviceStatus? device;
                    lock (_stateLock) device = _deviceStatus;
                    await _pipe.BroadcastAsync(PipeMessageType.ThemeManifest, _theme!.Manifest);
                    await _pipe.BroadcastAsync(PipeMessageType.SensorSnapshot, _snapshot);
                    if (_power is not null) await _pipe.BroadcastAsync(PipeMessageType.PowerSnapshot, _power);
                    if (device is not null) await _pipe.BroadcastAsync(PipeMessageType.DeviceStatus, device);
                }
                break;
        }
    }
    private void UpdateSettings(Func<PanelSettings,PanelSettings> update)=>Settings.Update(update);
    private LoadedTheme LoadActiveTheme()
    {
        var active = Path.Combine(_paths.Themes, _settings.ActiveThemeId);
        if (Directory.Exists(active)) return ThemePackage.LoadDirectory(active);
        var bundled = Path.Combine(AssetRoot, "themes", _settings.ActiveThemeId);
        if (Directory.Exists(bundled)) return ThemePackage.LoadDirectory(bundled);
        return ThemePackage.LoadDirectory(Path.Combine(AssetRoot, "themes", "diagnostic-neutral"));
    }
    private void EnsureBundledThemesImported()
    {
        var bundledRoot = Path.Combine(AssetRoot, "themes");
        if (!Directory.Exists(bundledRoot)) return;
        foreach (var source in Directory.GetDirectories(bundledRoot))
        {
            try
            {
                var manifest = ThemePackage.LoadDirectory(source).Manifest;
                var destination = Path.Combine(_paths.Themes, manifest.Id);
                if (Directory.Exists(destination) && !HasBundledThemeChanged(source, destination)) continue;
                var packagePath = Path.Combine(Path.GetTempPath(), $"Seein.{manifest.Id}.{Guid.NewGuid():N}.jptheme");
                try
                {
                    ThemePackage.PackDirectory(source, packagePath);
                    ThemePackage.Import(packagePath, _paths.Themes);
                }
                finally { if (File.Exists(packagePath)) File.Delete(packagePath); }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Bundled theme import failed for {Directory}", source); }
        }
    }
    private static bool HasBundledThemeChanged(string source, string destination)
    {
        try
        {
            var sourceManifest = File.ReadAllBytes(Path.Combine(source, "manifest.json"));
            var installedManifest = File.ReadAllBytes(Path.Combine(destination, "manifest.json"));
            return !sourceManifest.AsSpan().SequenceEqual(installedManifest);
        }
        catch { return true; }
    }
    private void DeleteStagedThemePackage(string packagePath)
    {
        try
        {
            var incoming = Path.GetFullPath(Path.Combine(_paths.DataRoot, "incoming")) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(packagePath);
            if (candidate.StartsWith(incoming, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) File.Delete(candidate);
        }
        catch { }
    }
    private string ValidateStagedThemePath(string packagePath)
    {
        var incomingRoot = Path.GetFullPath(Path.Combine(_paths.DataRoot, "incoming"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(packagePath);
        if (!string.Equals(Path.GetDirectoryName(candidate), incomingRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("主题包必须位于 SEEIN 导入暂存目录。");
        if (!File.Exists(candidate)) throw new FileNotFoundException("主题包不存在。", candidate);
        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("不允许通过重解析点导入主题包。");
        return candidate;
    }
    private static bool IsAnimatedBackground(string? background)
    {
        if (background is null) return false;
        var extension = Path.GetExtension(background);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }


    public override async Task StopAsync(CancellationToken token)
    {
        _stop.Cancel();if(_loop is not null)await _loop.WaitAsync(token);
        if(_transport is not null)await _transport.DisposeAsync();
        if(_fps is not null)await _fps.DisposeAsync();
        Context.Required<SharedHardware>().Remove("display");_renderer?.Dispose();
        await base.StopAsync(token);
    }
}
