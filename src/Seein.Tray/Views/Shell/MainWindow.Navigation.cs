using Seein.Core.Configuration;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
namespace Seein.Tray;
public partial class MainWindow
{
    private sealed record NavigationItem(string Id, Button Button, TextBlock Glyph, TextBlock Label);
    private readonly Dictionary<string, NavigationItem> _navigationItems = [];
    private static readonly string[] DefaultNavigation = ["overview", "themes", "energy", "applications", "devices", "tools", "settings"];
    private static string NavigationKey(string route) => route.StartsWith("tool-", StringComparison.Ordinal) ? "tools" : route is "usage" or "power" or "load" ? "applications" : route == "display" ? "devices" : route;
    private static string NavigationGlyph(string route) => NavigationKey(route) switch
    { "themes" => "\uE790", "applications" => "\uE9D9", "settings" => "\uE713", "devices" => "\uE7F4", "tools" => "\uE74C", _ => "\uE8A5" };
    private void RegisterNavigation(string id, string title, string icon, Button button)
    {
        var glyph = new TextBlock { Text = icon, Style = (Style)FindResource("NavigationGlyphStyle") };
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        content.Children.Add(glyph); content.Children.Add(label); button.Content = content;
        button.Uid = id;
        button.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left; button.Padding = new(12, 9, 12, 9); button.ToolTip = title;
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        _navigationItems.Add(id, new(id, button, glyph, label));
    }
    private void ApplyNavigationOrder()
    {
        var saved = DesktopThemeManager.NavigationOrder.Select(id => id is "apps" or "plugins" ? "settings" : id);
        var order = NavigationOrder.Arrange(_navigationItems.Keys, saved, DefaultNavigation);
        NavigationItems.Children.Clear(); foreach (var id in order) NavigationItems.Children.Add(_navigationItems[id].Button);
        AnimatedReorder.Attach(NavigationItems, "sidebar", () => !_sidebarCollapsed, SaveNavigationOrder);
    }
    private void UpdateNavigationPresentation()
    {
        foreach (var item in _navigationItems.Values)
        {
            item.Label.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            item.Glyph.Visibility = Visibility.Visible; item.Glyph.Margin = _sidebarCollapsed ? new Thickness(0) : new Thickness(0, 0, 12, 0);
            item.Button.HorizontalContentAlignment = _sidebarCollapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
        }
    }
    private void SaveNavigationOrder()
    {
        var visible = NavigationItems.Children.Cast<Button>().Select(b => _navigationItems.Values.Single(i => i.Button == b).Id);
        try { DesktopThemeManager.SetNavigationOrder(NavigationOrder.Remember(DesktopThemeManager.NavigationOrder, visible)); StatusText.Text = "导航顺序已保存"; }
        catch (Exception ex) { StatusText.Text = "导航顺序未能保存：" + ex.Message; }
    }
}
