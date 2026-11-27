using Seein.Core;
using Seein.Core.Themes;
using Seein.Core.Rendering;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace Seein.Tray;

public partial class ThemeEditorView : System.Windows.Controls.UserControl, IDisposable
{
    public ThemeEditorDocument Document { get; private set; } = null!;
    private readonly ThemeProjectStore _store = new(AppPaths.UserDefault.DataRoot);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly SemaphoreSlim _renderGate = new(1);
    private ThemeRenderer _renderer = new();
    private SensorSnapshot _sensors = SensorSnapshot.Empty;
    private PowerSnapshot? _power; private MediaSnapshot? _media;
    private DateTime _lastSaved, _lastFrame; private bool _dirtyFrame = true, _rendering, _loading, _disposed, _layerSync;
    private int _generation; private double _zoom = .35;
    private int _draftGeneration=-1;
    private bool _saving;
    private bool _applyPending;
    private readonly List<ThemeLayer> _clipboard = [];
    public Func<string,Task>? ApplyPackage { get; set; }
    public event Action? ProjectSaved;
    public ThemeEditorView()
    {
        InitializeComponent();
        foreach(var (type,name) in new[]{("text","文字"),("image","图片"),("rect","卡片"),("circle","圆形"),("line","线条"),("clock","日期时间"),("media-cover","媒体封面"),("media-title","播放信息")})
        { var t=type;var button=SmallButton(name,()=>AddElement(t));ElementButtons.Children.Add(button); }
        foreach(var (op,name) in new[]{("left","左对齐"),("center","水平居中"),("right","右对齐"),("top","顶对齐"),("middle","垂直居中"),("bottom","底对齐"),("distributeX","横向等距"),("distributeY","纵向等距")})
        {var operation=op;AlignButtons.Children.Add(SmallButton(name,()=>Document.Align(Canvas.Selection,operation)));}
        Canvas.SelectionChanged+=()=>{SyncLayers();Inspect();};Canvas.GestureFinished+=()=>{SyncLayers();Inspect();};Canvas.EditRequested+=()=>Properties.Children.OfType<TextBox>().FirstOrDefault()?.Focus();
        PreviewKeyDown+=Editor_KeyDown;
        _timer.Tick+=async(_,_)=>await TickAsync();
        Loaded+=(_,_)=>{_timer.Start();Fit();};Unloaded+=(_,_)=>{_timer.Stop();SaveDraft();};
        IsVisibleChanged+=(_,_)=>{if(IsVisible){_timer.Start();_dirtyFrame=true;}else{_timer.Stop();SaveDraft();}};
        Attach(_store.Resume()??_store.New());MetricList.ItemsSource=ThemeMetricCatalog.All;MetricList.SelectedIndex=0;
    }
    private static Button SmallButton(string title,Action click)
    {var b=new Button{Content=title,Margin=new(0,0,5,6),Padding=new(9,5,9,5)};b.Click+=(_,_)=>click();return b;}
    public void OpenTheme(string root,bool copy=true)
    {Try(()=>{SaveDraft();Attach(_store.Open(root,copy));});}
    private void Attach(ThemeEditorDocument document)
    {
        if(Document is not null)Document.Changed-=DocumentChanged;
        Document=document;Document.Changed+=DocumentChanged;_generation++;_dirtyFrame=true;
        Canvas.SetDocument(document);_loading=true;ThemeName.Text=document.Manifest.Name;
        OrientationBox.SelectedIndex=document.Manifest.Orientation switch{ThemeOrientation.PortraitClockwise=>0,ThemeOrientation.PortraitCounterClockwise=>1,_=>2};_loading=false;
        SyncLayers();Inspect();Fit();_lastSaved=DateTime.UtcNow;Status("编辑草稿，不影响当前副屏");
        _=ResetRendererAsync();
    }
    private async Task ResetRendererAsync()
    {await _renderGate.WaitAsync();try{_renderer.Dispose();_renderer=new();}finally{_renderGate.Release();}_dirtyFrame=true;}
    private void DocumentChanged()
    { _generation++;_dirtyFrame=true;UndoButton.IsEnabled=Document.CanUndo;RedoButton.IsEnabled=Document.CanRedo;Canvas.InvalidateVisual();Status("有未保存的修改"); }
    public void UpdateTelemetry(SensorSnapshot? sensors=null,PowerSnapshot? power=null,MediaSnapshot? media=null)
    {
        if(sensors is not null)_sensors=sensors;if(power is not null)_power=power;if(media is not null)_media=media;
        if(IsVisible&&DataMode.SelectedIndex==1)_dirtyFrame=true;
    }
    private async Task TickAsync()
    {
        if(_disposed||!IsVisible)return;
        var now=DateTime.UtcNow;var interval=1d/Math.Clamp(Document.Manifest.FrameRate,1,30);
        var animated=Path.GetExtension(Document.Manifest.Background)?.ToLowerInvariant() is ".gif" or ".mp4" or ".webm";
        if(!_rendering&&(now-_lastFrame).TotalSeconds>=interval&&(_dirtyFrame||animated&&PauseAnimation.IsChecked!=true|| (now-_lastFrame).TotalSeconds>=1&&(DataMode.SelectedIndex==0||Document.Manifest.Layers.Any(l=>l.Type=="clock"))))
        {
            _rendering=true;_lastFrame=now;_dirtyFrame=false;
            try
            {
                var image=await RenderAsync();if(image is not null){Canvas.Frame=image;Canvas.InvalidateVisual();}
            }
            catch(Exception ex){Status("预览失败："+ex.Message);}
            finally{_rendering=false;}
        }
        if(Document.Dirty&&_draftGeneration!=_generation&&(now-_lastSaved).TotalSeconds>=2&&!Canvas.IsMouseCaptured)SaveDraft();
    }
    public async Task<BitmapSource?> RenderAsync()
    {
        var document=Document;var theme=new LoadedTheme(ThemeEditorDocument.Clone(document.Manifest),document.Root);
        var live=DataMode.SelectedIndex==1;var sensors=live?_sensors:ThemeMetricCatalog.Demo(DateTimeOffset.Now);
        var power=live?_power:new PowerSnapshot(sensors.Timestamp,238,true,186,820,6240,54219,"估算整机功率");
        var media=_media;var options=new ThemeRenderOptions(PauseAnimation.IsChecked==true);
        var scale=(float)Math.Clamp(_zoom*VisualTreeHelper.GetDpi(this).DpiScaleX,.05,1);
        var frame=await Task.Run(async()=>{await _renderGate.WaitAsync();try{return _renderer.RenderPreview(theme,sensors,power,media,scale,options);}finally{_renderGate.Release();}});
        if(_disposed||!ReferenceEquals(document,Document))return null;
        var image=BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Pbgra32,null,frame.Pixels,frame.Stride);image.Freeze();return image;
    }
    private static BitmapImage Decode(byte[] bytes)
    {var image=new BitmapImage();using var stream=new MemoryStream(bytes);image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();return image;}
    private void SaveDraft()
    {if(Document is null||_disposed||_draftGeneration==_generation)return;Try(()=>{_store.Remember(Document);_draftGeneration=_generation;_lastSaved=DateTime.UtcNow;Status("草稿已自动保存到 SSD");});}
    private void Status(string text)
    {if(EditorStatus is null||Document is null)return;EditorStatus.Text=$"{Document.Manifest.Canvas.Width} × {Document.Manifest.Canvas.Height} · {_zoom:P0} · {text}"+(DataMode.SelectedIndex==1?(_sensors.Timestamp==DateTimeOffset.MinValue?" · 等待实时数据":" · 实时数据"):" · 模拟数据");}
    private void Try(Action action){try{action();}catch(Exception ex){Status(ex.Message);}}
    private async Task SaveProjectAsync()
    {
        if(_saving)throw new InvalidOperationException("正在保存，请稍候。");_saving=true;IsEnabled=false;
        try
        {
            CommitName();var document=Document;var manifest=ThemeEditorDocument.Clone(document.Manifest);var root=document.Root;
            var preview=await Task.Run(()=>{using var renderer=new ThemeRenderer();return renderer.RenderPng(new(manifest,root),ThemeMetricCatalog.Demo(DateTimeOffset.Now),null,null,new(true));});
            await Task.Run(()=>{_store.Save(document,preview);_store.Remember(document);});Status("工程已保存："+Document.ProjectPath);ProjectSaved?.Invoke();
        }finally{_saving=false;IsEnabled=true;}
    }
    private async void Save_Click(object sender,RoutedEventArgs e){try{await SaveProjectAsync();}catch(Exception ex){Status("保存失败，草稿已保留："+ex.Message);}}
    private async void Apply_Click(object sender,RoutedEventArgs e)
    {
        if(_applyPending)return;
        try
        {
            await SaveProjectAsync();if(ApplyPackage is null){Status("此预览未连接副屏，工程已保存");return;}
            var directory=Path.Combine(AppPaths.SystemDefault.DataRoot,"incoming");Directory.CreateDirectory(directory);
            var package=Path.Combine(directory,Guid.NewGuid().ToString("N")+".jptheme");await Task.Run(()=>ThemeProjectStore.Export(Document,package));
            _applyPending=true;ApplyThemeButton.IsEnabled=false;await ApplyPackage(package);if(_applyPending)Status("已发送应用请求，等待后台确认");
        }catch(Exception ex){_applyPending=false;ApplyThemeButton.IsEnabled=true;Status("应用失败，保留原副屏主题及草稿："+ex.Message);}
    }
    public void ReportApplyResult(ApplyThemeResult result)
    {if(!_applyPending)return;_applyPending=false;ApplyThemeButton.IsEnabled=true;Status(result.Success?"主题已应用到副屏":"应用失败，原主题保留："+result.Error);}
    private void Export_Click(object sender,RoutedEventArgs e)
    {
        Try(()=>{Directory.CreateDirectory(_store.ExportRoot);var dialog=new Microsoft.Win32.SaveFileDialog{Filter="SEEIN 主题|*.jptheme",InitialDirectory=_store.ExportRoot,FileName=Document.Manifest.Id+".jptheme"};
            if(dialog.ShowDialog(Window.GetWindow(this))==true){CommitName();ThemeProjectStore.Export(Document,dialog.FileName);Status("主题已导出");}});
    }
    public void NewDocument(){SaveDraft();Attach(_store.New());}
    private void New_Click(object sender,RoutedEventArgs e)=>NewDocument();
    private void Open_Click(object sender,RoutedEventArgs e)
    {Try(()=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="主题工程 manifest.json|manifest.json",InitialDirectory=Directory.Exists(_store.ProjectRoot)?_store.ProjectRoot:null};if(dialog.ShowDialog(Window.GetWindow(this))==true)OpenTheme(Path.GetDirectoryName(dialog.FileName)!,false);});}
    private void Recover_Click(object sender,RoutedEventArgs e)
    {
        var drafts=_store.Drafts().OrderByDescending(d=>File.GetLastWriteTimeUtc(Path.Combine(d,"manifest.json"))).ToArray();
        var menu=new ContextMenu();foreach(var draft in drafts){try{var path=draft;var title=ThemePackage.LoadDirectory(path).Manifest.Name;var item=new MenuItem{Header=title+" · "+Directory.GetLastWriteTime(path).ToString("MM-dd HH:mm")};item.Click+=(_,_)=>OpenTheme(path,true);menu.Items.Add(item);}catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException){}}
        if(menu.Items.Count==0){Status("暂无可恢复草稿");return;}menu.PlacementTarget=(Button)sender;menu.IsOpen=true;
    }
    private void CommitName(){if(Document is not null&&!string.IsNullOrWhiteSpace(ThemeName.Text))Document.Update(m=>m with{Name=ThemeName.Text.Trim()});}
    private void ThemeName_Commit(object sender,KeyboardFocusChangedEventArgs e){if(!_loading)CommitName();}
    private void Orientation_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_loading||Document is null)return;var orientation=OrientationBox.SelectedIndex switch{0=>ThemeOrientation.PortraitClockwise,1=>ThemeOrientation.PortraitCounterClockwise,_=>ThemeOrientation.Landscape};
        var canvas=orientation==ThemeOrientation.Landscape?new ThemeCanvas(1920,462):new(462,1920);
        Document.Update(m=>m with{Orientation=orientation,Canvas=canvas,Layers=m.Layers.Select(l=>ThemeEditorDocument.Fit(l with{X=l.X*canvas.Width/m.Canvas.Width,Y=l.Y*canvas.Height/m.Canvas.Height,Width=l.Width*canvas.Width/m.Canvas.Width,Height=l.Height*canvas.Height/m.Canvas.Height},canvas)).ToList()});
        Canvas.Width=canvas.Width;Canvas.Height=canvas.Height;Fit();Inspect();
    }
    private async void DataMode_Changed(object sender,SelectionChangedEventArgs e)
    {if(Document is null)return;await ResetRendererAsync();Status("已切换数据源，趋势图重新采样");}
    private void PreviewOption_Changed(object sender,RoutedEventArgs e)
    {if(Canvas is null)return;Canvas.Snap=SnapOption?.IsChecked==true;Canvas.GridVisible=GridOption?.IsChecked==true;Canvas.InvalidateVisual();_dirtyFrame=true;}
    private void Fit(){if(Document is null||CanvasScroll.ActualWidth<20)return;SetZoom(Math.Min((CanvasScroll.ActualWidth-24)/Document.Manifest.Canvas.Width,(CanvasScroll.ActualHeight-24)/Document.Manifest.Canvas.Height));}
    private void SetZoom(double zoom){_zoom=Math.Clamp(zoom,.08,4);Canvas.Zoom=_zoom;Canvas.LayoutTransform=new ScaleTransform(_zoom,_zoom);Canvas.InvalidateVisual();_dirtyFrame=true;Status("就绪");}
    private void Fit_Click(object sender,RoutedEventArgs e)=>Fit();
    private void ZoomOut_Click(object sender,RoutedEventArgs e)=>SetZoom(_zoom/1.2);
    private void ZoomIn_Click(object sender,RoutedEventArgs e)=>SetZoom(_zoom*1.2);
    private void Canvas_SizeChanged(object sender,SizeChangedEventArgs e){if(!IsLoaded)Fit();}
    private void Canvas_MouseWheel(object sender,MouseWheelEventArgs e){if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){SetZoom(_zoom*(e.Delta>0?1.15:1/1.15));e.Handled=true;}}
    private void ToggleLeft_Click(object sender,RoutedEventArgs e){LeftPanel.Visibility=LeftPanel.IsVisible?Visibility.Collapsed:Visibility.Visible;LeftColumn.Width=new(LeftPanel.Visibility==Visibility.Collapsed?0:220);}
    private void ToggleRight_Click(object sender,RoutedEventArgs e){RightPanel.Visibility=RightPanel.IsVisible?Visibility.Collapsed:Visibility.Visible;RightColumn.Width=new(RightPanel.Visibility==Visibility.Collapsed?0:255);}
    public void Dispose(){if(_disposed)return;SaveDraft();_disposed=true;_timer.Stop();Document.Changed-=DocumentChanged;_ = Task.Run(async()=>{await _renderGate.WaitAsync();try{_renderer.Dispose();}finally{_renderGate.Release();}});}
}
