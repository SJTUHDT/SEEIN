using Seein.Core.Themes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace Seein.Tray;

public sealed class ThemeEditorCanvas : FrameworkElement
{
    public ThemeEditorDocument? Document { get; private set; }
    public HashSet<string> Selection { get; } = [];
    public BitmapSource? Frame { get; set; }
    public double Zoom { get; set; } = 1;
    public bool Snap { get; set; } = true;
    public bool GridVisible { get; set; } = true;
    public event Action? SelectionChanged;
    public event Action? GestureFinished;
    public event Action? EditRequested;
    private Point _start;
    private string? _operation;
    private Dictionary<string,ThemeLayer> _originals = [];
    private Rect _rubber;
    private int _resizeCorner=3;
    private System.Windows.Controls.ScrollViewer? _panScroll;
    private Point _panStart; private double _panX,_panY;
    private readonly List<(bool Vertical,double At)> _guides = [];
    public ThemeEditorCanvas() { Focusable = true; ClipToBounds = true; }
    public void SetDocument(ThemeEditorDocument document)
    { Document = document; Width = document.Manifest.Canvas.Width; Height = document.Manifest.Canvas.Height; Selection.Clear(); InvalidateVisual(); SelectionChanged?.Invoke(); }
    public void Select(IEnumerable<string> ids) { Selection.Clear(); Selection.UnionWith(ids); InvalidateVisual(); SelectionChanged?.Invoke(); }
    public Rect SelectionBounds()
    {
        var result = Rect.Empty;
        foreach(var l in Document?.Manifest.Layers.Where(l=>Selection.Contains(l.Id)) ?? []) result.Union(new Rect(l.X,l.Y,l.Width,l.Height));
        return result;
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16,19,26)),null,new Rect(0,0,Width,Height));
        if (Frame is not null) dc.DrawImage(Frame,new Rect(0,0,Width,Height));
        var z = Math.Max(.05,Zoom);
        if(GridVisible && Zoom > .15)
        {
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(30,200,200,200)),.5/z);
            for(double x=0;x<Width;x+=32)dc.DrawLine(grid,new(x,0),new(x,Height));
            for(double y=0;y<Height;y+=32)dc.DrawLine(grid,new(0,y),new(Width,y));
        }
        var pen = new Pen(Brushes.DeepSkyBlue,1.5/z);
        foreach(var layer in Document?.Manifest.Layers.Where(l=>Selection.Contains(l.Id)) ?? [])
        {
            dc.PushTransform(new RotateTransform(layer.Rotation,layer.X+layer.Width/2,layer.Y+layer.Height/2));
            dc.DrawRectangle(null,new Pen(layer.Locked?Brushes.Gray:Brushes.DeepSkyBlue,1/z),new Rect(layer.X,layer.Y,layer.Width,layer.Height));dc.Pop();
        }
        var bounds=SelectionBounds();
        if(!bounds.IsEmpty)
        {
            dc.DrawRectangle(null,pen,bounds);
            foreach(var p in Corners(bounds))dc.DrawRectangle(Brushes.White,pen,new Rect(p.X-4/z,p.Y-4/z,8/z,8/z));
            dc.DrawLine(pen,new(bounds.Left+bounds.Width/2,bounds.Top),new(bounds.Left+bounds.Width/2,bounds.Top-22/z));
            dc.DrawEllipse(Brushes.White,pen,new(bounds.Left+bounds.Width/2,bounds.Top-22/z),5/z,5/z);
        }
        foreach(var guide in _guides)dc.DrawLine(new Pen(Brushes.Magenta,1/z),guide.Vertical?new(guide.At,0):new(0,guide.At),guide.Vertical?new(guide.At,Height):new(Width,guide.At));
        if(_operation=="select")dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(25,0,140,255)),pen,_rubber);
    }
    private static IEnumerable<Point> Corners(Rect r) => new[]{r.TopLeft,r.TopRight,r.BottomLeft,r.BottomRight};
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if(Document is null)return;Focus();_start=e.GetPosition(this);_guides.Clear();
        var bounds=SelectionBounds();var z=Math.Max(.05,Zoom);_operation=null;
        if(!bounds.IsEmpty && Document.Manifest.Layers.Where(l=>Selection.Contains(l.Id)).All(l=>!l.Locked))
        {
            if((_start-new Point(bounds.Left+bounds.Width/2,bounds.Top-22/z)).Length<10/z)_operation="rotate";
            else
            {
                var corners=Corners(bounds).ToArray();
                for(var i=0;i<corners.Length;i++)if((_start-corners[i]).Length<12/z){_operation="resize";_resizeCorner=i;break;}
            }
        }
        if(_operation is null)
        {
            var hit=Document.Manifest.Layers.OrderByDescending(l=>l.Z).FirstOrDefault(l=>l.Visible&&Hit(l,_start));
            if(hit is null)
            { if(!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))Selection.Clear();_operation="select";_rubber=new Rect(_start,_start); }
            else
            {
                var ids=hit.GroupId is null?new[]{hit.Id}:Document.Manifest.Layers.Where(l=>l.GroupId==hit.GroupId).Select(l=>l.Id).ToArray();
                if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control))foreach(var id in ids){if(!Selection.Add(id))Selection.Remove(id);}
                else if(!Selection.Contains(hit.Id)){Selection.Clear();Selection.UnionWith(ids);}
                _operation=hit.Locked?null:"move";
                if(e.ClickCount==2)EditRequested?.Invoke();
            }
            SelectionChanged?.Invoke();
        }
        _originals=Document.Manifest.Layers.Where(l=>Selection.Contains(l.Id)&&!l.Locked).ToDictionary(l=>l.Id);
        if(_operation is not null){Document.BeginGesture();CaptureMouse();}InvalidateVisual();e.Handled=true;
    }
    private static bool Hit(ThemeLayer layer,Point point)
    {
        var transform=new RotateTransform(-layer.Rotation,layer.X+layer.Width/2,layer.Y+layer.Height/2);
        return new Rect(layer.X,layer.Y,layer.Width,layer.Height).Contains(transform.Transform(point));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if(_panScroll is not null&&IsMouseCaptured){var p=e.GetPosition(_panScroll);_panScroll.ScrollToHorizontalOffset(_panX+_panStart.X-p.X);_panScroll.ScrollToVerticalOffset(_panY+_panStart.Y-p.Y);return;}
        if(_operation is null||!IsMouseCaptured||Document is null)return;
        var point=e.GetPosition(this);var dx=point.X-_start.X;var dy=point.Y-_start.Y;_guides.Clear();
        if(_operation=="select"){_rubber=new Rect(_start,point);InvalidateVisual();return;}
        if(_originals.Count==0)return;
        if(_operation=="move")
        {
            if(Snap&&!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                var left=_originals.Values.Min(l=>l.X);var top=_originals.Values.Min(l=>l.Y);
                var w=_originals.Values.Max(l=>l.X+l.Width)-left;var h=_originals.Values.Max(l=>l.Y+l.Height)-top;
                dx=SnapAxis(left+dx,w,true)-left;dy=SnapAxis(top+dy,h,false)-top;
            }
            Document.Transform(_originals,(float)dx,(float)dy);
        }
        else if(_operation=="resize")
        {
            var left=_originals.Values.Min(l=>l.X);var top=_originals.Values.Min(l=>l.Y);
            var w=_originals.Values.Max(l=>l.X+l.Width)-left;var h=_originals.Values.Max(l=>l.Y+l.Height)-top;
            var fromLeft=_resizeCorner is 0 or 2;var fromTop=_resizeCorner is 0 or 1;
            var newWidth=Math.Max(4,w+(fromLeft?-dx:dx));var newHeight=Math.Max(4,h+(fromTop?-dy:dy));
            if(_originals.Count==1&&!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)&&_originals.Values.First().Type is not ("image" or "media-cover"))
            {
                var layer=_originals.Values.First();Document.Edit(new[]{layer.Id},_=>ThemeEditorDocument.Fit(layer with{X=fromLeft?layer.X+layer.Width-(float)newWidth:layer.X,Y=fromTop?layer.Y+layer.Height-(float)newHeight:layer.Y,Width=(float)newWidth,Height=(float)newHeight},Document.Manifest.Canvas));
            }
            else {var scale=(float)Math.Max(.05,Math.Max(newWidth/w,newHeight/h));Document.Transform(_originals,fromLeft?w-w*scale:0,fromTop?h-h*scale:0,scale);}
        }
        else if(_operation=="rotate")
        {
            var left=_originals.Values.Min(l=>l.X);var top=_originals.Values.Min(l=>l.Y);var right=_originals.Values.Max(l=>l.X+l.Width);var bottom=_originals.Values.Max(l=>l.Y+l.Height);
            var cx=(left+right)/2;var cy=(top+bottom)/2;
            var degrees=(Math.Atan2(point.Y-cy,point.X-cx)-Math.Atan2(_start.Y-cy,_start.X-cx))*180/Math.PI;
            if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))degrees=Math.Round(degrees/15)*15;
            Document.Transform(_originals,0,0,1,(float)degrees);
        }
        InvalidateVisual();e.Handled=true;
    }
    private double SnapAxis(double position,double size,bool vertical)
    {
        var targets=new List<double>{0,vertical?Width/2:Height/2,vertical?Width:Height};
        foreach(var l in Document!.Manifest.Layers.Where(l=>!Selection.Contains(l.Id)&&l.Visible))
        {var at=vertical?l.X:l.Y;var extent=vertical?l.Width:l.Height;targets.AddRange(new[]{(double)at,at+extent/2,at+extent});}
        var best=8/Math.Max(.05,Zoom);var result=Math.Round(position/8)*8;double? guide=null;
        foreach(var offset in new[]{0,size/2,size})foreach(var target in targets)
        {var delta=Math.Abs(position+offset-target);if(delta<best){best=delta;result=target-offset;guide=target;}}
        if(guide is double guidePosition)_guides.Add((vertical,guidePosition));
        return result;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if(_operation=="select"&&Document is not null)
        {foreach(var l in Document.Manifest.Layers.Where(l=>l.Visible&&_rubber.IntersectsWith(new Rect(l.X,l.Y,l.Width,l.Height))))Selection.Add(l.Id);SelectionChanged?.Invoke();}
        Finish();e.Handled=true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { if(_operation is not null)Finish();base.OnLostMouseCapture(e); }
    public void Finish(bool cancel=false)
    { var active=_operation is not null;_operation=null;ReleaseMouseCapture();Document?.EndGesture(cancel);_guides.Clear();InvalidateVisual();if(active)GestureFinished?.Invoke(); }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);if(e.ChangedButton!=MouseButton.Middle)return;
        DependencyObject? parent=VisualTreeHelper.GetParent(this);
        while(parent is not null&&parent is not System.Windows.Controls.ScrollViewer)parent=VisualTreeHelper.GetParent(parent);
        if(parent is System.Windows.Controls.ScrollViewer scroll){_panScroll=scroll;_panStart=e.GetPosition(scroll);_panX=scroll.HorizontalOffset;_panY=scroll.VerticalOffset;CaptureMouse();e.Handled=true;}
    }
    protected override void OnMouseUp(MouseButtonEventArgs e){base.OnMouseUp(e);if(e.ChangedButton==MouseButton.Middle){_panScroll=null;ReleaseMouseCapture();e.Handled=true;}}
}
