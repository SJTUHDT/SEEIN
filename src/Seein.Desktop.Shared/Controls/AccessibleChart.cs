using System.Windows;
using System.Windows.Automation.Peers;
using Brush = System.Windows.Media.Brush;

namespace Seein.Tray;

public abstract class AccessibleChart : FrameworkElement
{
    private static readonly DependencyProperty ThemeSurfaceProperty = DependencyProperty.Register(
        "ThemeSurface", typeof(Brush), typeof(AccessibleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    protected AccessibleChart() => SetResourceReference(ThemeSurfaceProperty, "PanelBrush");

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);

    private sealed class ChartPeer(AccessibleChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => owner.GetType().Name;
    }
}
