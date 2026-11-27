using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;

namespace Seein.Tray;

public partial class ReleaseNotesWindow : Window
{
    internal static string ProductVersion { get; } =
        typeof(ReleaseNotesWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? typeof(ReleaseNotesWindow).Assembly.GetName().Version?.ToString(3)
        ?? "未知";

    public ReleaseNotesWindow()
    {
        InitializeComponent();
        VersionHeading.Text = $"SEEIN {ProductVersion}";
        MaxHeight = SystemParameters.WorkArea.Height - 32;
        MaxWidth = SystemParameters.WorkArea.Width - 32;
        using var stream = typeof(ReleaseNotesWindow).Assembly.GetManifestResourceStream("Seein.ReleaseNotes.json")
            ?? throw new InvalidOperationException("版本历史资源缺失。");
        var entries = JsonSerializer.Deserialize<ReleaseEntry[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var entry in entries)
        {
            var body = new StackPanel();
            var heading = new TextBlock { Text = entry.Version + (entry.Version == ProductVersion ? " · 本次更新" : ""), FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            System.Windows.Automation.AutomationProperties.SetHeadingLevel(heading, System.Windows.Automation.AutomationHeadingLevel.Level2);
            body.Children.Add(heading);
            body.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap });
            foreach (var change in entry.Changes)
            {
                var line = new TextBlock { Text = "• " + change, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
                line.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); body.Children.Add(line);
            }
            var card = new Border { Child = body, Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 14) };
            card.SetResourceReference(StyleProperty, "CardStyle"); HistoryEntries.Children.Add(card);
        }
        SourceInitialized += (_, _) => WindowBackdrop.TryApply(this, DesktopThemeManager.IsDark);
    }

    private sealed record ReleaseEntry(string Version, string Title, string[] Changes);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
