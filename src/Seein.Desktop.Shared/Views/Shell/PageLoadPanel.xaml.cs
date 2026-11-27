using System.Windows;
using System.Windows.Media.Animation;

namespace Seein.Tray;

public partial class PageLoadPanel : System.Windows.Controls.UserControl
{
    public PageLoadPanel() => InitializeComponent();

    public void Show(string title, string detail, bool busy)
    {
        TitleText.Text = title;
        DetailText.Text = detail;
        Progress.IsIndeterminate = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Progress.IsIndeterminate = false;
        Visibility = Visibility.Collapsed;
    }

    public static void Reveal(FrameworkElement content, bool animate)
    {
        content.BeginAnimation(OpacityProperty, null);
        content.Opacity = 1;
        content.IsHitTestVisible = true;
        if (animate && SystemParameters.ClientAreaAnimation)
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                FillBehavior = FillBehavior.Stop
            });
    }
}
