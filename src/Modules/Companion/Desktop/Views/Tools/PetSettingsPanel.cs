using Seein.Core;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Seein.Pet;
using Seein.PetHost;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using UserControl = System.Windows.Controls.UserControl;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using FontFamily = System.Windows.Media.FontFamily;

namespace Seein.Tray;

internal sealed class PetSettingsPanel : UserControl
{
    private readonly DesktopPetRuntime _runtime;
    private readonly PetConfiguration _configuration;
    private readonly string _assets = Path.Combine(ProcessNames.ModuleDirectory("companion"), "pet-assets");
    private readonly ContentControl _body = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly StackPanel _layout = new();
    private readonly StackPanel _pets = new();

    public PetSettingsPanel(DesktopPetRuntime runtime)
    {
        _runtime = runtime;
        _configuration = new(runtime.Home, _assets);
        var navigation = new WrapPanel { Name = "PetSettingsTabs" };
        navigation.Children.Add(ActionButton("宠物", () => { ShowPets(); return Task.CompletedTask; }));
        navigation.Children.Add(ActionButton("外观、行为与语音", () =>
        {
            DetachBody();
            _body.Content = new PetPreferencesPanel(_configuration, patch => SendAsync("config", patch));
            return Task.CompletedTask;
        }));
        var root = new DockPanel();
        DockPanel.SetDock(navigation, Dock.Top);
        root.Children.Add(navigation);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_body);
        Content = root;
        ShowPets();
    }

    private Button ActionButton(string title, Func<Task> action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += async (_, _) =>
        {
            IsEnabled = false;
            _status.Text = "";
            try { await action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = ex.Message; }
            finally { IsEnabled = true; }
        };
        return button;
    }

    private async Task SendAsync(string path, JsonObject input)
    {
        await _runtime.CallAsync("request", "POST", "/dsh-pet-7340/" + path, Encoding.UTF8.GetBytes(input.ToJsonString()), "application/json");
    }

    private void ShowPets()
    {
        DetachBody();
        _layout.Children.Clear();
        _pets.Children.Clear();
        foreach (var pet in _configuration.ReadPets()) AddCard(pet);
        _layout.Children.Add(_pets);
        // Recreate after navigation so unloaded animated previews are not reused after disposal.
        _body.Content = new ScrollViewer { Content = _layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void AddCard(PetProfile pet)
    {
        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = pet.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var name = new TextBox { Text = pet.Name, Margin = new Thickness(0, 4, 0, 10) };
        card.Children.Add(new TextBlock { Text = "名称" });
        card.Children.Add(name);
        var avatar = PetAvatar.Find(_configuration, _assets, pet);
        if (avatar is not null) card.Children.Add(new PetImage(avatar) { Height = 72, HorizontalAlignment = HorizontalAlignment.Left });
        var size = new Slider { Minimum = 80, Maximum = 1000, Value = pet.Size, TickFrequency = 10, IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 0, 12) };
        card.Children.Add(new TextBlock { Text = "大小" });
        card.Children.Add(size);
        var visible = new CheckBox { Content = "在桌面显示", IsChecked = pet.Display is "both" or "desktop", Margin = new Thickness(0, 0, 0, 10) };
        var fixedPosition = new CheckBox { Content = "固定位置", IsChecked = pet.Settings.Flag("fixedEnabled"), Margin = new Thickness(0, 0, 0, 10) };
        var whisper = new CheckBox { Content = "允许碎碎念", IsChecked = pet.Settings.Flag("whisperEnabled"), Margin = new Thickness(0, 0, 0, 10) };
        card.Children.Add(visible);
        card.Children.Add(fixedPosition);
        card.Children.Add(whisper);
        var actions = new WrapPanel();
        foreach (var (label, action) in new[] { ("对话", "chat"), ("睡觉 / 叫醒", "sleep"), ("回到初始位置", "home") })
            actions.Children.Add(ActionButton(label, () => SendAsync("pet-action", new() { ["id"] = pet.Id, ["action"] = action })));
        actions.Children.Add(ActionButton("应用", async () =>
        {
            await SendAsync("pet-update", new() { ["id"] = pet.Id, ["patch"] = new JsonObject
            {
                ["name"] = name.Text, ["size"] = size.Value, ["display"] = visible.IsChecked == true ? "both" : "none",
                ["fixedEnabled"] = fixedPosition.IsChecked == true, ["whisperEnabled"] = whisper.IsChecked == true
            } });
            ShowPets();
            _status.Text = "已保存并应用。";
        }));
        actions.Children.Add(ActionButton("详细配置", () => { EditJson(pet.Name, pet.Settings, value => SendAsync("pet-update", new() { ["id"] = pet.Id, ["patch"] = value })); return Task.CompletedTask; }));
        if (pet.Entry != "main") actions.Children.Add(ActionButton("更换头像", async () => { PetAvatar.Upload(_configuration, pet, Window.GetWindow(this)); await _runtime.CallAsync("reload"); ShowPets(); }));
        actions.Children.Add(ActionButton("删除", async () =>
        {
            if (System.Windows.MessageBox.Show(Window.GetWindow(this), "删除“" + pet.Name + "”？已保存的记忆会保留。", "桌宠设置", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await SendAsync("pet-remove", new() { ["id"] = pet.Id });
            ShowPets();
        }));
        card.Children.Add(actions);
        var border = new Border { Child = card, Margin = new Thickness(0, 4, 0, 12) };
        border.SetResourceReference(StyleProperty, "CardStyle");
        _pets.Children.Add(border);
    }

    private void EditJson(string title, JsonObject original, Func<JsonObject, Task> save)
    {
        DetachBody();
        var panel = new DockPanel();
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var input = new TextBox { Text = original.ToJsonString(PetConfiguration.JsonOptions), AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ActionButton("保存并应用", async () =>
        {
            var value = JsonNode.Parse(input.Text, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? throw new InvalidDataException("配置必须为对象。");
            await save(value);
            ShowPets();
            _status.Text = "已保存并应用。";
        }));
        buttons.Children.Add(ActionButton("返回", () => { ShowPets(); return Task.CompletedTask; }));
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(input);
        _body.Content = panel;
    }

    private void DetachBody()
    {
        if (_body.Content is ScrollViewer scroll) scroll.Content = null;
        _body.Content = null;
    }
}

