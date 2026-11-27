using Seein.Core;
using Seein.Core.Ipc;
using Seein.Contracts;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button=System.Windows.Controls.Button;
using WpfMessageBox=System.Windows.MessageBox;
namespace Seein.Tray;
using Seein.Core.Rendering;
using Seein.Core.Themes;
using System.Windows.Media.Imaging;

public sealed class DisplayDesktop : DesktopModule
{
private readonly System.Collections.ObjectModel.ObservableCollection<ThemeListItem> _themes=[];
private string? _pendingThemeId,_pendingThemeDeleteId,_activeThemeId;private int _previewGeneration;
private readonly MediaSessionCollector _media=new();
private SensorSnapshot _editorSensors=SensorSnapshot.Empty;private PowerSnapshot? _editorPower;private MediaSnapshot? _editorMedia;
    private ThemeLibraryView? _ThemeLibraryPage;
    private ThemeLibraryView ThemeLibraryPage => _ThemeLibraryPage ??= CreateThemeLibraryPage();
    private ThemeLibraryView CreateThemeLibraryPage() { var view=new ThemeLibraryView();view.Visibility=Visibility.Collapsed;view.ImportRequested+=ImportTheme_Click;view.ApplyRequested+=EnableSelectedTheme_Click;view.DeleteRequested+=DeleteSelectedTheme_Click;view.SelectionChanged+=ImportedThemesList_SelectionChanged;view.ThemeList.ItemsSource=_themes;
        view.EditRequested+=()=>{if(view.ThemeList.SelectedItem is ThemeListItem selected)view.OpenEditor(selected.Directory,selected.Source!="我的创作");};
        view.EditorCreated+=editor=>{editor.UpdateTelemetry(_editorSensors,_editorPower,_editorMedia);editor.ProjectSaved+=()=>RefreshThemeList();editor.ApplyPackage=async package=>{_pendingThemeId=editor.Document.Manifest.Id;await _pipe.SendAsync(PipeMessageType.ApplyThemeRequest,new ApplyThemeRequest(package),_shutdown.Token);};};
        RefreshThemeListAfterLoad(view);return view; }
    protected override void Initialize(){}
    public override IEnumerable<ModulePage> Pages => [Page("themes","主题库","主题库",()=>ThemeLibraryPage)];
    protected override void HandleMessage(PipeEnvelope message){switch(message.Type){
            case PipeMessageType.SensorSnapshot: _editorSensors=message.Read<SensorSnapshot>()??SensorSnapshot.Empty;_ThemeLibraryPage?.ExistingEditor?.UpdateTelemetry(sensors:_editorSensors);break;
            case PipeMessageType.PowerSnapshot: _editorPower=message.Read<PowerSnapshot>();_ThemeLibraryPage?.ExistingEditor?.UpdateTelemetry(power:_editorPower);break;
            case PipeMessageType.ThemeManifest:
                var theme = message.Read<ThemeManifest>();
                if (theme is not null)
                {
                    _activeThemeId = theme.Id;
                    if(_ThemeLibraryPage is not null)RefreshThemeList(theme.Id);
                }
                break;
            case PipeMessageType.ApplyThemeResult:
                var result = message.Read<ApplyThemeResult>();
                if (result is not null)
                {
                    _ThemeLibraryPage?.ExistingEditor?.ReportApplyResult(result);
                    StatusText.Text = result.Success ? $"模板“{result.ThemeName}”已完成" : $"模板操作失败：{result.Error}";
                    if (result.Success)
                    {
                        RefreshThemeList(_pendingThemeId);
                    }
                }
                break;
            case PipeMessageType.DeleteThemeResult:
                var deleteResult = message.Read<DeleteThemeResult>();
                if (deleteResult is null || deleteResult.RequestId != _pendingThemeDeleteId) return;
                _pendingThemeDeleteId = null;
                StatusText.Text = deleteResult.Success ? "导入主题已删除" : $"删除失败：{deleteResult.Error}";
                if (deleteResult.Success) RefreshThemeList(_activeThemeId);
                else WpfMessageBox.Show(Host.Window, deleteResult.Error ?? "无法删除此主题。", "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
}}
    private async void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Seein 模板 (*.jptheme)|*.jptheme", CheckFileExists = true };
        if (dialog.ShowDialog(Host.Window) != true) return;
        var validation = ThemePackage.Validate(dialog.FileName);
        if (!validation.IsValid)
        {
            WpfMessageBox.Show(Host.Window, string.Join(Environment.NewLine, validation.Errors), "模板无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _pendingThemeId = validation.Manifest!.Id;
            var stagedPackage = StagePackage(dialog.FileName, validation.Manifest.Id);
            await _pipe.SendAsync(PipeMessageType.ApplyThemeRequest, new ApplyThemeRequest(stagedPackage, Activate: false), _shutdown.Token);
            StatusText.Text = $"正在导入“{validation.Manifest.Name}”…";
        }
        catch (Exception ex) { WpfMessageBox.Show(Host.Window, ex.Message, "服务未连接", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ImportedThemesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ThemeLibraryPage.ThemeList.SelectedItem as ThemeListItem;
        ThemeLibraryPage.ApplyButton.IsEnabled = selected is not null;
        ThemeLibraryPage.DeleteButton.IsEnabled = selected is { Source: "已导入" } &&
            !string.Equals(selected.Id, _activeThemeId, StringComparison.OrdinalIgnoreCase);
        if (selected is not null) await PreviewThemeAsync(selected);
    }
    private async void PreviewSelectedTheme_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeLibraryPage.ThemeList.SelectedItem is ThemeListItem selected) await PreviewThemeAsync(selected);
    }
    private async void EnableSelectedTheme_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeLibraryPage.ThemeList.SelectedItem is not ThemeListItem selected) return;
        try
        {
            var packagePath = CreateStagedPackagePath(selected.Id);
            await Task.Run(() => ThemePackage.PackDirectory(selected.Directory, packagePath));
            _pendingThemeId = selected.Id;
            await _pipe.SendAsync(PipeMessageType.ApplyThemeRequest, new ApplyThemeRequest(packagePath), _shutdown.Token);
            StatusText.Text = $"正在启用“{selected.Name}”…";
        }
        catch (Exception ex) { WpfMessageBox.Show(Host.Window, ex.Message, "启用失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void DeleteSelectedTheme_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeLibraryPage.ThemeList.SelectedItem is not ThemeListItem selected || selected.Source != "已导入" ||
            string.Equals(selected.Id, _activeThemeId, StringComparison.OrdinalIgnoreCase)) return;
        var answer = WpfMessageBox.Show(Host.Window, $"确定删除导入主题“{selected.Name}”吗？此操作无法撤销。",
            "删除主题", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            _pendingThemeDeleteId = requestId;
            await _pipe.SendAsync(PipeMessageType.DeleteThemeRequest,
                new DeleteThemeRequest(selected.Id, requestId), _shutdown.Token);
            StatusText.Text = $"正在删除“{selected.Name}”…";
        }
        catch (Exception ex)
        {
            _pendingThemeDeleteId = null;
            WpfMessageBox.Show(Host.Window, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private async Task PreviewThemeAsync(ThemeListItem selected)
    {
        var generation = ++_previewGeneration;
        try
        {
            var bitmap = await RenderThemePreviewAsync(selected);
            if (generation != _previewGeneration) return;
            ThemeLibraryPage.PreviewImageControl.Source = bitmap;
            ThemeLibraryPage.PreviewHintControl.Visibility = Visibility.Collapsed;
            if (ThemeLibraryPage.Visibility == Visibility.Visible)
                StatusText.Text = $"正在预览“{selected.Name}”（模拟数据）";
        }
        catch (Exception ex) { WpfMessageBox.Show(Host.Window, ex.Message, "预览失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private static async Task<BitmapImage> RenderThemePreviewAsync(ThemeListItem selected)
    {
        var loaded = await Task.Run(() => ThemePackage.LoadDirectory(selected.Directory));
        var jpeg = await Task.Run(() =>
        {
            using var renderer = new ThemeRenderer();
            return renderer.RenderJpeg(loaded, DemoSensors(), DemoPower(), null);
        });
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(jpeg);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
    private void RefreshThemeList(string? selectId = null)
    {
        var requested = selectId ?? (ThemeLibraryPage.ThemeList.SelectedItem as ThemeListItem)?.Id;
        _themes.Clear();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddThemesFrom(Path.Combine(Services.ModuleRoot, "themes"), "内置", ids);
        AddThemesFrom(AppPaths.SystemDefault.Themes, "已导入", ids);
        AddThemesFrom(new ThemeProjectStore(AppPaths.UserDefault.DataRoot).ProjectRoot, "我的创作", ids);
        ThemeLibraryPage.ThemeList.SelectedItem = _themes.FirstOrDefault(item => string.Equals(item.Id, requested, StringComparison.OrdinalIgnoreCase))
            ?? _themes.FirstOrDefault();
    }
    private void AddThemesFrom(string root, string source, HashSet<string> ids)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;
            try
            {
                var theme = ThemePackage.LoadDirectory(directory).Manifest;
                if (ids.Add(theme.Id)) _themes.Add(new ThemeListItem(theme.Id, theme.Name, source, directory));
            }
            catch { }
        }
    }
    private static string StagePackage(string sourcePath, string id)
    {
        var destination = CreateStagedPackagePath(id);
        File.Copy(sourcePath, destination, true);
        return destination;
    }
    private static string CreateStagedPackagePath(string id)
    {
        var staging = Path.Combine(AppPaths.SystemDefault.DataRoot, "incoming");
        Directory.CreateDirectory(staging);
        return Path.Combine(staging, $"{id}.{Guid.NewGuid():N}.jptheme");
    }
    private static SensorSnapshot DemoSensors() => new(DateTimeOffset.Now, new Dictionary<string, double?>
    {
        [SensorKeys.CpuTemperature] = 58, [SensorKeys.CpuLoad] = 42, [SensorKeys.CpuPower] = 76,
        [SensorKeys.GpuTemperature] = 65, [SensorKeys.GpuLoad] = 87, [SensorKeys.GpuPower] = 163,
        [SensorKeys.MemoryLoad] = 54, [SensorKeys.NetworkDown] = 18_500_000, [SensorKeys.NetworkUp] = 2_300_000,
        [SensorKeys.SystemDriveLoad] = 42, [SensorKeys.SystemVolume] = 60, [SensorKeys.Fps] = 144
    });
    private static PowerSnapshot DemoPower() => new(DateTimeOffset.Now, 238, true, 186.4, 820.1, 6240.8, 54219, "估算整机功耗");
private void RefreshThemeListAfterLoad(ThemeLibraryView view){view.Loaded+=(_,_)=>RefreshThemeList(_activeThemeId);}
private sealed record ThemeListItem(string Id,string Name,string Source,string Directory);
public override Task StartAsync(CancellationToken token){_=MediaAsync(token);return Task.CompletedTask;}
private async Task MediaAsync(CancellationToken token){while(!token.IsCancellationRequested){try{if(_pipe.IsConnected){var snapshot=await _media.ReadAsync(token);if(snapshot is not null){_editorMedia=snapshot;_ThemeLibraryPage?.ExistingEditor?.UpdateTelemetry(media:snapshot);await _pipe.SendAsync(PipeMessageType.MediaSnapshot,snapshot,token);}}}catch(OperationCanceledException){break;}catch{}try{await Task.Delay(1000,token);}catch(OperationCanceledException){break;}}}
protected override void RefreshCurrentPageData(){if(_ThemeLibraryPage is not null)RefreshThemeList();}
public override void Dispose(){_ThemeLibraryPage?.ExistingEditor?.Dispose();_media.Dispose();base.Dispose();}
}
