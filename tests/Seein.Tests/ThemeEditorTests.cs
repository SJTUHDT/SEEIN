using Seein.Core;
using Seein.Core.Themes;
using Seein.Core.Rendering;
using SkiaSharp;
using System.Text.Json;

namespace Seein.Tests;

public sealed class ThemeEditorTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"SeeinThemeEditor-"+Guid.NewGuid().ToString("N"));
    private ThemeProjectStore Store()
    {Directory.CreateDirectory(_root);File.WriteAllText(Path.Combine(_root,"storage-layout.json"),JsonSerializer.Serialize(new{ExportRoot=Path.Combine(_root,"cold")}));return new(_root);}
    private static ThemeLayer Layer(string id,float x=20,float y=20)=>new(){Id=id,Type="rect",X=x,Y=y,Width=100,Height=80,Style=new(){["color"]=JsonSerializer.SerializeToElement("#FF0000")}};
    [Fact] public void GestureIsOneUndoStepAndRedoPreservesStyle()
    {
        var d=Store().New();d.Update(m=>m with{Layers=[Layer("a")]});d.BeginGesture();var originals=d.Manifest.Layers.ToDictionary(l=>l.Id);
        for(var i=0;i<25;i++)d.Transform(originals,i,i);
        d.EndGesture();Assert.Equal(44,d.Manifest.Layers[0].X);d.Undo();Assert.Equal(20,d.Manifest.Layers[0].X);d.Redo();Assert.Equal(44,d.Manifest.Layers[0].X);
        Assert.Equal("#FF0000",d.Manifest.Layers[0].Style["color"].GetString());
    }
    [Fact] public void CancelGestureRestoresOriginalAndNewEditClearsRedo()
    {
        var d=Store().New();d.Update(m=>m with{Layers=[Layer("a")]});d.BeginGesture();d.Transform(d.Manifest.Layers.ToDictionary(l=>l.Id),20,30);d.EndGesture(true);Assert.Equal(20,d.Manifest.Layers[0].X);
        d.Update(m=>m with{Name="B"});d.Undo();Assert.True(d.CanRedo);d.Update(m=>m with{Name="C"});Assert.False(d.CanRedo);
    }
    [Fact] public void GroupTransformsStayWithinCanvas()
    {
        var d=Store().New();d.Update(m=>m with{Layers=[Layer("a"),Layer("b",200,200)]});var originals=d.Manifest.Layers.ToDictionary(l=>l.Id);
        d.Transform(originals,99999,99999);Assert.All(d.Manifest.Layers,l=>{Assert.InRange(l.X,0,d.Manifest.Canvas.Width-l.Width);Assert.InRange(l.Y,0,d.Manifest.Canvas.Height-l.Height);});
        Assert.Equal(180,d.Manifest.Layers[1].X-d.Manifest.Layers[0].X);
    }
    [Fact] public void DistributeAndAlignUseStableIdsAcrossCloning()
    {
        var d=Store().New();d.Update(m=>m with{Layers=[Layer("a",10),Layer("b",130),Layer("c",350)]});d.Align(["a","b","c"],"distributeX");
        Assert.Equal(180,d.Manifest.Layers[1].X);d.Align(["a","b","c"],"middle");Assert.Equal(d.Manifest.Layers[0].Y,d.Manifest.Layers[2].Y);
    }
    [Fact] public void OpenCopyNeverChangesOriginalAndPreservesUnknownFields()
    {
        var store=Store();var d=store.New();d.Update(m=>m with{Layers=[Layer("a") with{Style=new(){["futureStyle"]=JsonSerializer.SerializeToElement(new{value=42})}}],Extensions=new(){["futureManifest"]=JsonSerializer.SerializeToElement(true)}});ThemeProjectStore.WriteManifest(d);
        var before=File.ReadAllBytes(Path.Combine(d.Root,"manifest.json"));var copy=store.Open(d.Root,true);copy.Update(m=>m with{Name="Edited"});ThemeProjectStore.WriteManifest(copy);
        Assert.NotEqual(d.Manifest.Id,copy.Manifest.Id);Assert.Equal(before,File.ReadAllBytes(Path.Combine(d.Root,"manifest.json")));Assert.True(copy.Manifest.Extensions!["futureManifest"].GetBoolean());Assert.Equal(42,copy.Manifest.Layers[0].Style["futureStyle"].GetProperty("value").GetInt32());
    }
    [Fact] public void SaveExportAndImportRemainEditableAndSelfContained()
    {
        var store=Store();var d=store.New();var file=Path.Combine(_root,"source.png");using(var b=new SKBitmap(20,20)){b.Erase(SKColors.Blue);using var image=SKImage.FromBitmap(b);using var bytes=image.Encode(SKEncodedImageFormat.Png,100);File.WriteAllBytes(file,bytes.ToArray());}
        var asset=store.ImportAsset(d,file);d.Update(m=>m with{Background=asset,Layers=[Layer("a") with{Rotation=33,Opacity=.5f,GroupId="group",Locked=true}]});
        using var renderer=new ThemeRenderer();var preview=renderer.RenderPng(new(d.Manifest,d.Root),SensorSnapshot.Empty,null,null);
        store.Save(d,preview);Assert.False(d.Dirty);Assert.Single(store.Projects());Assert.StartsWith(store.ProjectRoot,d.ProjectPath!);
        var export=Path.Combine(store.ExportRoot,"test.jptheme");ThemeProjectStore.Export(d,export);Assert.True(ThemePackage.Validate(export).IsValid);
        File.Delete(file);var imported=ThemePackage.Import(export,Path.Combine(_root,"imported"));Assert.True(File.Exists(Path.Combine(imported.RootDirectory,asset)));Assert.Equal(33,imported.Manifest.Layers[0].Rotation);Assert.True(imported.Manifest.Layers[0].Locked);
        var opened=store.Open(d.ProjectPath!,false);Assert.Equal(d.Manifest.Id,opened.Manifest.Id);Assert.Equal(d.ProjectPath,opened.ProjectPath);
    }
    [Fact] public void HotDraftsAndColdProjectsAreSeparated()
    {var store=Store();var d=store.New();ThemeProjectStore.WriteManifest(d);Assert.StartsWith(Path.Combine(_root,"ThemeEditor"),d.Root);Assert.DoesNotContain("cold",d.Root);Assert.Single(store.Drafts());Assert.Empty(store.Projects());}
    [Fact] public void FailedColdSaveRetainsRecoverableSsdDraft()
    {
        var store=Store();var d=store.New();d.Update(m=>m with{Name="Unsaved"});Directory.CreateDirectory(Path.GetDirectoryName(store.ProjectRoot)!);File.WriteAllText(store.ProjectRoot,"blocked");
        Assert.ThrowsAny<IOException>(()=>store.Save(d,[]));Assert.True(d.Dirty);Assert.Equal("Unsaved",ThemePackage.LoadDirectory(d.Root).Manifest.Name);
    }
    [Fact] public void Schema11RoundTripsAndOldSchemasRemainAccepted()
    {
        var d=Store().New();d.Update(m=>m with{Layers=[Layer("a") with{Visible=false,Rotation=45,Opacity=.3f,Name="hidden"}]});ThemeProjectStore.WriteManifest(d);Assert.Equal("1.1",ThemePackage.LoadDirectory(d.Root).Manifest.SchemaVersion);
        Assert.ThrowsAny<ArgumentException>(()=>d.Update(m=>m with{Layers=[Layer("a") with{Opacity=float.NaN}]}));
    }
    [Theory][InlineData("bar")][InlineData("ring")][InlineData("gauge")][InlineData("sparkline")][InlineData("metric")]
    public void AllDataStylesRenderMissingValuesWithoutCrashing(string type)
    {
        var d=Store().New();var layer=ThemeMetricCatalog.Create(type);d.Update(m=>m with{Layers=[layer]});using var renderer=new ThemeRenderer();using var bitmap=SKBitmap.Decode(renderer.RenderPng(new(d.Manifest,d.Root),SensorSnapshot.Empty,null,null));Assert.Equal(462,bitmap.Width);Assert.Equal(1920,bitmap.Height);
    }
    [Fact] public void RealAndDemoMetricCatalogIncludesDynamicFans()
    {
        var values=new SensorSnapshot(DateTimeOffset.Now,new Dictionary<string,double?>{{"fan.system-4.rpm",1298}});
        Assert.Contains(ThemeMetricCatalog.ForSnapshot(values),m=>m.Key=="fan.system-4.rpm"&&m.Unit=="RPM");Assert.All(ThemeMetricCatalog.All,m=>Assert.NotNull(ThemeMetricCatalog.Demo(DateTimeOffset.Now).Get(m.Key)));
    }
    [Fact] public void RotationOpacityAndHiddenLayersRenderAtExpectedPixels()
    {
        var d=Store().New();d.Update(m=>m with{BackgroundColor="#000000",Layers=[Layer("red",50,50) with{Width=100,Height=20,Rotation=90},Layer("hidden",0,0) with{Visible=false}]});using var renderer=new ThemeRenderer();using var b=SKBitmap.Decode(renderer.RenderPng(new(d.Manifest,d.Root),SensorSnapshot.Empty,null,null));
        Assert.Equal(SKColors.Black,b.GetPixel(5,5));Assert.True(b.GetPixel(100,30).Red>200);Assert.True(b.GetPixel(55,60).Red<10);
        d.Update(m=>m with{Layers=[Layer("red",50,50) with{Opacity=.5f}]});using var half=SKBitmap.Decode(renderer.RenderPng(new(d.Manifest,d.Root),SensorSnapshot.Empty,null,null));Assert.InRange(half.GetPixel(75,75).Red,120,135);
    }
    [Fact] public void WarningThresholdChangesTheActualRenderedColor()
    {
        var d=Store().New();var l=ThemeMetricCatalog.Create("bar") with{X=0,Y=0,Width=200,Height=50};var style=new Dictionary<string,JsonElement>(l.Style){["warning"]=JsonSerializer.SerializeToElement(60),["critical"]=JsonSerializer.SerializeToElement(90),["showValue"]=JsonSerializer.SerializeToElement(0)};
        d.Update(m=>m with{Layers=[l with{Style=style}]});using var renderer=new ThemeRenderer();using var bitmap=SKBitmap.Decode(renderer.RenderPng(new(d.Manifest,d.Root),new(DateTimeOffset.Now,new Dictionary<string,double?>{{l.Binding!,95}}),null,null));Assert.Equal(SKColor.Parse("#FF6868"),bitmap.GetPixel(20,20));
    }
    [Fact] public void RepaintingDoesNotInventHistorySamplesAndBindingChangesResetIt()
    {
        var d=Store().New();var layer=ThemeMetricCatalog.Create("sparkline");d.Update(m=>m with{Layers=[layer]});using var r=new ThemeRenderer();var snapshot=ThemeMetricCatalog.Demo(DateTimeOffset.Now);
        for(var i=0;i<4;i++)r.RenderPng(new(d.Manifest,d.Root),snapshot,null,null);Assert.Equal(1,r.TimedHistoryCount(layer.Id));
        r.RenderPng(new(d.Manifest,d.Root),snapshot with{Timestamp=snapshot.Timestamp.AddSeconds(1)},null,null);Assert.Equal(2,r.TimedHistoryCount(layer.Id));
        d.Update(m=>m with{Layers=[layer with{Binding=SensorKeys.GpuLoad}]});r.RenderPng(new(d.Manifest,d.Root),snapshot,null,null);Assert.Equal(1,r.TimedHistoryCount(layer.Id));
    }
    [Fact] public void TrendShowsFirstSampleAndUsesCollectedIntervalDuringWarmup()
    {
        var d=Store().New();var l=ThemeMetricCatalog.Create("sparkline") with{X=10,Y=10,Width=200,Height=100};
        l=l with{Style=new(l.Style){["color"]=JsonSerializer.SerializeToElement("#FFFFFF"),["strokeWidth"]=JsonSerializer.SerializeToElement(4)}};
        d.Update(m=>m with{BackgroundColor="#000000",Layers=[l]});using var r=new ThemeRenderer();var at=DateTimeOffset.Now;
        SensorSnapshot Sample(int seconds)=>new(at.AddSeconds(seconds),new Dictionary<string,double?>{{l.Binding!,50}});
        using var first=SKBitmap.Decode(r.RenderPng(new(d.Manifest,d.Root),Sample(0),null,null));Assert.True(first.GetPixel(207,60).Red>200);
        using var second=SKBitmap.Decode(r.RenderPng(new(d.Manifest,d.Root),Sample(1),null,null));Assert.True(second.GetPixel(50,60).Red>200);Assert.True(second.GetPixel(150,60).Red>200);
        Assert.Equal(2,r.TimedHistoryCount(l.Id)); // drawing a full-width line must not synthesize samples
    }
    [Fact] public void TrendResetsOnClockRollbackAndExpiresOldSamples()
    {
        var d=Store().New();var l=ThemeMetricCatalog.Create("sparkline");l=l with{Style=new(l.Style){["historySeconds"]=JsonSerializer.SerializeToElement(5)}};
        d.Update(m=>m with{Layers=[l]});using var r=new ThemeRenderer();var at=DateTimeOffset.Now;
        void Render(int seconds,double? value)=>r.RenderPng(new(d.Manifest,d.Root),new(at.AddSeconds(seconds),new Dictionary<string,double?>{{l.Binding!,value}}),null,null);
        Render(0,30);Render(1,40);Render(2,50);Assert.Equal(3,r.TimedHistoryCount(l.Id));
        Render(-5,60);Assert.Equal(1,r.TimedHistoryCount(l.Id));Render(-4,70);Assert.Equal(2,r.TimedHistoryCount(l.Id));
        Render(10,null);Assert.Equal(0,r.TimedHistoryCount(l.Id));Render(11,double.NaN);Assert.Equal(0,r.TimedHistoryCount(l.Id));
    }
    [Fact] public void TrendAppliesValueScaleAndClipsStrokeAtPlotBounds()
    {
        var d=Store().New();var l=ThemeMetricCatalog.Create("sparkline") with{X=10,Y=10,Width=200,Height=100};
        l=l with{Style=new(l.Style){["color"]=JsonSerializer.SerializeToElement("#FFFFFF"),["strokeWidth"]=JsonSerializer.SerializeToElement(4),["valueScale"]=JsonSerializer.SerializeToElement(2)}};
        d.Update(m=>m with{BackgroundColor="#000000",Layers=[l]});using var r=new ThemeRenderer();var at=DateTimeOffset.Now;
        for(int i=0;i<2;i++)r.RenderPng(new(d.Manifest,d.Root),new(at.AddSeconds(i),new Dictionary<string,double?>{{l.Binding!,25}}),null,null);
        using var b=SKBitmap.Decode(r.RenderPng(new(d.Manifest,d.Root),new(at.AddSeconds(1),new Dictionary<string,double?>{{l.Binding!,25}}),null,null));
        Assert.True(b.GetPixel(100,60).Red>200);Assert.Equal(SKColors.Black,b.GetPixel(100,85));Assert.Equal(SKColors.Black,b.GetPixel(9,60));Assert.Equal(SKColors.Black,b.GetPixel(210,60));
    }
    [Fact] public void EmbeddedFontsAreValidatedAndLegacyStylesAreRetained()
    {
        var d=Store().New();d.Update(m=>m with{Layers=[Layer("a") with{Style=new(){["fontAsset"]=JsonSerializer.SerializeToElement("../outside.ttf")}}]});ThemeProjectStore.WriteManifest(d);Assert.Throws<InvalidDataException>(()=>ThemePackage.LoadDirectory(d.Root));
    }
    [Fact] public void GifBackgroundCanBeDecodedAndPaused()
    {
        var d=Store().New();var file=Path.Combine(d.Root,"two.gif");
        using(var stream=File.Create(file))using(var w=new BinaryWriter(stream))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));w.Write((ushort)2);w.Write((ushort)2);w.Write(new byte[]{0x80,0,0,255,0,0,0,0,255});
            for(int frame=0;frame<2;frame++)
            {
                w.Write(new byte[]{0x21,0xF9,4,0,10,0,0,0,0x2C});w.Write((ushort)0);w.Write((ushort)0);w.Write((ushort)2);w.Write((ushort)2);w.Write((byte)0);w.Write((byte)2);
                uint bits=0;var shift=0;foreach(var code in new[]{4,frame,4,frame,4,frame,4,frame,5}){bits|=(uint)code<<shift;shift+=3;}w.Write((byte)4);w.Write(bits);w.Write((byte)0);
            }
            w.Write((byte)0x3B);
        }
        d.Update(m=>m with{Background="two.gif"});using var renderer=new ThemeRenderer();
        var loaded=new LoadedTheme(d.Manifest,d.Root);using var first=SKBitmap.Decode(renderer.RenderPng(loaded,SensorSnapshot.Empty,null,null,new(true)));
        using var paused=SKBitmap.Decode(renderer.RenderPng(loaded,SensorSnapshot.Empty,null,null,new(true)));Assert.Equal(first.GetPixel(100,100),paused.GetPixel(100,100));
        Assert.True(first.GetPixel(100,100).Red>200);
        using var next=SKBitmap.Decode(renderer.RenderPng(loaded,SensorSnapshot.Empty,null,null,new(){AnimationSeconds=.125}));
        Assert.True(next.GetPixel(100,100).Blue>200);
    }
    [Fact] public void Mp4BackgroundUsesTheSamePausedPreviewRenderer()
    {
        var d=Store().New();var path=Path.Combine(d.Root,"video.mp4");
        using(var writer=new OpenCvSharp.VideoWriter(path,OpenCvSharp.VideoWriter.FourCC('m','p','4','v'),10,new OpenCvSharp.Size(32,32)))
        {Assert.True(writer.IsOpened());using var frame=new OpenCvSharp.Mat(32,32,OpenCvSharp.MatType.CV_8UC3,new OpenCvSharp.Scalar(0,255,0));for(int i=0;i<5;i++)writer.Write(frame);}
        d.Update(m=>m with{Background="video.mp4"});using var renderer=new ThemeRenderer();using var image=SKBitmap.Decode(renderer.RenderPng(new(d.Manifest,d.Root),SensorSnapshot.Empty,null,null,new(true)));Assert.True(image.GetPixel(100,100).Green>200);
    }
    [Fact] public void ExportCannotIncludeItsOwnArchive()
    {var d=Store().New();Assert.Throws<InvalidOperationException>(()=>ThemeProjectStore.Export(d,Path.Combine(d.Root,"self.jptheme")));}
    [Fact] public void ReopeningEditorResumesOneDraftWithoutCreatingMoreDirectories()
    {var store=Store();var d=store.New();d.Update(m=>m with{Name="恢复测试"});store.Remember(d);var loaded=store.Resume();Assert.NotNull(loaded);Assert.Equal(d.Root,loaded.Root);Assert.Equal("恢复测试",loaded.Manifest.Name);Assert.Single(store.Drafts());}
    [Fact] public void RawPreviewUsesPremultipliedBgraAndPreservesLogicalGeometry()
    {
        var d=Store().New();d.Update(m=>m with{BackgroundColor="#000000",Layers=[Layer("red",20,20)]});using var r=new ThemeRenderer();
        var frame=r.RenderPreview(new(d.Manifest,d.Root),SensorSnapshot.Empty,null,null,.5f);Assert.Equal(231,frame.Width);Assert.Equal(960,frame.Height);
        var index=20*frame.Stride+20*4;Assert.Equal(255,frame.Pixels[index+2]);Assert.Equal(0,frame.Pixels[index]);Assert.Equal(255,frame.Pixels[index+3]);
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
