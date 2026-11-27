using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using Brush = System.Windows.Media.Brush;
using SystemColors = System.Windows.SystemColors;

namespace Seein.Tray;

public static class DesktopThemeManager
{
    private const string SettingsFileName = "desktop-ui.json";
    private static bool _tracking;
    private static string? _appliedTheme;
    private static string? _accentHex;
    public static string Current { get; private set; } = "System";
    public static bool SidebarCollapsed { get; private set; }
    public static IReadOnlyList<string> NavigationOrder {get;private set;}=[];
    private static readonly Dictionary<string, string[]> TabOrders = new(StringComparer.Ordinal);
    public static IReadOnlyList<string> GetTabOrder(string scope) => TabOrders.GetValueOrDefault(scope) ?? [];
    public static void SetTabOrder(string scope, IEnumerable<string> keys) { TabOrders[scope] = keys.Distinct(StringComparer.Ordinal).Take(100).ToArray(); Save(); }
    public static bool IsDark => _appliedTheme == "Dark";
    public static event Action? Changed;

    /// <summary>Windows personalisation accent, used by embedded web pages to match the shell.</summary>
    public static string AccentHex => _accentHex ??= ReadAccentHex();

    private static string ReadAccentHex()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "ColorizationColor", null) is int value)
            {
                // The stored value is 0xAARRGGBB.
                var rgb = value & 0x00FFFFFF;
                return $"#{(rgb >> 16) & 0xFF:X2}{(rgb >> 8) & 0xFF:X2}{rgb & 0xFF:X2}";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        return "#0067C0";
    }

    public static string Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("theme", out var theme) && theme.GetString() is "Dark" or "Light" or "System")
                    Current = theme.GetString()!;
                if (document.RootElement.TryGetProperty("sidebarCollapsed", out var collapsed) &&
                    collapsed.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    SidebarCollapsed = collapsed.GetBoolean();
                if(document.RootElement.TryGetProperty("tabOrders", out var tabs) && tabs.ValueKind == JsonValueKind.Object)
                    foreach(var entry in tabs.EnumerateObject())
                        if(entry.Value.ValueKind == JsonValueKind.Array) TabOrders[entry.Name] = entry.Value.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()!).Distinct().Take(100).ToArray();
                if(document.RootElement.TryGetProperty("navigationOrder",out var order)&&order.ValueKind==JsonValueKind.Array)
                    NavigationOrder=order.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()!).Where(v=>v.Length is >0 and <=128).Distinct(StringComparer.Ordinal).Take(100).ToArray();
            }
        }
        catch (JsonException) { }
        catch (IOException) { }

        if (!_tracking)
        {
            _tracking = true;
            SystemEvents.UserPreferenceChanged += (_, _) =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
            SystemParameters.StaticPropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SystemParameters.HighContrast)) Apply();
            };
        }
        Apply();
        return Current;
    }

    public static void Set(string theme)
    {
        if (theme is not ("Dark" or "Light" or "System") || theme == Current) return;
        Current = theme;
        Apply();
        Save();
    }

    public static void SetSidebarCollapsed(bool collapsed)
    {
        if (SidebarCollapsed == collapsed) return;
        SidebarCollapsed = collapsed;
        Save();
    }

    public static void SetNavigationOrder(IEnumerable<string> order)
    {
        NavigationOrder=order.Distinct(StringComparer.Ordinal).Take(100).ToArray();Save();
    }
    private static void Save()
    {
        var path = GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var document=File.Exists(path)?System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject():new System.Text.Json.Nodes.JsonObject();
        document["theme"]=Current;document["sidebarCollapsed"]=SidebarCollapsed;document["navigationOrder"]=JsonSerializer.SerializeToNode(NavigationOrder);
        document["tabOrders"] = JsonSerializer.SerializeToNode(TabOrders);
        File.WriteAllText(temporary,document.ToJsonString());
        File.Move(temporary, path, true);
    }

    private static void Apply()
    {
        var theme = SystemParameters.HighContrast ? "Contrast" : Current == "System" ? GetWindowsTheme() : Current;
        if (theme == _appliedTheme && theme != "Contrast") return;
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"/Seein.Tray;component/Styles/Theme.{(theme == "Dark" ? "Dark" : "Light")}.xaml", UriKind.RelativeOrAbsolute)
        };
        if (theme == "Contrast") AddContrastColors(replacement);
        if (dictionaries.Count == 0) dictionaries.Add(replacement);
        else dictionaries[0] = replacement;
        _appliedTheme = theme;
        Changed?.Invoke();
    }

    private static string GetWindowsTheme()
    {
        const string personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        return Registry.GetValue(personalize, "AppsUseLightTheme", 1) is 0 ? "Dark" : "Light";
    }

    private static void AddContrastColors(ResourceDictionary dictionary)
    {
        Map(SystemColors.WindowBrush, "WindowBrush", "SidebarBrush", "PanelBrush", "PanelAltBrush", "ButtonBrush");
        Map(SystemColors.WindowTextBrush, "BorderBrush", "CardStrokeBrush", "ControlStrokeBrush", "TextBrush",
            "MutedTextBrush", "SubtleTextBrush", "ChartGridBrush", "ScrollThumbBrush", "ScrollThumbHoverBrush",
            "GoodBrush", "WarningBrush", "DangerBrush");
        Map(SystemColors.HotTrackBrush, "AccentBrush", "AccentFillBrush", "ChartBandBrush");
        Map(SystemColors.WindowBrush, "WarningFillBrush", "AccentTextBrush", "HoverBrush");
        Map(SystemColors.HighlightBrush, "SelectedBrush", "AccentSoftBrush");
        Map(SystemColors.HighlightTextBrush, "SelectedTextBrush");
        Map(System.Windows.Media.Brushes.Transparent, "InteractionBrush");

        void Map(Brush brush, params string[] keys)
        {
            foreach (var key in keys) dictionary[key] = brush;
        }
    }

    private static string GetSettingsPath() =>
        Path.Combine(Seein.Core.AppPaths.UserDefault.DataRoot, SettingsFileName);
}
