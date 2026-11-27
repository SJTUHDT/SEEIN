using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Seein.Pet;

namespace Seein.PetHost;

internal sealed class NativeTheme : IDisposable
{
    private static Uri Resource(string name) => new($"/{typeof(NativeTheme).Assembly.GetName().Name};component/Styles/{name}", UriKind.Relative);
    private readonly System.Windows.Application _application;
    private readonly string _settings;
    private readonly FileSystemWatcher? _watcher;
    private ResourceDictionary? _palette;

    public NativeTheme(System.Windows.Application application, string home)
    {
        _application = application;
        _settings = Path.Combine(Path.GetDirectoryName(home)!, "desktop-ui.json");
        Apply();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = Resource("Controls.xaml") });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = Resource("PetMenus.xaml") });
        var window = new Style(typeof(Window));
        window.Setters.Add(new Setter(Control.FontSizeProperty, 14d));
        window.Setters.Add(new Setter(Control.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe UI Variable Text, Microsoft YaHei UI")));
        window.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("WindowBrush")));
        window.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
        application.Resources[typeof(Window)] = window;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        var directory = Path.GetDirectoryName(_settings)!;
        if (Directory.Exists(directory))
        {
            _watcher = new FileSystemWatcher(directory, "desktop-ui.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
            _watcher.Changed += (_, _) => Refresh();
            _watcher.Created += (_, _) => Refresh();
            _watcher.Renamed += (_, _) => Refresh();
        }
    }

    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => Refresh();
    public static void Configure(Window window)
    {
        window.Style = System.Windows.Application.Current.Resources[typeof(Window)] as Style;
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        window.FontSize = 14;
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(window, System.Windows.Media.TextFormattingMode.Display);
    }
    private void Refresh() { if (!_application.Dispatcher.HasShutdownStarted) _application.Dispatcher.BeginInvoke(Apply); }

    private void Apply()
    {
        var mode = "System";
        try { mode = PetConfiguration.ReadObject(_settings).Text("theme", "System"); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        if (mode == "System")
            mode = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0 ? "Dark" : "Light";
        var palette = new ResourceDictionary { Source = Resource($"Theme.{(mode == "Dark" ? "Dark" : "Light")}.xaml") };
        if (_palette is not null) _application.Resources.MergedDictionaries.Remove(_palette);
        _palette = palette;
        _application.Resources.MergedDictionaries.Insert(0, palette);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        SystemEvents.UserPreferenceChanged -= PreferenceChanged;
    }
}
