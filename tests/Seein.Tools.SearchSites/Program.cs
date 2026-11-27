using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Seein.Core.Tools;
using Seein.Tray;

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = [];
    private static ToolboxView View = null!;
    private static Window Window = null!;
    private static string ResultFile = "";
    private static string Root = "";
    private static bool Preview;
    private static bool Headless;

    [STAThread]
    public static void Main(string[] args)
    {
        Root = Path.GetFullPath(args[0]);
        Preview = args.Contains("--preview");
        Headless = args.Contains("--headless");
        Environment.SetEnvironmentVariable("SEEIN_INSTANCE", "search-sites-qa");
        Environment.SetEnvironmentVariable("SEEIN_PREVIEW_USER_DATA", Path.Combine(Root, ".build", "search-sites-ui-data" + (Preview ? "" : "-" + Guid.NewGuid().ToString("N"))));
        Environment.SetEnvironmentVariable("SEEIN_SEARCH_SITE_TEST", "1");
        ResultFile = Path.Combine(Root, ".build", "search-sites-ui-results.json");
        var app = new Application();
        foreach (var file in new[] { "Theme.Light.xaml", "Controls.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Styles", file)) });
        var runtime = typeof(ToolboxView).Assembly.GetType("Seein.Tray.ToolRuntime")!;
        ((HashSet<string>)runtime.GetField("ActiveModules", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!).Add("pane");
        View = new ToolboxView();
        var layout = new DockPanel();
        var launcher = new Button { Content = "打开隔离启动器测试", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 14) };
        launcher.Click += async (_, _) =>
        {
            var response = (Task<ToolResponse>)runtime.GetMethod("SendAsync")!.Invoke(runtime.GetProperty("Instance")!.GetValue(null),
                ["pane", "diagnostics.search-site", null, new JsonObject { ["query"] = "gh" }])!;
            await response;
        };
        DockPanel.SetDock(launcher, Dock.Top); layout.Children.Add(launcher); layout.Children.Add(View);
        Window = new Window
        {
            Title = "SEEIN · 搜索站点修复预览（隔离数据）", Width = 1140, Height = 850,
            Content = new Border { Padding = new Thickness(24), Child = layout },
            Background = (System.Windows.Media.Brush)app.Resources["WindowBrush"],
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        async Task RunAndFinish()
        {
            try { await Run(); }
            catch (Exception ex) { Results.Add(new { name = "Unexpected error", passed = false, actual = ex.ToString() }); }
            finally
            {
                if (Results.Count > 0) File.WriteAllText(ResultFile, JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
                if (Headless) { ((IDisposable)runtime.GetProperty("Instance")!.GetValue(null)!).Dispose(); app.Shutdown(); }
                else if (!Preview) Window.Close();
            }
        }
        Window.Closed += (_, _) => ((IDisposable)runtime.GetProperty("Instance")!.GetValue(null)!).Dispose();
        if (Headless)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.InvokeAsync(async () => await RunAndFinish());
            app.Run();
        }
        else { Window.Loaded += async (_, _) => await RunAndFinish(); app.Run(Window); }
    }
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    private static IEnumerable<T> Walk<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T value) yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var descendant in Walk<T>(child)) yield return descendant;
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException("设置页面未完成操作。");
    }
    private static void Check(string name, bool ok, object? actual = null)
    {
        Results.Add(new { name, passed = ok, actual });
        if (!ok) throw new InvalidOperationException(name);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Tab(string id) => Walk<RadioButton>(View).Single(b => Equals(b.Tag, id)).IsChecked = true;
    private static StackPanel Sites() => Walk<StackPanel>(View).Single(p => p.GetType().Name == "ToolCollectionEditor" && Get<ToolField>(p, "_field").Id == "Launcher.SearchEngines");
    private static TextBox[] LastInputs() => Walk<TextBox>(Sites().Children.OfType<Border>().Last()).ToArray();
    private static TextBlock Feedback() => Sites().Children.OfType<TextBlock>().Last();
    private static void SiteButton(string content) => Click(Walk<Button>(Sites()).Single(b => Equals(b.Content, content)));
    private static async Task Run()
    {
        View.Open("pane");
        var firstGeneration = Get<int>(View, "_generation"); View.Open("pane");
        Check("Repeated opens share the pending initial settings read", Get<int>(View, "_generation") == firstGeneration);
        await Until(() => Get<IReadOnlyList<ToolField>>(View, "_fields").Count > 0);
        if (Preview)
        {
            Tab("launcher");
            await Task.Delay(100);
            Sites().Children.OfType<Border>().Last().BringIntoView();
            Window.Activate();
            return;
        }
        var runtime = typeof(ToolboxView).Assembly.GetType("Seein.Tray.ToolRuntime")!;
        var active = (HashSet<string>)runtime.GetField("ActiveModules", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var colorPanel = Walk<DockPanel>(View).Single(p => System.Windows.Automation.AutomationProperties.GetName(p) == "强调色");
        var color = Walk<TextBox>(colorPanel).Single(); color.Text = "#112233";
        var tabBefore = Walk<RadioButton>(View).First();
        Tab("launcher"); Tab("general");
        Check("Returning to a category preserves its controls and unsaved text", ReferenceEquals(color, Walk<TextBox>(View).First(t => t.Text == "#112233")));
        View.Open("pane");
        Check("Warm page opening preserves controls and navigation tabs", ReferenceEquals(tabBefore, Walk<RadioButton>(View).First()) && color.Text == "#112233");
        var version = Get<int>(View, "_generation"); var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) View.Open("pane");
        timer.Stop();
        Check("100 warm opens neither rebuild nor start another read", Get<int>(View, "_generation") == version && ReferenceEquals(tabBefore, Walk<RadioButton>(View).First()), new { averageMilliseconds = timer.Elapsed.TotalMilliseconds / 100 });
        var combo = Walk<ComboBox>(View).First(c => System.Windows.Automation.AutomationProperties.GetName(c) == "界面主题");
        var acceptedOption = combo.SelectedItem;
        active.Remove("pane");
        try
        {
            combo.SelectedIndex = (combo.SelectedIndex + 1) % combo.Items.Count;
            await Task.Delay(100);
            Check("Failed dropdown save restores accepted selection", Equals(combo.SelectedItem, acceptedOption));
        }
        finally { active.Add("pane"); }
        Tab("launcher");
        var check = Walk<CheckBox>(View).First(c => System.Windows.Automation.AutomationProperties.GetName(c) == "失去焦点后隐藏");
        var savedCheck = !check.IsChecked;
        check.IsChecked = savedCheck; check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Get<IReadOnlyList<ToolField>>(View, "_fields").First(f => f.Id == "Launcher.HideOnBlur").Value!.GetValue<bool>() == savedCheck);
        active.Remove("pane");
        try
        {
            check.IsChecked = !savedCheck; check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            Check("Failed toggle save restores latest accepted value", check.IsChecked == savedCheck);
        }
        finally { active.Add("pane"); }
        var original = Sites().Children.OfType<Border>().Count();
        SiteButton("添加");
        var inputs = LastInputs();
        inputs[0].Text = "UI 测试站点"; inputs[1].Text = "uix"; inputs[2].Text = "example.com/find?q=%s";
        Tab("general"); Tab("launcher");
        Check("Unsaved site survives tab switching", LastInputs()[1].Text == "uix");
        SiteButton("保存列表");
        await Until(() => LastInputs()[2].Text == "https://example.com/find?q={query}");
        Check("Save normalizes template and shows success", LastInputs()[2].Text == "https://example.com/find?q={query}", Feedback().Text);
        Tab("general"); Tab("launcher");
        Check("Saved list survives page re-render", Sites().Children.OfType<Border>().Count() == original + 1 && LastInputs()[1].Text == "uix");
        var reload = (Task)typeof(ToolboxView).GetMethod("LoadSettingsAsync", Private)!.Invoke(View, null)!;
        await reload;
        Check("Saved site re-read from native settings", LastInputs()[1].Text == "uix");
        SiteButton("添加"); inputs = LastInputs();
        inputs[0].Text = "未完成站点"; inputs[1].Text = "incomplete"; inputs[2].Text = "https://example.com/";
        SiteButton("保存列表");
        await Until(() => Feedback().Text.Contains("{query}"));
        Check("Invalid URL shows inline error and retains draft", LastInputs()[1].Text == "incomplete", Feedback().Text);
        Tab("general"); Tab("launcher");
        Check("Failed save retains draft across tabs", LastInputs()[1].Text == "incomplete");
        Click(Walk<Button>(Sites().Children.OfType<Border>().Last()).Single(b => Equals(b.Content, "移除")));
        SiteButton("保存列表"); await Until(() => Get<TextBlock>(View, "Feedback").Text.Contains("已保存"));
        var runtimeInstance = runtime.GetProperty("Instance")!.GetValue(null)!;
        var reads = runtime.GetMethod("GetSettingsAsync")!;
        var send = runtime.GetMethod("SendAsync")!;
        var cachedSettings = runtime.GetMethod("CachedSettings")!;
        for (var i = 0; i < 8; i++)
        {
            var oldRead = (Task<ToolResponse>)reads.Invoke(runtimeInstance, ["pane"])!;
            var expected = i % 2 == 0;
            var save = (Task<ToolResponse>)send.Invoke(runtimeInstance, ["pane", "settings.apply", null, new JsonObject { ["Clipboard.Dedupe"] = expected }])!;
            await Task.WhenAll(oldRead, save);
            var snapshot = (IReadOnlyList<ToolField>)cachedSettings.Invoke(runtimeInstance, ["pane"])!;
            Check("Concurrent read cannot overwrite a newer saved snapshot " + i, save.Result.Success && snapshot.Single(f => f.Id == "Clipboard.Dedupe").Value!.GetValue<bool>() == expected);
        }
        Tab("capabilities");
        var clipboard = Walk<CheckBox>(View).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "剪贴板");
        clipboard.IsChecked = false; clipboard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !Walk<RadioButton>(View).Any(t => Equals(t.Tag, "clipboard")));
        Check("Capability changes refresh available categories without stale cached tabs", !Walk<RadioButton>(View).Any(t => Equals(t.Tag, "clipboard")));
        clipboard.IsChecked = true; clipboard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Walk<RadioButton>(View).Any(t => Equals(t.Tag, "clipboard")));
        Tab("launcher");
        var shortcuts = new ShortcutSettingsView
        {
            GetItems = () => [],
            CaptureCommand = (_, _, _) => Task.FromResult(new ToolResponse("local", true, Fields: []))
        };
        shortcuts.Refresh(); var shortcutBody = Get<StackPanel>(shortcuts, "_body"); var shortcutIntro = shortcutBody.Children[0];
        shortcuts.Refresh();
        Check("Shortcut settings reuse their loaded body", ReferenceEquals(shortcutIntro, shortcutBody.Children[0]));
        var apps = new AppListView(); apps.Load(new Seein.Core.AppCatalogResult([]));
        var catalog = Get<Border>(apps, "CatalogContent"); apps.BeginLoad();
        Check("Application list remains visible while refreshing", catalog.Visibility == Visibility.Visible);
        apps.ShowLoadError("test connection unavailable");
        Check("Application refresh failure preserves content and shows status", catalog.Visibility == Visibility.Visible && Get<TextBlock>(apps, "StatusText").Visibility == Visibility.Visible);
        using (var display = new DisplayControlView())
        {
            var show = typeof(DisplayControlView).GetMethod("ShowSettings", Private)!;
            var setting = new Seein.Core.Displays.DisplaySetting(Seein.Core.Displays.DisplayFeature.Brightness, "亮度", true, 40);
            show.Invoke(display, ["test-monitor", new[] { setting }]);
            var panel = Get<Panel>(display, "SettingsPanel"); var control = panel.Children[1];
            show.Invoke(display, ["test-monitor", new[] { setting with { Current = 60 } }]);
            Check("Monitor refresh updates values without replacing controls", ReferenceEquals(control, panel.Children[1]) && Walk<Slider>(panel).Single().Value == 60);
            var pending = Get<Dictionary<Seein.Core.Displays.DisplayFeature, uint>>(display, "_pending");
            Check("Monitor snapshot updates do not queue hardware writes", pending.Count == 0);
        }
        if (Headless) Check("Headless checks never show a window", !Window.IsVisible && !Window.IsLoaded);
        // The preview shows the real site settings page with isolated test data.
        var scroll = Get<ScrollViewer>(View, "SettingsScroll");
        scroll.ScrollToBottom();
        if (!Headless) Window.Activate();
    }
}
