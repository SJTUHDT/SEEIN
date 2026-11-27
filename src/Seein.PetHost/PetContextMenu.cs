using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using Seein.Pet;

namespace Seein.PetHost;

internal sealed class PetContextMenu : ContextMenu
{
    private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private long _outsideSince;
    private bool _pointerEntered;
    private readonly HashSet<MenuItem> _prepared = [];

    public PetContextMenu()
    {
        SetResourceReference(StyleProperty, typeof(ContextMenu));
        // Leave room for the original 28px shadow without shifting the visible panel away from the pointer.
        HorizontalOffset = -28;
        VerticalOffset = -20;
        Opened += (_, _) => { Prepare(Items); _pointerEntered = false; _outsideSince = 0; _leave.Start(); };
        Closed += (_, _) => _leave.Stop();
        _leave.Tick += (_, _) =>
        {
            if (!GetCursorPos(out var cursor)) return;
            if (ContainsPointer(Template.FindName("MenuPanel", this) as FrameworkElement, cursor) || InOpenSubmenu(Items, cursor)) { _pointerEntered = true; _outsideSince = 0; return; }
            if (!_pointerEntered) return;
            var now = Environment.TickCount64;
            if (_outsideSince == 0) _outsideSince = now;
            else if (now - _outsideSince >= 200) IsOpen = false;
        };
    }

    private static bool InOpenSubmenu(ItemCollection items, CursorPoint cursor)
    {
        foreach (var item in items.OfType<MenuItem>().Where(item => item.IsSubmenuOpen))
        {
            if (ContainsPointer(item.Template.FindName("SubmenuPanel", item) as FrameworkElement, cursor)) return true;
            if (InOpenSubmenu(item.Items, cursor)) return true;
        }
        return false;
    }

    private void Prepare(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (_prepared.Add(item)) item.MouseEnter += (_, _) =>
            {
                if (!item.HasItems) return;
                if (item.Template.FindName("PART_Popup", item) is Popup popup)
                {
                    popup.HorizontalOffset = popup.VerticalOffset = 0;
                    popup.Placement = PlacementMode.Custom;
                    popup.CustomPopupPlacementCallback = (size, _, _) => PlacePopup(item, size);
                }
                item.IsSubmenuOpen = true;
            };
            Prepare(item.Items);
        }
    }

    private CustomPopupPlacement[] PlacePopup(MenuItem item, Size size)
    {
        var dpi = VisualTreeHelper.GetDpi(item);
        PetMenuBox Box(FrameworkElement element)
        {
            var origin = element.PointToScreen(new Point());
            var end = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return new(origin.X / dpi.DpiScaleX, origin.Y / dpi.DpiScaleY, (end.X - origin.X) / dpi.DpiScaleX, (end.Y - origin.Y) / dpi.DpiScaleY);
        }
        var parent = VisualTreeHelper.GetParent(item);
        while (parent is not null && parent is not Border { Name: "MenuPanel" or "SubmenuPanel" }) parent = VisualTreeHelper.GetParent(parent);
        var parentBox = parent is FrameworkElement panel ? Box(panel) : Box(item);
        var point = item.PointToScreen(new Point());
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)).WorkingArea;
        var open = new List<PetMenuBox>();
        if (Template.FindName("MenuPanel", this) is FrameworkElement main) open.Add(Box(main));
        void Collect(ItemCollection children)
        {
            foreach (var branch in children.OfType<MenuItem>().Where(branch => branch.IsSubmenuOpen && branch != item))
            {
                if (branch.Template.FindName("SubmenuPanel", branch) is FrameworkElement child && child.IsVisible) open.Add(Box(child));
                Collect(branch.Items);
            }
        }
        Collect(Items);
        var picked = PetMenuLayout.Place(Math.Max(0, size.Width - 56), Math.Max(0, size.Height - 56), point.Y / dpi.DpiScaleY,
            parentBox, open, new(screen.X / dpi.DpiScaleX, screen.Y / dpi.DpiScaleY, screen.Width / dpi.DpiScaleX, screen.Height / dpi.DpiScaleY));
        return [new(new Point(picked.X - point.X / dpi.DpiScaleX - 28, picked.Y - point.Y / dpi.DpiScaleY - 20), PopupPrimaryAxis.Horizontal)];
    }

    private static bool ContainsPointer(FrameworkElement? element, CursorPoint cursor)
    {
        if (element is null || !element.IsVisible) return false;
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return cursor.X >= topLeft.X && cursor.Y >= topLeft.Y && cursor.X < bottomRight.X && cursor.Y < bottomRight.Y;
    }

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32")] private static extern bool GetCursorPos(out CursorPoint point);
}
