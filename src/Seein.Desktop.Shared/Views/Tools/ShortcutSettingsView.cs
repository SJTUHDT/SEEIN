using Seein.Core.Tools;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
namespace Seein.Tray;

public sealed class ShortcutSettingsView : System.Windows.Controls.UserControl
{
    private readonly StackPanel _body = new();
    private int _generation;
    private string? _bindingsStamp;
    private bool _localReady;
    private Button? _recording;
    public static bool IsRecording { get; private set; }
    private static event Action? BindingsChanged;
    public static void NotifyBindingsChanged() => BindingsChanged?.Invoke();
    public Func<IEnumerable<ShortcutItem>>? GetItems { get; set; }
    public Func<ShortcutBinding, (bool Success, string Message)>? SaveBinding { get; set; }
    public Func<string, string, JsonObject?, Task<ToolResponse>>? CaptureCommand { get; set; }
    public ShortcutSettingsView()
    {
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { _generation++; StopRecording(); } };
        Loaded += (_, _) => BindingsChanged += RefreshBindings;
        Unloaded += (_, _) => { BindingsChanged -= RefreshBindings; _generation++; StopRecording(); };
    }
    private void RefreshBindings() => Dispatcher.BeginInvoke(() => { if (IsVisible && !IsRecording) Refresh(); });
    public void Refresh()
    {
        var items = (GetItems?.Invoke() ?? []).ToArray();
        var stamp = System.Text.Json.JsonSerializer.Serialize(items);
        if (_bindingsStamp == stamp && (CaptureCommand is null || _localReady)) return;
        _bindingsStamp = stamp; _localReady = false;
        StopRecording(); var generation = ++_generation; _body.Children.Clear();
        var intro = new TextBlock { Text = "按功能管理快捷键，更改后自动保存。双击右 Alt 可暂停或恢复截图快捷键，原有组合键会保留。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); _body.Children.Add(intro);
        foreach (var group in items.GroupBy(i => i.Group))
        {
            var body = Card(GroupName(group.Key), "全局快捷键");
            foreach (var item in group) AddGlobalRow(body, item);
        }
        if (CaptureCommand is not null)
        {
            var local = Card("截图与录屏 · 窗口内操作", "仅在截图、标注或录屏窗口中生效");
            var status = new TextBlock { Text = "正在读取…", Margin = new(0, 8, 0, 0) }; local.Children.Add(status);
            _ = LoadLocalAsync(local, status, generation);
        }
        if (_body.Children.Count == 1) _body.Children.Add(new TextBlock { Text = "启用功能插件后，可在这里统一管理对应快捷键。" });
    }
    private static string GroupName(string group) => group switch
    { "snow" => "截图与录屏", "pane" => "效率工具", "SEEIN" => "SEEIN 与桌宠工作台", "显示器" => "设备控制 · 显示器", "米家挂灯" => "设备控制 · 米家挂灯", _ => group };
    private StackPanel Card(string title, string subtitle)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
        var caption = new TextBlock { Text = subtitle, FontSize = 12, Margin = new(0, 4, 0, 16) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); body.Children.Add(caption);
        var card = new Border { Child = body, Padding = new(20), Margin = new(0, 0, 0, 14), CornerRadius = new(8), BorderThickness = new(1) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeBrush");
        _body.Children.Add(card); return body;
    }
    private (Grid Row, TextBlock Feedback) Row(StackPanel body, string name)
    {
        var row = new Grid { Margin = new(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(300) });
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
        label.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap });
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed, Margin = new(0, 4, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(feedback, System.Windows.Automation.AutomationLiveSetting.Polite);
        label.Children.Add(feedback); row.Children.Add(label); body.Children.Add(row); return (row, feedback);
    }
    private void AddGlobalRow(StackPanel body, ShortcutItem item)
    {
        var (row, feedback) = Row(body, item.Name);
        var current = item.Binding;
        var editor = BindingButton(item.Name, () => Format(current), async (key, mods) =>
        {
            var binding = key is Key.Back or Key.Delete ? new ShortcutBinding(item.Id, Enabled: false) :
                new(item.Id, Modifiers(mods), (uint)KeyInterop.VirtualKeyFromKey(key));
            var result = Save(binding); if (result.Success) current = binding;
            Feedback(feedback, result); await Task.CompletedTask;
        });
        if (item.Id is "pane.clipboard" or CaptureShortcutGate.ToggleAction)
        {
            var right = item.Id == CaptureShortcutGate.ToggleAction;
            var container = new Grid(); container.ColumnDefinitions.Add(new() { Width = new GridLength(135) }); container.ColumnDefinitions.Add(new());
            var mode = new System.Windows.Controls.ComboBox { ItemsSource = new[] { "按键", right ? "双击右 Alt" : "双击左 Alt" }, SelectedIndex = current.DoubleAlt || current.DoubleRightAlt ? 1 : 0, Margin = new(0, 0, 8, 0) };
            System.Windows.Automation.AutomationProperties.SetName(mode, right ? "截图快捷键启停方式" : "剪贴板历史呼出方式");
            editor.IsEnabled = !current.DoubleAlt && !current.DoubleRightAlt;
            var updating = false;
            mode.SelectionChanged += (_, _) =>
            {
                if (updating) return;
                var candidate = mode.SelectedIndex == 1 ? new ShortcutBinding(item.Id, DoubleAlt: !right) { DoubleRightAlt = right } : new(item.Id, Enabled: false);
                var result = Save(candidate); Feedback(feedback, result);
                if (result.Success) current = candidate;
                else { updating = true; mode.SelectedIndex = current.DoubleAlt || current.DoubleRightAlt ? 1 : 0; updating = false; }
                editor.IsEnabled = !current.DoubleAlt && !current.DoubleRightAlt; editor.Content = Format(current);
            };
            container.Children.Add(mode); Grid.SetColumn(editor, 1); container.Children.Add(editor); Grid.SetColumn(container, 1); row.Children.Add(container);
        }
        else { Grid.SetColumn(editor, 1); row.Children.Add(editor); }
    }
    private (bool Success, string Message) Save(ShortcutBinding binding)
    {
        try
        {
            ShortcutValidation.Validate((GetItems?.Invoke() ?? []).Where(i => i.Id != binding.ActionId).Select(i => i.Binding).Append(binding));
            return SaveBinding?.Invoke(binding) ?? (false, "快捷键尚未初始化。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException) { return (false, ex.Message); }
    }
    private static void Feedback(TextBlock label, (bool Success, string Message) result)
    {
        label.Visibility = Visibility.Visible; label.Text = result.Success ? "已保存" : result.Message;
        label.SetResourceReference(TextBlock.ForegroundProperty, result.Success ? "MutedTextBrush" : "DangerBrush");
    }
    private Button BindingButton(string name, Func<string> text, Func<Key, ModifierKeys, Task> save)
    {
        var button = new Button { Content = text(), MinHeight = 34, ToolTip = "选择后按下快捷键；Esc 取消，Delete 清除", HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(button, name + "快捷键");
        button.Click += (_, _) => { StopRecording(); _recording = button; IsRecording = true; button.Content = "按下快捷键…"; button.Focus(); };
        button.LostKeyboardFocus += (_, _) => { if (_recording == button) { StopRecording(); button.Content = text(); } };
        button.PreviewKeyDown += async (_, e) =>
        {
            if (_recording != button) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) { StopRecording(); button.Content = text(); return; }
            e.Handled = true;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if (key == Key.Escape) { StopRecording(); button.Content = text(); return; }
            if (e.IsRepeat) return;
            _recording = null; button.IsEnabled = false;
            try { await save(key, Keyboard.Modifiers); }
            catch (Exception ex) { button.ToolTip = ex.Message; }
            finally { button.Content = text(); button.IsEnabled = true; button.Focus(); }
        };
        button.AddHandler(CancelRecordingEvent, new RoutedEventHandler((_, _) => button.Content = text()));
        button.PreviewKeyUp += (_, _) => { if (_recording is null) IsRecording = false; };
        return button;
    }
    private void StopRecording() { if (_recording is not null) _recording.RaiseEvent(new RoutedEventArgs(CancelRecordingEvent)); _recording = null; IsRecording = false; }
    private static readonly RoutedEvent CancelRecordingEvent = EventManager.RegisterRoutedEvent("CancelRecording", RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(ShortcutSettingsView));
    private async Task LoadLocalAsync(StackPanel body, TextBlock status, int generation)
    {
        try
        {
            var response = await CaptureCommand!("shortcuts.get", "", null);
            if (generation != _generation) return;
            if (!response.Success) { status.Text = response.Error ?? "暂时无法读取"; return; }
            body.Children.Remove(status);
            foreach (var group in (response.Fields ?? []).GroupBy(f => f.Section))
            {
                body.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new(0, 8, 0, 10) });
                foreach (var field in group)
                {
                    var (row, feedback) = Row(body, field.Label);
                    var current = string.Join(" / ", (field.Value as JsonArray ?? []).Select(v => v?.GetValue<string>()));
                    var button = BindingButton(field.Label, () => current.Length == 0 ? "未设置" : current, async (key, mods) =>
                    {
                        var candidate = key is Key.Back or Key.Delete ? "" : PortableKey(key, mods);
                        var result = await CaptureCommand!("shortcuts.apply", "", new JsonObject { [field.Id] = candidate.Length == 0 ? new JsonArray() : new JsonArray(candidate) });
                        if (result.Success) current = candidate;
                        Feedback(feedback, (result.Success, result.Error ?? "保存失败，原绑定已保留。"));
                    });
                    Grid.SetColumn(button, 1); row.Children.Add(button);
                }
            }
            _localReady = true;
        }
        catch (Exception ex) { if (generation == _generation) status.Text = "读取失败：" + ex.Message; }
    }
    private static uint Modifiers(ModifierKeys modifier) => (modifier.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (modifier.HasFlag(ModifierKeys.Control) ? 2u : 0) | (modifier.HasFlag(ModifierKeys.Shift) ? 4u : 0) | (modifier.HasFlag(ModifierKeys.Windows) ? 8u : 0);
    private static string PortableKey(Key key, ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl"); if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift"); if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Meta");
        parts.Add(key switch {
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((int)key - (int)Key.NumPad0).ToString(),
            Key.Return => "Return", Key.Escape => "Esc", Key.Space => "Space",
            Key.OemPlus => "+", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".",
            Key.OemQuestion => "/", Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemPipe => "\\", Key.OemTilde => "`",
            Key.PageUp => "PgUp", Key.PageDown => "PgDown", Key.Insert => "Ins", _ => key.ToString() });
        return string.Join("+", parts);
    }
    private static string Format(ShortcutBinding binding)
    {
        if (!binding.Enabled || binding.Empty) return "未设置";
        if (binding.DoubleAlt) return "左 Alt × 2";
        if (binding.DoubleRightAlt) return "右 Alt × 2";
        var parts = new List<string>();
        if ((binding.Modifiers & 2) != 0) parts.Add("Ctrl"); if ((binding.Modifiers & 1) != 0) parts.Add("Alt");
        if ((binding.Modifiers & 4) != 0) parts.Add("Shift"); if ((binding.Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(KeyInterop.KeyFromVirtualKey((int)binding.Key).ToString()); return string.Join(" + ", parts);
    }
}
