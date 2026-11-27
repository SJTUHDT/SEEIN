using Seein.Core.Tools;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Seein.Tray;

// Collection settings stay ordinary forms; no JSON editing is exposed to users.
internal sealed class ToolCollectionEditor : StackPanel
{
    private readonly ToolField _field;
    private readonly JsonArray _items;
    private readonly Func<JsonNode, Task<string?>> _save;
    private readonly Action<JsonNode>? _changed;
    private TextBlock? _feedback;
    public ToolCollectionEditor(ToolField field, Func<JsonNode, Task<string?>> save,
        JsonNode? draft = null, Action<JsonNode>? changed = null)
    {
        _field = field; _items = (draft ?? field.Value)?.DeepClone() as JsonArray ?? []; _save = save; _changed = changed;
        Render();
    }
    private void Changed() => _changed?.Invoke(_items.DeepClone());
    private void Render()
    {
        Children.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            var itemIndex = index;
            var box = new StackPanel { Margin = new(0, 0, 0, 12) };
            var commands = new WrapPanel();
            foreach (var (text, offset) in new[] { ("上移", -1), ("下移", 1), ("移除", 0) })
            {
                var button = new Button { Content = text, Margin = new(0, 0, 6, 6) };
                button.IsEnabled = offset == 0 || itemIndex + offset >= 0 && itemIndex + offset < _items.Count;
                button.Click += (_, _) =>
                {
                    var value = _items[itemIndex]; _items.RemoveAt(itemIndex);
                    if (offset != 0) _items.Insert(itemIndex + offset, value);
                    Changed(); Render();
                };
                commands.Children.Add(button);
            }
            box.Children.Add(commands);
            foreach (var column in _field.Columns ?? [new("value", "内容")])
            {
                var scalar = _items[itemIndex] is not JsonObject;
                JsonNode? Read() => scalar ? _items[itemIndex] : _items[itemIndex]?[column.Key];
                void Write(JsonNode? value) { if (scalar) _items[itemIndex] = value; else _items[itemIndex]![column.Key] = value; Changed(); }
                var row = new DockPanel { Margin = new(0, 0, 0, 6) };
                var label = new TextBlock { Text = column.Label, Width = 120, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(label);
                if (column.Kind == "bool")
                {
                    var check = new CheckBox { IsChecked = Read()?.GetValue<bool>() == true, Content = "启用" };
                    check.Click += (_, _) => Write(JsonValue.Create(check.IsChecked == true)); row.Children.Add(check);
                }
                else if (column.Options is { Count: > 0 })
                {
                    var combo = new ComboBox { ItemsSource = column.Options, DisplayMemberPath = "Label", SelectedItem = column.Options.FirstOrDefault(o => JsonNode.DeepEquals(o.Value, Read())) };
                    combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ToolOption option) Write(option.Value?.DeepClone()); }; row.Children.Add(combo);
                }
                else
                {
                    var value = Read();
                    var text = new TextBox { Text = column.Kind == "lines" && value is JsonArray lines ? string.Join(Environment.NewLine, lines.Select(x => x?.GetValue<string>())) : value?.GetValue<string>() ?? "", AcceptsReturn = column.Kind == "lines", TextWrapping = TextWrapping.Wrap, MinWidth = 100 };
                    text.TextChanged += (_, _) => Write(column.Kind == "lines" ? new JsonArray(text.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) : JsonValue.Create(text.Text)); row.Children.Add(text);
                }
                box.Children.Add(row);
            }
            var border = new Border { Child = box, Padding = new(12), CornerRadius = new(6), BorderThickness = new(1), Margin = new(0, 0, 0, 8) };
            border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeBrush"); Children.Add(border);
        }
        var actions = new WrapPanel();
        var add = new Button { Content = "添加", Margin = new(0, 0, 8, 0) };
        add.Click += (_, _) =>
        {
            if (_field.Kind == "lines") _items.Add("");
            else { var value = new JsonObject(); foreach (var c in _field.Columns ?? []) value[c.Key] = c.Default?.DeepClone() ?? JsonValue.Create(""); _items.Add(value); }
            Changed(); Render();
            var last = Children.OfType<Border>().LastOrDefault();
            if (last is not null) Dispatcher.InvokeAsync(() => last.BringIntoView());
        };
        var save = new Button { Content = "保存列表" };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var value = _field.Id == "Launcher.SearchEngines" ? SearchSiteSettings.Prepare(_items) : _items.DeepClone();
                var error = await _save(value);
                if (error is not null) { _feedback!.Text = error; return; }
                _items.Clear(); foreach (var item in (JsonArray)value) _items.Add(item?.DeepClone());
                Render(); _feedback!.Text = "列表已保存并应用。";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException)
            { _feedback!.Text = ex.Message; }
            finally { save.IsEnabled = true; }
        };
        actions.Children.Add(add); actions.Children.Add(save); Children.Add(actions);
        _feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
        Children.Add(_feedback);
    }
}
