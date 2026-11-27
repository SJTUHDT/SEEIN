using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using Media = System.Windows.Media;

namespace Seein.Tray;

public static class FluentTrayMenu
{
    private static readonly Font MenuFont = new("Segoe UI Variable Text", 10f);
    private static readonly Font IconFont = new("Segoe Fluent Icons", 10.5f);
    private static readonly MenuRenderer Renderer = new();

    private sealed record Appearance(string Glyph, bool Destructive);

    public static Forms.ContextMenuStrip Create()
    {
        var menu = new Forms.ContextMenuStrip();
        StyleMenu(menu);
        return menu;
    }

    public static Forms.ToolStripMenuItem Item(string text, string glyph = "", EventHandler? action = null,
        bool destructive = false)
    {
        var item = new Forms.ToolStripMenuItem(text, null, action)
        {
            Tag = new Appearance(glyph, destructive),
            Padding = new Forms.Padding(0, 5, 8, 5)
        };
        return item;
    }

    public static void StyleSubmenu(Forms.ToolStripMenuItem item)
    {
        if (item.DropDown is Forms.ToolStripDropDownMenu menu) StyleMenu(menu);
    }

    private static void StyleMenu(Forms.ToolStripDropDownMenu menu)
    {
        menu.Renderer = Renderer;
        menu.Font = MenuFont;
        menu.ShowImageMargin = true;
        menu.ShowCheckMargin = false;
        menu.DropShadowEnabled = true;
        menu.SizeChanged += (_, _) => UpdateShape(menu);
        menu.Opening += (_, _) =>
        {
            UpdateShape(menu);
            menu.Invalidate();
        };
    }

    private static void UpdateShape(Forms.ToolStripDropDownMenu menu)
    {
        if (menu.Width <= 0 || menu.Height <= 0) return;
        using var path = RoundRectangle(new(0, 0, menu.Width, menu.Height), Scale(menu, 8));
        var previous = menu.Region;
        menu.Region = new Region(path);
        previous?.Dispose();
    }

    private static float Scale(Forms.ToolStrip? menu, float pixels) => pixels * (menu?.DeviceDpi ?? 96) / 96f;

    private static Color ColorOf(string resource, Color fallback)
    {
        if (Wpf.Application.Current?.TryFindResource(resource) is not Media.SolidColorBrush brush) return fallback;
        var color = brush.Color;
        return Color.FromArgb(color.R, color.G, color.B);
    }

    private static GraphicsPath RoundRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var size = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (size <= 0) return path;
        path.AddArc(bounds.Left, bounds.Top, size, size, 180, 90);
        path.AddArc(bounds.Right - size, bounds.Top, size, size, 270, 90);
        path.AddArc(bounds.Right - size, bounds.Bottom - size, size, size, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - size, size, size, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class MenuRenderer : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(ColorOf("PanelBrush", SystemColors.Menu));
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip.Width < 2 || e.ToolStrip.Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRectangle(new(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), Scale(e.ToolStrip, 8));
            using var pen = new Pen(ColorOf("CardStrokeBrush", SystemColors.MenuText));
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed)) return;
            var inset = Scale(e.ToolStrip, 4);
            var bounds = RectangleF.Inflate(new RectangleF(0, 0, e.Item.Width, e.Item.Height), -inset, -1);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRectangle(bounds, Scale(e.ToolStrip, 4));
            using var brush = new SolidBrush(ColorOf("SelectedBrush", SystemColors.Highlight));
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = TextColor(e.Item);
            base.OnRenderItemText(e);
            if (e.Item.Tag is not Appearance { Glyph.Length: > 0 } item) return;
            var size = (int)Math.Round(Scale(e.ToolStrip, 18));
            var left = Math.Max(5, e.TextRectangle.Left - size - (int)Scale(e.ToolStrip, 4));
            Forms.TextRenderer.DrawText(e.Graphics, item.Glyph, IconFont, new Rectangle(left, 0, size, e.Item.Height),
                e.TextColor, Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.VerticalCenter |
                Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var center = new PointF(e.ImageRectangle.Left + e.ImageRectangle.Width / 2f,
                e.ImageRectangle.Top + e.ImageRectangle.Height / 2f);
            var scale = Scale(e.ToolStrip, 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var color = e.Item.Selected && Wpf.SystemParameters.HighContrast
                ? TextColor(e.Item) : ColorOf("AccentBrush", SystemColors.Highlight);
            using var pen = new Pen(color, scale * 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            e.Graphics.DrawLines(pen, new PointF[]
            {
                new(center.X - 4 * scale, center.Y), new(center.X - scale, center.Y + 3 * scale),
                new(center.X + 4 * scale, center.Y - 3 * scale)
            });
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            var x = e.ArrowRectangle.Left + e.ArrowRectangle.Width / 2f;
            var y = e.ArrowRectangle.Top + e.ArrowRectangle.Height / 2f;
            var scale = e.Item?.Owner is { } owner ? Scale(owner, 1) : 1;
            var direction = e.Direction == Forms.ArrowDirection.Left ? -1 : 1;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(e.Item is { } item ? TextColor(item) : SystemColors.MenuText, 1.3f * scale)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };
            e.Graphics.DrawLines(pen, new PointF[]
            {
                new(x - direction * 1.5f * scale, y - 3 * scale), new(x + direction * 1.5f * scale, y),
                new(x - direction * 1.5f * scale, y + 3 * scale)
            });
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var inset = Scale(e.ToolStrip, 11);
            using var pen = new Pen(ColorOf("BorderBrush", SystemColors.MenuText));
            e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, e.Item.Width - inset, e.Item.Height / 2);
        }

        private static Color TextColor(Forms.ToolStripItem item)
        {
            if (!item.Enabled) return Wpf.SystemParameters.HighContrast ? SystemColors.GrayText :
                ColorOf("SubtleTextBrush", SystemColors.GrayText);
            if (Wpf.SystemParameters.HighContrast && (item.Selected || item.Pressed))
                return ColorOf("SelectedTextBrush", SystemColors.HighlightText);
            return item.Tag is Appearance { Destructive: true }
                ? ColorOf("DangerBrush", SystemColors.MenuText) : ColorOf("TextBrush", SystemColors.MenuText);
        }
    }
}
