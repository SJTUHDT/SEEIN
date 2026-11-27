using Seein.Core.Tools;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Seein.Tray;

public partial class ToolboxView : System.Windows.Controls.UserControl
{
    private string? _module;
    private int _generation;
    private IReadOnlyList<ToolField> _fields = [];
    private readonly Dictionary<string, string> _selected = [];
    private readonly Dictionary<(string Module, string Field), JsonNode> _drafts = [];
    private readonly Dictionary<(string Module, string Field), string> _textDrafts = [];
    private readonly Dictionary<(string Module, string Category), (string Stamp, UIElement[] Body)> _categoryViews = [];
    private readonly Dictionary<(string Module, string Category), double> _scrollPositions = [];
    private readonly string _tabGroup = "ToolSettings_" + Guid.NewGuid().ToString("N");
    private string? _renderedCategory;
    private string? _tabsStamp;
    private bool _updatingTabs;
    private Task? _loadTask;
    private DateTimeOffset _lastRead;
    public ToolboxView() => InitializeComponent();
    public void Open(string module, bool settings = false)
    {
        if (_module != module)
        {
            ++_generation; _loadTask = null; _lastRead = default;
            _module = module; _fields = ToolRuntime.Instance.CachedSettings(module) ?? [];
            _selected.TryAdd(module, "general"); _renderedCategory = null;
            Render();
        }
        if (_loadTask is { IsCompleted: false }) return;
        if (_fields.Count > 0 && DateTimeOffset.UtcNow - _lastRead < TimeSpan.FromSeconds(15)) return;
        _loadTask = RefreshSettingsAsync(quiet: _fields.Count > 0);
    }
    private Task LoadSettingsAsync() => RefreshSettingsAsync(quiet: _fields.Count > 0);
    private async Task RefreshSettingsAsync(bool quiet)
    {
        var module = _module; var generation = ++_generation;
        if (module is null) return;
        if (!quiet) Feedback.Text = "正在准备设置…";
        var response = await ToolRuntime.Instance.GetSettingsAsync(module);
        if (generation != _generation || module != _module) return;
        if (!response.Success) { Feedback.Text = quiet ? "当前显示上次读取的设置。" + response.Error : response.Error; return; }
        _lastRead = DateTimeOffset.UtcNow;
        var fields = response.Fields ?? [];
        if (JsonSerializer.Serialize(_fields, ToolCatalog.Json) != JsonSerializer.Serialize(fields, ToolCatalog.Json))
        { _fields = fields; Render(); }
        Feedback.Text = "更改后立即生效。全局快捷键在「应用设置 → 快捷键管理」中设置。";
    }
    private string CategoryStamp(string module, string category) => JsonSerializer.Serialize(_fields.Where(f => ToolSettingsNavigation.CategoryOf(module, f.Id) == category), ToolCatalog.Json);
    private string CategoryShape(string module, string category) => JsonSerializer.Serialize(_fields.Where(f => ToolSettingsNavigation.CategoryOf(module, f.Id) == category).Select(f => f with { Value = null }), ToolCatalog.Json);
    private void RememberBody(string module, string category) => _categoryViews[(module, category)] = (CategoryStamp(module, category), SettingFields.Children.Cast<UIElement>().ToArray());
    private bool CategoryEnabled(string id)
    {
        var capability = id switch { "launcher" => "launcher", "clipboard" => "clipboard", "folders" => "folder-jump", _ => null };
        return _module != "pane" || capability is null ||
            _fields.FirstOrDefault(f => f.Id == "capability." + capability)?.Value?.GetValue<bool>() != false;
    }
    private void Render()
    {
        if (_module is not {} module) return;
        var categories = ToolSettingsNavigation.Categories(module).Where(c => CategoryEnabled(c.Id)).ToArray();
        if (!categories.Any(c => c.Id == _selected[module])) _selected[module] = categories[0].Id;
        if (_renderedCategory is {} previous) _scrollPositions[(module, previous)] = SettingsScroll.VerticalOffset;
        var tabsStamp = module + ":" + string.Join('|', categories.Select(c => c.Id));
        _updatingTabs = true;
        if (_tabsStamp != tabsStamp)
        {
            CategoryTabs.Children.Clear();
            foreach (var category in categories)
            {
                var tab = new RadioButton { Content = category.Name, Tag = category.Id, GroupName = _tabGroup, Style = (Style)FindResource("SegmentedButtonStyle"), IsChecked = _selected[module] == category.Id };
                tab.Checked += (_, _) => { if (_updatingTabs) return; _selected[module] = category.Id; Render(); };
                CategoryTabs.Children.Add(tab);
            }
            _tabsStamp = tabsStamp;
            AnimatedReorder.Attach(CategoryTabs, "tools:" + module + ":categories");
        }
        foreach (var tab in CategoryTabs.Children.OfType<RadioButton>()) tab.IsChecked = Equals(tab.Tag, _selected[module]);
        _updatingTabs = false;
        var current = categories.First(c => c.Id == _selected[module]);
        _renderedCategory = current.Id;
        SettingFields.Children.Clear();
        PageTitle.Text = current.Name; PageDescription.Text = current.Description;
        if (_categoryViews.TryGetValue((module, current.Id), out var cached) && cached.Stamp == CategoryStamp(module, current.Id))
        {
            foreach (var element in cached.Body) SettingFields.Children.Add(element);
            SettingsScroll.ScrollToVerticalOffset(_scrollPositions.GetValueOrDefault((module, current.Id)));
            return;
        }
        if (current.Id == "about")
        {
            var text = new TextBlock { Text = module == "pane" ? "Pane 0.1.606\n\n已整合启动器、剪贴板、文件对话框跳转、批量重命名和定时关机。\n启动与托盘由 SEEIN 统一管理。" : "Snow Shot\n\n截图、录屏、离线 OCR、截图翻译和图片转 PDF。\n截图与录制界面保留工具原有设计，设置与快捷键由 SEEIN 统一管理。", TextWrapping = TextWrapping.Wrap };
            SettingFields.Children.Add(Card(text)); RememberBody(module, current.Id); return;
        }
        var fields = _fields.Where(f => ToolSettingsNavigation.CategoryOf(module, f.Id) == current.Id).ToArray();
        if (current.Id == "capabilities")
        {
            var grid = new UniformGrid { Columns = 2 };
            foreach (var field in fields) grid.Children.Add(CapabilityCard(module, field));
            SettingFields.Children.Add(grid); RememberBody(module, current.Id); return;
        }
        foreach (var section in fields.GroupBy(f => f.Section))
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = section.Key == "Screenshot translation" ? "截图翻译服务" : section.Key, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 16) });
            foreach (var field in section)
            {
                var row = new Grid { Margin = new(0, 0, 0, 14) };
                var collection = field.Kind is "collection" or "lines";
                row.ColumnDefinitions.Add(new() { Width = new GridLength(collection ? 190 : 1, collection ? GridUnitType.Pixel : GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var labels = new StackPanel { Margin = new(0, 3, 24, 0), VerticalAlignment = VerticalAlignment.Center };
                labels.Children.Add(new TextBlock { Text = Label(field), TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrWhiteSpace(field.Description))
                {
                    var description = new TextBlock { Text = field.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 5, 0, 0) };
                    description.SetResourceReference(ForegroundProperty, "MutedTextBrush"); labels.Children.Add(description);
                }
                row.Children.Add(labels); var editor = Editor(module, field);
                AutomationProperties.SetName(editor, Label(field));
                Grid.SetColumn(editor, 1); row.Children.Add(editor); body.Children.Add(row);
            }
            SettingFields.Children.Add(Card(body));
        }
        RememberBody(module, current.Id);
        SettingsScroll.ScrollToVerticalOffset(_scrollPositions.GetValueOrDefault((module, current.Id)));
    }
    private static string Label(ToolField field) => field.Id switch
    { "seein.translation.baseUrl" => "翻译 API 地址（OpenAI 兼容）", "seein.translation.model" => "翻译模型名称", "seein.translation.apiKey" => "翻译 API 密钥", _ => field.Label };
    private Border CapabilityCard(string module, ToolField field)
    {
        var body = new StackPanel(); var head = new DockPanel();
        var toggle = Toggle(module, field); DockPanel.SetDock(toggle, Dock.Right); head.Children.Add(toggle);
        var icon = field.Id switch { "capability.launcher" => "\uE721", "capability.clipboard" => "\uE8C8", "capability.folder-jump" => "\uE8B7", "capability.rename" => "\uE8AC", _ => "\uE7E8" };
        var glyph = new TextBlock { Text = icon, FontFamily = new("Segoe Fluent Icons"), FontSize = 22, Margin = new(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(glyph, Dock.Left); head.Children.Add(glyph);
        head.Children.Add(new TextBlock { Text = field.Label, FontWeight = FontWeights.SemiBold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head);
        var description = new TextBlock { Text = field.Description, TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 4), MinHeight = 42 };
        description.SetResourceReference(ForegroundProperty, "MutedTextBrush"); body.Children.Add(description);
        var card = Card(body); card.Margin = new(0, 0, 12, 12); return card;
    }
    private CheckBox Toggle(string module, ToolField field)
    {
        var box = new CheckBox { Style = (Style)FindResource("ToolToggleStyle"), IsChecked = field.Value?.GetValue<bool>() == true, HorizontalAlignment = HorizontalAlignment.Right };
        var accepted = box.IsChecked;
        AutomationProperties.SetName(box, field.Label);
        box.Click += async (_, _) =>
        {
            box.IsEnabled = false;
            var success = await ApplyAsync(module, field.Id, JsonValue.Create(box.IsChecked == true));
            if (success) accepted = box.IsChecked;
            else box.IsChecked = accepted;
            box.IsEnabled = true;
            if (field.Kind == "capability") { Render(); await LoadSettingsAsync(); }
        };
        return box;
    }
    private FrameworkElement Editor(string module, ToolField field)
    {
        if (field.Kind is "collection" or "lines") return new ToolCollectionEditor(field, async value =>
        {
            if (field.Id == "Launcher.SearchEngines")
                value = SearchSiteSettings.Prepare(value, _fields.FirstOrDefault(f => f.Id == "Launcher.DefaultEngine")?.Value?.GetValue<string>());
            if (!await ApplyAsync(module, field.Id, value)) return Feedback.Text;
            _drafts.Remove((module, field.Id));
            return null;
        }, _drafts.GetValueOrDefault((module, field.Id)), value => _drafts[(module, field.Id)] = value);
        if (field.Kind == "bool") return Toggle(module, field);
        if (field.Options is { Count: > 0 })
        {
            var box = new ComboBox { ItemsSource = field.Options, DisplayMemberPath = "Label", SelectedItem = field.Options.FirstOrDefault(o => JsonNode.DeepEquals(o.Value, field.Value)), VerticalAlignment = VerticalAlignment.Center };
            var accepted = box.SelectedItem; var restoring = false;
            box.SelectionChanged += async (_, _) =>
            {
                if (restoring || box.SelectedItem is not ToolOption option) return;
                box.IsEnabled = false;
                try
                {
                    if (await ApplyAsync(module, field.Id, option.Value?.DeepClone())) accepted = option;
                    else { restoring = true; box.SelectedItem = accepted; restoring = false; }
                }
                finally { box.IsEnabled = true; }
            };
            return box;
        }
        var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var save = new Button { Content = "应用", Margin = new(8, 0, 0, 0) }; DockPanel.SetDock(save, Dock.Right); panel.Children.Add(save);
        if (field.Kind == "secret")
        {
            var box = new PasswordBox { MinWidth = 100 };
            if (_textDrafts.TryGetValue((module, field.Id), out var password)) box.Password = password;
            box.PasswordChanged += (_, _) => _textDrafts[(module, field.Id)] = box.Password;
            save.Click += async (_, _) => { if (await ApplyAsync(module, field.Id, JsonValue.Create(box.Password))) { box.Clear(); _textDrafts.Remove((module, field.Id)); } }; panel.Children.Add(box);
        }
        else
        {
            var box = new TextBox { Text = field.Kind == "number" && field.Value is JsonValue numeric && numeric.TryGetValue<double>(out var numberValue) ? numberValue.ToString("0.####") : field.Value is JsonValue value && value.TryGetValue<string>(out var str) ? str : field.Value?.ToJsonString() ?? "", MinWidth = 100 };
            if (_textDrafts.TryGetValue((module, field.Id), out var draft)) box.Text = draft;
            box.TextChanged += (_, _) => _textDrafts[(module, field.Id)] = box.Text;
            save.Click += async (_, _) =>
            {
                JsonNode? value;
                if (field.Kind == "number")
                {
                    if (!double.TryParse(box.Text, out var number) || !double.IsFinite(number) || field.Minimum is {} min && number < min || field.Maximum is {} max && number > max) { Feedback.Text = "请输入有效范围内的数值。"; return; }
                    value = JsonValue.Create(number);
                }
                else value = JsonValue.Create(box.Text);
                await ApplyAsync(module, field.Id, value);
            };
            if (field.Id.StartsWith("General.Glass"))
            {
                var controls = new Grid();
                controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                controls.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                box.Width = 76; box.MinWidth = 0; box.Margin = new(0, 0, 14, 0);
                controls.Children.Add(box);
                var slider = new Slider { Minimum = field.Minimum ?? 0, Maximum = field.Maximum ?? 1, Value = field.Value?.GetValue<double>() ?? 0, VerticalAlignment = VerticalAlignment.Center, SmallChange = 0.01 };
                AutomationProperties.SetName(slider, field.Label);
                slider.ValueChanged += (_, _) => box.Text = slider.Value.ToString("0.####");
                Grid.SetColumn(slider, 1); controls.Children.Add(slider); panel.Children.Add(controls);
            }
            else panel.Children.Add(box);
        }
        return panel;
    }
    private static Border Card(UIElement body)
    {
        var card = new Border { Child = body, Padding = new(20), Margin = new(0, 0, 0, 14), CornerRadius = new(8), BorderThickness = new(1) };
        card.SetResourceReference(BackgroundProperty, "PanelBrush"); card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeBrush"); return card;
    }
    private async Task<bool> ApplyAsync(string module, string id, JsonNode? value)
    {
        ++_generation;
        var response = await ToolRuntime.Instance.SendAsync(module, "settings.apply", values: new JsonObject { [id] = value });
        if (module == _module)
        {
            if (response.Success)
            {
                var previousShape = _renderedCategory is {} oldCategory ? CategoryShape(module, oldCategory) : null;
                _fields = response.Fields ?? _fields.Select(field => field.Id == id ? field with { Value = value?.DeepClone() } : field).ToArray();
                _textDrafts.Remove((module, id)); _lastRead = DateTimeOffset.UtcNow;
                if (_renderedCategory is {} current)
                {
                    if (previousShape == CategoryShape(module, current)) RememberBody(module, current);
                    else
                    {
                        _categoryViews.Remove((module, current));
                        Dispatcher.InvokeAsync(() => { if (_module == module && _renderedCategory == current) Render(); }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            }
            Feedback.Text = response.Success ? "设置已保存并应用" : response.Error;
        }
        return response.Success;
    }
}
