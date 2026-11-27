using System.IO;
using System.Windows;

namespace Seein.Tray;

public partial class DisplayInputAliasWindow : Window
{
    private readonly Action<string> _save;
    public DisplayInputAliasWindow(string monitorName, string portName, string alias, Action<string> save)
    {
        InitializeComponent();
        PortLabel.Text = $"{monitorName} · {portName}";
        AliasBox.Text = alias;
        _save = save;
        Loaded += (_, _) => { AliasBox.Focus(); AliasBox.SelectAll(); };
        SourceInitialized += (_, _) => WindowBackdrop.TryApply(this, DesktopThemeManager.IsDark);
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try { _save(AliasBox.Text); DialogResult = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Feedback.Text = ex.Message; }
    }
}
