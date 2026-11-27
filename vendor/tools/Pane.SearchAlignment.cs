using Microsoft.UI.Xaml;
using XamlPoint = Windows.Foundation.Point;

namespace Pane
{
    public sealed partial class App
    {
        // Geometry and a drag fixture for the isolated native integration test.
        System.Text.Json.Nodes.JsonObject SeeinSearchPositions(int dragX = 0, int dragY = 0)
        {
            if (Environment.GetEnvironmentVariable("SEEIN_SEARCH_ALIGNMENT_TEST") != "1")
                throw new InvalidOperationException("Search-position diagnostics require an isolated test instance.");
            var result = new System.Text.Json.Nodes.JsonObject();
            if (panels.TryGetValue("launcher", out var launcher) && launcher.View is Views.LauncherView source)
            {
                if (dragX != 0 || dragY != 0) launcher.SimulateDrag(dragX, dragY);
                Add("launcher", launcher.SeeinSearchCenter(source.SeeinSearchInput));
            }
            if (secondaries.TryGetValue(SecondaryId.Files, out var files) && files.View is Views.FilesView target)
                Add("files", files.SeeinSearchCenter(target.SeeinSearchInput));
            return result;
            void Add(string name, XamlPoint center) => result[name] = new System.Text.Json.Nodes.JsonObject { ["x"] = center.X, ["y"] = center.Y };
        }
    }
}

namespace Pane.Views
{
    public sealed partial class LauncherView
    {
        internal FrameworkElement SeeinSearchInput => SearchRow;
    }
    public sealed partial class FilesView
    {
        internal FrameworkElement SeeinSearchInput => Head;
    }
}

namespace Pane.Hosting
{
    public sealed partial class GlassPanel
    {
        // Align the full search-bar strips, including their icons and buttons.
        // The editable textbox alone has an asymmetric center in each header.
        internal XamlPoint SeeinSearchCenter(FrameworkElement input)
        {
            root.UpdateLayout();
            var local = input.TransformToVisual(root).TransformPoint(new XamlPoint(input.ActualWidth / 2, input.ActualHeight / 2));
            return new XamlPoint(Origin.X + local.X * dpi, Origin.Y + local.Y * dpi);
        }

        internal void SeeinAlignSearchAt(FrameworkElement input, XamlPoint center)
        {
            var monitor = At((int)Math.Round(center.X), (int)Math.Round(center.Y));
            dpi = monitor.Dpi;
            Layout(positionX, positionY);
            root.UpdateLayout();
            var local = input.TransformToVisual(root).TransformPoint(new XamlPoint(input.ActualWidth / 2, input.ActualHeight / 2));
            var origin = ClampPosition(monitor, center.X - local.X * dpi, center.Y - local.Y * dpi);
            RememberedOrigin = (origin.X, origin.Y);
            SetAnchor(monitor, origin);
            // Also reposition an existing file window, including one reopened
            // after being resized. New windows receive this origin before Show.
            if (Shown) LayoutAtAnchor();
        }
    }
}
