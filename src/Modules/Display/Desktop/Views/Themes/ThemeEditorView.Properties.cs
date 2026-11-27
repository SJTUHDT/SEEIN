using Seein.Core;
using Seein.Core.Themes;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Seein.Tray;

public partial class ThemeEditorView
{
    private sealed record LayerRow(string Id,string Label);
    private IEnumerable<string> EditableIds => Document.Manifest.Layers.Where(l=>Canvas.Selection.Contains(l.Id)&&!l.Locked).Select(l=>l.Id).ToArray();
    private void SyncLayers()
    {
        if(Document is null)return;_layerSync=true;
        LayerList.ItemsSource=Document.Manifest.Layers.OrderByDescending(l=>l.Z).Select(l=>new LayerRow(l.Id,(l.Locked?"🔒 ":"")+(!l.Visible?"◌ ":"")+(l.Name??l.Type))).ToArray();
        foreach(var row in LayerList.Items.OfType<LayerRow>().Where(r=>Canvas.Selection.Contains(r.Id)))LayerList.SelectedItems.Add(row);
        _layerSync=false;
        var assets=Path.Combine(Document.Root,"assets");AssetList.ItemsSource=Directory.Exists(assets)?Directory.GetFiles(assets).Select(Path.GetFileName).ToArray():[];
    }
    private void LayerList_Changed(object sender,SelectionChangedEventArgs e){if(!_layerSync)Canvas.Select(LayerList.SelectedItems.OfType<LayerRow>().Select(l=>l.Id).ToArray());}
    private void AddElement(string type)
    {
        if(type=="image"){Image_Click(this,new());return;}
        var layer=ThemeMetricCatalog.Create(type=="media-title"?"metric":type);
        if(type=="media-title")layer=layer with{Binding="media.title",Name="正在播放",Format=null,Style=ThemeEditorDocument.Style(layer,"unit","")};
        AddLayer(layer);
    }
    public void AddLayer(ThemeLayer layer)
    {
        var count=Document.Manifest.Layers.Count;
        layer=ThemeEditorDocument.Fit(layer with{Z=Document.Manifest.Layers.Select(l=>l.Z).DefaultIfEmpty().Max()+1,X=24+(count%4)*16,Y=24+(count%8)*24},Document.Manifest.Canvas);
        Document.Update(m=>m with{Layers=m.Layers.Append(layer).ToList()});Canvas.Select(new[]{layer.Id});SyncLayers();Inspect();
    }
    private void AddMetric_Click(object sender,RoutedEventArgs e)
    {if(MetricList.SelectedItem is ThemeMetric metric)AddLayer(ThemeMetricCatalog.Create((MetricStyle.SelectedItem as ComboBoxItem)?.Tag as string??"metric",metric));}
    private void AddMetricCard_Click(object sender,RoutedEventArgs e)
    {
        var metric=MetricList.SelectedItem as ThemeMetric??ThemeMetricCatalog.All[0];var group=Guid.NewGuid().ToString("N");var z=Document.Manifest.Layers.Select(l=>l.Z).DefaultIfEmpty().Max()+1;
        var card=ThemeMetricCatalog.Create("rect") with{X=24,Y=24,Width=360,Height=200,Name=metric.Name+" · 卡片",GroupId=group,Z=z};card=card with{Style=ThemeEditorDocument.Style(card,"color","#182638")};
        var title=ThemeMetricCatalog.Create("text") with{X=42,Y=32,Width=324,Height=50,Format=metric.Name,GroupId=group,Z=z+1};
        var value=ThemeMetricCatalog.Create("metric",metric) with{X=42,Y=90,Width=324,Height=90,GroupId=group,Z=z+2};
        var layers=new[]{card,title,value}.Select(l=>ThemeEditorDocument.Fit(l,Document.Manifest.Canvas)).ToArray();Document.Update(m=>m with{Layers=m.Layers.Concat(layers).ToList()});Canvas.Select(layers.Select(l=>l.Id));SyncLayers();Inspect();
    }
    private void MetricSearch_Changed(object sender,TextChangedEventArgs e)
    {if(MetricList is not null)MetricList.ItemsSource=ThemeMetricCatalog.ForSnapshot(_sensors).Where(m=>(m.Name+m.Key+m.Category).Contains(MetricSearch.Text,StringComparison.OrdinalIgnoreCase)).ToArray();}
    private string? PickAsset(string filter)
    {var dialog=new Microsoft.Win32.OpenFileDialog{Filter=filter};return dialog.ShowDialog(Window.GetWindow(this))==true?dialog.FileName:null;}
    private void Image_Click(object sender,RoutedEventArgs e)
    {
        Try(()=>{var file=PickAsset("图片|*.png;*.jpg;*.jpeg");if(file is null)return;using(var image=SkiaSharp.SKBitmap.Decode(file)){if(image is null||(long)image.Width*image.Height>64_000_000)throw new InvalidDataException("图片无效或超过 6400 万像素。");}
            var asset=_store.ImportAsset(Document,file);AddLayer(ThemeMetricCatalog.Create("image") with{Asset=asset,Name=Path.GetFileName(file),Width=220,Height=140});});
    }
    private void Background_Click(object sender,RoutedEventArgs e)
    {Try(()=>{var file=PickAsset("背景图片或动画|*.png;*.jpg;*.jpeg;*.gif;*.mp4;*.webm");if(file is null)return;var asset=_store.ImportAsset(Document,file);Document.Update(m=>m with{Background=asset});_ = ResetRendererAsync();SyncLayers();Inspect();});}
    private void ClearBackground_Click(object sender,RoutedEventArgs e){Document.Update(m=>m with{Background=null});_ = ResetRendererAsync();Inspect();}
    private void Font_Click(object sender,RoutedEventArgs e)
    {Try(()=>{var file=PickAsset("字体|*.ttf;*.otf");if(file is null)return;var asset=_store.ImportAsset(Document,file);Document.Edit(EditableIds,l=>l with{Style=ThemeEditorDocument.Style(l,"fontAsset",asset)});SyncLayers();Inspect();});}
    private void Undo_Click(object sender,RoutedEventArgs e){Document.Undo();AfterHistory();}
    private void Redo_Click(object sender,RoutedEventArgs e){Document.Redo();AfterHistory();}
    private void AfterHistory(){Canvas.Width=Document.Manifest.Canvas.Width;Canvas.Height=Document.Manifest.Canvas.Height;_loading=true;ThemeName.Text=Document.Manifest.Name;OrientationBox.SelectedIndex=Document.Manifest.Orientation switch{ThemeOrientation.PortraitClockwise=>0,ThemeOrientation.PortraitCounterClockwise=>1,_=>2};_loading=false;SyncLayers();Inspect();}
    private void Delete_Click(object sender,RoutedEventArgs e){var ids=EditableIds.ToHashSet();var remaining=Canvas.Selection.Except(ids).ToArray();Document.Update(m=>m with{Layers=m.Layers.Where(l=>!ids.Contains(l.Id)).ToList()});Canvas.Select(remaining);SyncLayers();Inspect();}
    private void Duplicate_Click(object sender,RoutedEventArgs e){CopySelection();PasteSelection();}
    private void CopySelection(){_clipboard.Clear();_clipboard.AddRange(ThemeEditorDocument.Clone(new(){Layers=Document.Manifest.Layers.Where(l=>Canvas.Selection.Contains(l.Id)).ToList()}).Layers);}
    private void PasteSelection()
    {
        if(_clipboard.Count==0)return;var groups=new Dictionary<string,string>();var z=Document.Manifest.Layers.Select(l=>l.Z).DefaultIfEmpty().Max();
        var layers=_clipboard.Select(l=>{string? group=null;if(l.GroupId is not null){if(!groups.TryGetValue(l.GroupId,out group))groups[l.GroupId]=group=Guid.NewGuid().ToString("N");}
            return ThemeEditorDocument.Fit(l with{Id="element-"+Guid.NewGuid().ToString("N"),X=l.X+16,Y=l.Y+16,GroupId=group,Locked=false,Z=++z},Document.Manifest.Canvas);}).ToArray();
        Document.Update(m=>m with{Layers=m.Layers.Concat(layers).ToList()});Canvas.Select(layers.Select(l=>l.Id));SyncLayers();Inspect();
    }
    private void Group_Click(object sender,RoutedEventArgs e){var id=Guid.NewGuid().ToString("N");Document.Edit(EditableIds,l=>l with{GroupId=id});SyncLayers();Inspect();}
    private void Ungroup_Click(object sender,RoutedEventArgs e){Document.Edit(EditableIds,l=>l with{GroupId=null});SyncLayers();Inspect();}
    private void Lock_Click(object sender,RoutedEventArgs e){var selected=Canvas.Selection.ToArray();var locked=Document.Manifest.Layers.Where(l=>selected.Contains(l.Id)).All(l=>l.Locked);Document.Edit(selected,l=>l with{Locked=!locked});SyncLayers();Inspect();}
    private void Visibility_Click(object sender,RoutedEventArgs e){var ids=Canvas.Selection.ToArray();Document.Edit(ids,l=>l with{Visible=!l.Visible});SyncLayers();Inspect();}
    private void Forward_Click(object sender,RoutedEventArgs e)=>Reorder(1);
    private void Backward_Click(object sender,RoutedEventArgs e)=>Reorder(-1);
    private void Reorder(int direction)
    {
        var selected=EditableIds.ToHashSet();Document.Update(m=>
        {var order=m.Layers.OrderBy(l=>l.Z).ToList();var indexes=Enumerable.Range(0,order.Count).Where(i=>selected.Contains(order[i].Id));if(direction>0)indexes=indexes.Reverse();foreach(var i in indexes.ToArray()){var j=i+direction;if(j>=0&&j<order.Count&&!selected.Contains(order[j].Id))(order[i],order[j])=(order[j],order[i]);}return m with{Layers=order.Select((l,i)=>l with{Z=i}).ToList()};});SyncLayers();
    }
    private void Editor_KeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(Keyboard.FocusedElement is TextBox or ComboBox)return;
        var ctrl=Keyboard.Modifiers.HasFlag(ModifierKeys.Control);var shift=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if(e.Key==Key.Escape){Canvas.Finish(true);Canvas.Select([]);}
        else if(ctrl&&e.Key==Key.Z){if(shift)Document.Redo();else Document.Undo();AfterHistory();}
        else if(ctrl&&e.Key==Key.Y){Document.Redo();AfterHistory();}
        else if(ctrl&&e.Key==Key.C)CopySelection();else if(ctrl&&e.Key==Key.V)PasteSelection();else if(ctrl&&e.Key==Key.D)Duplicate_Click(this,new());
        else if(ctrl&&e.Key==Key.A)Canvas.Select(Document.Manifest.Layers.Where(l=>l.Visible).Select(l=>l.Id));
        else if(ctrl&&e.Key==Key.G){if(shift)Ungroup_Click(this,new());else Group_Click(this,new());}
        else if(ctrl&&e.Key==Key.S)Save_Click(this,new());
        else if(e.Key==Key.Delete)Delete_Click(this,new());
        else if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {var n=shift?10:1;Document.Edit(EditableIds,l=>ThemeEditorDocument.Fit(l with{X=l.X+(e.Key==Key.Left?-n:e.Key==Key.Right?n:0),Y=l.Y+(e.Key==Key.Up?-n:e.Key==Key.Down?n:0)},Document.Manifest.Canvas));}
        else return;e.Handled=true;
    }
    private void Example_Click(object sender,RoutedEventArgs e)
    {
        Document.Update(m=>m with{Layers=[]});var types=new[]{"metric","bar","ring","gauge","sparkline"};
        foreach(var (type,index) in types.Select((t,i)=>(t,i))){var l=ThemeMetricCatalog.Create(type,ThemeMetricCatalog.All[index]);l=ThemeEditorDocument.Fit(l with{X=32,Y=80+index*210,Width=360,Height=type is "ring" or "gauge"?180:100,Z=index},Document.Manifest.Canvas);Document.Update(m=>m with{Layers=m.Layers.Append(l).ToList()});}
        SyncLayers();Canvas.Select([]);Inspect();
    }
    private void SavePreset_Click(object sender,RoutedEventArgs e)
    {
        Try(()=>{if(Canvas.Selection.Count==0){Status("先选择需要保存的组件");return;}ThemeProjectStore.WriteManifest(Document);var copy=_store.Open(Document.Root,true);
            copy.Update(m=>m with{Name="组件预设",Layers=m.Layers.Where(l=>Canvas.Selection.Contains(l.Id)).ToList()});
            var folder=Path.Combine(Path.GetDirectoryName(_store.ProjectRoot)!,"组件预设");Directory.CreateDirectory(folder);
            var dialog=new Microsoft.Win32.SaveFileDialog{Filter="组件预设|*.jptheme",InitialDirectory=folder,FileName="组件预设.jptheme"};if(dialog.ShowDialog(Window.GetWindow(this))==true){ThemeProjectStore.Export(copy,dialog.FileName);Status("组件预设已保存（含素材）");}});
    }
    private void LoadPreset_Click(object sender,RoutedEventArgs e)
    {Try(()=>{var file=PickAsset("组件预设|*.jptheme");if(file is null)return;var imported=ThemePackage.Import(file,Path.Combine(AppPaths.UserDefault.DataRoot,"ThemeEditor","preset-imports",Guid.NewGuid().ToString("N")));
        var mapped=new List<ThemeLayer>();foreach(var layer in imported.Manifest.Layers){var style=new Dictionary<string,JsonElement>(layer.Style);if(style.TryGetValue("fontAsset",out var f)&&f.ValueKind==JsonValueKind.String)style["fontAsset"]=JsonSerializer.SerializeToElement(_store.ImportAsset(Document,Path.Combine(imported.RootDirectory,f.GetString()!)));
            mapped.Add(layer with{Asset=layer.Asset is null?null:_store.ImportAsset(Document,Path.Combine(imported.RootDirectory,layer.Asset)),Style=style});}_clipboard.Clear();_clipboard.AddRange(mapped);PasteSelection();});}
    private void Inspect()
    {
        if(Document is null)return;Properties.Children.Clear();
        var chosen=Document.Manifest.Layers.Where(l=>Canvas.Selection.Contains(l.Id)).ToArray();
        void Title(string text)=>Properties.Children.Add(new TextBlock{Text=text,FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new(0,6,0,12),TextWrapping=TextWrapping.Wrap});
        void Field(string label,string value,Action<string> save)
        {
            Properties.Children.Add(new TextBlock{Text=label,FontSize=12,Margin=new(0,8,0,4)});
            var box=new TextBox{Text=value,MinHeight=32,ToolTip=label,HorizontalContentAlignment=System.Windows.HorizontalAlignment.Left};
            System.Windows.Automation.AutomationProperties.SetName(box,label);
            void Commit(){Try(()=>{if(box.Text==value)return;save(box.Text);value=box.Text;_dirtyFrame=true;});}
            box.LostKeyboardFocus+=(_,_)=>Commit();box.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();e.Handled=true;}};
            if(label.Contains("颜色"))
            {
                var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new GridLength(36)});row.ColumnDefinitions.Add(new());
                var swatch=new Button{Width=30,Height=32,Margin=new(0,0,6,0),ToolTip="选择颜色"};
                void Paint(){try{swatch.Background=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(box.Text)!;}catch{}}
                Paint();box.TextChanged+=(_,_)=>Paint();
                swatch.Click+=(_,_)=>
                {
                    var palette=new WrapPanel{Width=220,Margin=new(10)};
                    var popup=new System.Windows.Controls.Primitives.Popup{PlacementTarget=swatch,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
                    foreach(var color in new[]{"#FFFFFF","#D0D8E2","#94A3B8","#475569","#202833","#000000","#FF6B6B","#FF9F43","#FFC857","#B7E65C","#66DFB3","#36CFC9","#60B4FF","#5C7CFA","#AF9BFF","#E399FF","#FF80C0","#FF0080","#800040","#0080FF","#80FFFF","#FFFF00","#00FFFF","#FF00FF"})
                    {
                        var hex=color;var chip=new Button{Width=30,Height=28,Margin=new(3),Padding=new(0),ToolTip=hex,Background=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!};
                        chip.Click+=(_,_)=>{box.Text=hex;Commit();popup.IsOpen=false;};palette.Children.Add(chip);
                    }
                    var border=new Border{Child=palette,CornerRadius=new(8),BorderThickness=new(1),Padding=new(2)};border.SetResourceReference(Border.BackgroundProperty,"PanelBrush");border.SetResourceReference(Border.BorderBrushProperty,"CardStrokeBrush");popup.Child=border;popup.IsOpen=true;
                };
                row.Children.Add(swatch);Grid.SetColumn(box,1);row.Children.Add(box);Properties.Children.Add(row);
            }
            else Properties.Children.Add(box);
        }
        void Choice(string label,string current,IReadOnlyList<(string Id,string Name)> choices,Action<string> save)
        {
            Properties.Children.Add(new TextBlock{Text=label,FontSize=12,Margin=new(0,8,0,4)});
            var box=new ComboBox{ItemsSource=choices.Select(c=>c.Name).ToArray(),SelectedIndex=Math.Max(0,choices.ToList().FindIndex(c=>c.Id==current)),MinHeight=32};
            System.Windows.Automation.AutomationProperties.SetName(box,label);box.SelectionChanged+=(_,_)=>{if(box.SelectedIndex>=0)Try(()=>save(choices[box.SelectedIndex].Id));};Properties.Children.Add(box);
        }
        static float Number(string s,float min,float max){if(!float.TryParse(s,NumberStyles.Float,CultureInfo.CurrentCulture,out var v)||!float.IsFinite(v)||v<min||v>max)throw new ArgumentException($"请输入 {min}–{max} 范围的数字");return v;}
        if(chosen.Length==0)
        {
            Title("画布与背景");Field("背景颜色",Document.Manifest.BackgroundColor,v=>{ValidateColor(v);Document.Update(m=>m with{BackgroundColor=v});});
            void Bg(string key,object value)=>Document.Update(m=>m with{BackgroundStyle=new(m.BackgroundStyle){[key]=JsonSerializer.SerializeToElement(value)}});
            string Bs(string k,string fallback)=>Document.Manifest.BackgroundStyle.TryGetValue(k,out var v)?v.ToString():fallback;
            Choice("背景适配",Bs("fit","cover"),new[]{("cover","填充裁剪"),("contain","完整适应"),("stretch","拉伸")},v=>Bg("fit",v));
            Field("背景透明度（0–1）",Bs("opacity","1"),v=>Bg("opacity",Number(v,0,1)));
            Field("水平裁剪位置（0–1）",Bs("cropX","0.5"),v=>Bg("cropX",Number(v,0,1)));Field("垂直裁剪位置（0–1）",Bs("cropY","0.5"),v=>Bg("cropY",Number(v,0,1)));
            Field("刷新帧率（1–30）",Document.Manifest.FrameRate.ToString(),v=>Document.Update(m=>m with{FrameRate=(int)Number(v,1,30)}));
            Field("JPEG 质量（50–100）",Document.Manifest.JpegQuality.ToString(),v=>Document.Update(m=>m with{JpegQuality=(int)Number(v,50,100)}));
            Properties.Children.Add(new TextBlock{Text="选择元素后可编辑位置、数据来源及外观。\n\n拖动：移动\n右下角：缩放\n上方圆点：旋转\nCtrl：多选\nShift：等比缩放／旋转吸附\nAlt：暂时关闭吸附",TextWrapping=TextWrapping.Wrap,Margin=new(0,18,0,0)});return;
        }
        var first=chosen[0];Title(chosen.Length==1?first.Name??"元素属性":$"已选 {chosen.Length} 个元素");
        if(chosen.All(l=>l.Locked)){Properties.Children.Add(new TextBlock{Text="元素已锁定，请在图层面板解锁。",TextWrapping=TextWrapping.Wrap});return;}
        void Edit(Func<ThemeLayer,ThemeLayer> edit){Document.Edit(EditableIds,edit);Canvas.InvalidateVisual();}
        void Style(string key,object value)=>Edit(l=>l with{Style=ThemeEditorDocument.Style(l,key,value)});
        string S(string key,string fallback)=>first.Style.TryGetValue(key,out var v)?v.ToString():fallback;
        Field("元素名称",first.Name??first.Type,v=>{Edit(l=>l with{Name=v});SyncLayers();});
        Field("X",first.X.ToString(CultureInfo.CurrentCulture),v=>Edit(l=>ThemeEditorDocument.Fit(l with{X=Number(v,0,Document.Manifest.Canvas.Width)},Document.Manifest.Canvas)));
        Field("Y",first.Y.ToString(CultureInfo.CurrentCulture),v=>Edit(l=>ThemeEditorDocument.Fit(l with{Y=Number(v,0,Document.Manifest.Canvas.Height)},Document.Manifest.Canvas)));
        Field("宽度",first.Width.ToString(CultureInfo.CurrentCulture),v=>Edit(l=>ThemeEditorDocument.Fit(l with{Width=Number(v,1,Document.Manifest.Canvas.Width)},Document.Manifest.Canvas)));
        Field("高度",first.Height.ToString(CultureInfo.CurrentCulture),v=>Edit(l=>ThemeEditorDocument.Fit(l with{Height=Number(v,1,Document.Manifest.Canvas.Height)},Document.Manifest.Canvas)));
        Field("旋转角度",first.Rotation.ToString(CultureInfo.CurrentCulture),v=>Edit(l=>l with{Rotation=Number(v,-360,360)}));
        Field("透明度（0–1）",first.Opacity.ToString(CultureInfo.CurrentCulture),v=>Edit(l=>l with{Opacity=Number(v,0,1)}));
        if(first.Binding is not null&&first.Binding.StartsWith("media.")!=true)
        {
            Title("监测与外观");var metrics=ThemeMetricCatalog.ForSnapshot(_sensors).ToArray();
            Choice("数据来源",first.Binding,metrics.Select(m=>(m.Key,m.ToString())).ToArray(),key=>{var metric=metrics.First(m=>m.Key==key);Edit(l=>l with{Binding=key,Format=metric.Format,Style=new(l.Style){["max"]=JsonSerializer.SerializeToElement(metric.Maximum),["unit"]=JsonSerializer.SerializeToElement(metric.Unit)}});});
            Choice("显示风格",first.Type,new[]{("metric","数字读数"),("bar","进度条"),("ring","圆环"),("gauge","指针仪表"),("sparkline","趋势图")},v=>{Edit(l=>l with{Type=v});Dispatcher.BeginInvoke(Inspect);});
            Field("最小值",S("min","0"),v=>Style("min",Number(v,-1e12f,1e12f)));Field("最大值",S("max","100"),v=>Style("max",Number(v,-1e12f,1e12f)));
            Field("单位",S("unit",""),v=>Style("unit",v));Field("数值倍率",S("valueScale","1"),v=>Style("valueScale",Number(v,0,1e9f)));
            Field("警告阈值",S("warning","70"),v=>Style("warning",Number(v,-1e12f,1e12f)));Field("严重阈值",S("critical","90"),v=>Style("critical",Number(v,-1e12f,1e12f)));
            Field("警告颜色",S("warningColor","#FFC857"),v=>{ValidateColor(v);Style("warningColor",v);});Field("严重颜色",S("criticalColor","#FF6868"),v=>{ValidateColor(v);Style("criticalColor",v);});
            var actual=Seein.Core.Rendering.ThemeRenderer.ResolveBinding(first.Binding,_sensors,_power,_media);
            Properties.Children.Add(new TextBlock{Text=(actual is null?"当前未提供此通道；实时预览显示 --。":"当前采集值："+actual),TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)});
        }
        if(first.Type is "text" or "clock" or "metric" or "bar" or "ring" or "gauge")
        {
            Field(first.Type=="text"?"文字内容":first.Type=="clock"?"时间格式":"数值格式",first.Format??"",v=>Edit(l=>l with{Format=v}));
            Field("字体",S("fontFamily","Microsoft YaHei UI"),v=>Edit(l=>l with{Style=new(l.Style.Where(p=>p.Key!="fontAsset")){["fontFamily"]=JsonSerializer.SerializeToElement(v)}}));
            Field("字号",S("fontSize","28"),v=>Style("fontSize",Number(v,1,1000)));
            Choice("字重",S("fontWeight","normal"),new[]{("normal","常规"),("bold","粗体")},v=>Style("fontWeight",v));
            Choice("对齐",S("align","left"),new[]{("left","左对齐"),("center","居中"),("right","右对齐")},v=>Style("align",v));
            Choice("自动换行",S("wrap","0"),new[]{("0","关闭"),("1","开启")},v=>Style("wrap",int.Parse(v)));
        }
        Field("颜色",S("color","#60B4FF"),v=>{ValidateColor(v);Style("color",v);});
        if(first.Type is "rect" or "bar")Field("圆角",S("radius","12"),v=>Style("radius",Number(v,0,1000)));
        if(first.Type=="rect"){Field("边框宽度",S("borderWidth","0"),v=>Style("borderWidth",Number(v,0,100)));Field("边框颜色",S("borderColor","#607080"),v=>{ValidateColor(v);Style("borderColor",v);});}
        if(first.Type is "bar" or "ring" or "gauge")
        {
            Field("轨道颜色",S("trackColor","#303B50"),v=>{ValidateColor(v);Style("trackColor",v);});
            Choice("显示数值",S("showValue","1"),new[]{("1","显示"),("0","隐藏")},v=>Style("showValue",int.Parse(v)));
        }
        if(first.Type=="bar"){Choice("方向",S("direction","horizontal"),new[]{("horizontal","横向"),("vertical","纵向")},v=>Style("direction",v));Field("分段数量",S("segments","1"),v=>Style("segments",Number(v,1,100)));}
        if(first.Type is "ring" or "gauge" or "sparkline" or "line")Field("线宽",S("strokeWidth","10"),v=>Style("strokeWidth",Number(v,1,100)));
        if(first.Type=="ring")Choice("圆环范围",S("sweep","360"),new[]{("360","整圆"),("180","半圆")},v=>Style("sweep",int.Parse(v)));
        if(first.Type=="sparkline"){Choice("图表样式",S("area","0"),new[]{("0","折线"),("1","面积")},v=>Style("area",int.Parse(v)));Field("时间窗口（秒）",S("historySeconds","120"),v=>Style("historySeconds",Number(v,5,3600)));}
        if(first.Type=="image")Choice("图片适配",S("fit","cover"),new[]{("cover","裁剪填充"),("contain","完整适应"),("stretch","拉伸")},v=>Style("fit",v));
    }
    private static void ValidateColor(string text){if(!SkiaSharp.SKColor.TryParse(text,out _))throw new ArgumentException("请输入有效颜色，例如 #60B4FF 或 #8060B4FF。");}
}
