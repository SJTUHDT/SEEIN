using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Seein.Pet;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Seein.Tray;

internal sealed class PetPreferencesPanel : ScrollViewer
{
    public PetPreferencesPanel(PetConfiguration configuration, Func<JsonObject, Task> saveConfiguration)
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var panel = new StackPanel { Margin = new Thickness(12) };
        Content = panel;
        var config = configuration.ReadEntries()["main"]!.AsObject();
        var saves = new List<Action<JsonObject>>();
        void Title(string text) => panel.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 8) });
        JsonObject Parent(JsonObject root, string group)
        {
            if (group.Length == 0) return root;
            if (root[group] is not JsonObject) root[group] = config[group]?.DeepClone() ?? new JsonObject();
            return root[group]!.AsObject();
        }
        void Text(string group, string field, string label, string fallback = "", bool numeric = false)
        {
            var existing = group.Length == 0 ? config : config[group];
            var input = new TextBox { Text = existing?[field]?.ToString() ?? fallback, Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            saves.Add(patch =>
            {
                var parent = Parent(patch, group);
                if (numeric)
                {
                    if (!double.TryParse(input.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0 || value > 2147483)
                        throw new InvalidOperationException(label + "应为有效的非负数字。");
                    parent[field] = value;
                }
                else parent[field] = input.Text.Trim();
            });
        }
        void Flag(string group, string field, string label, bool fallback = false)
        {
            var existing = group.Length == 0 ? config : config[group];
            var input = new CheckBox { Content = label, IsChecked = existing?.Flag(field, fallback) ?? fallback, Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(input);
            saves.Add(patch => Parent(patch, group)[field] = input.IsChecked == true);
        }
        void Choice(string group, string field, string label, (string Id, string Name)[] choices)
        {
            var current = (group.Length == 0 ? config : config[group])?.Text(field);
            var input = new ComboBox { ItemsSource = choices.Select(c => c.Name).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => c.Id == current)), Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(new TextBlock { Text = label });
            panel.Children.Add(input);
            saves.Add(patch => Parent(patch, group)[field] = choices[input.SelectedIndex].Id);
        }
        Title("外观");
        Choice("font", "family", "字体", [("candy", "上首软糖体"), ("yahei", "微软雅黑"), ("youyuan", "幼圆"), ("kaiti", "楷体"), ("simsun", "宋体"), ("comic", "Comic Sans"), ("system", "系统字体")]);
        Text("font", "size", "气泡字号（4–200）", "21", true);
        Flag("font", "followPet", "字号跟随宠物大小", true);
        Title("物理与待机");
        Text("", "clickReactionThreshold", "累计点击多少次播放回应动画（2–100 次）", "5", true);
        Text("physics", "gravity", "重力", "1400", true);
        Text("physics", "restitution", "反弹系数（0–1）", "0.78", true);
        Text("physics", "groundFriction", "地面摩擦", "2.5", true);
        Text("physics", "throwPower", "抛出力度", "1", true);
        Flag("physics", "ceilingBounce", "碰到屏幕顶部反弹", true);
        Flag("physics", "petCollision", "宠物之间发生碰撞", true);
        Text("animationWeights", "idle", "待机权重（%）", "10", true);
        Text("animationWeights", "turn", "转身权重（%）", "5", true);
        Text("animationWeights", "move", "移动权重（%）", "5", true);
        Title("对话");
        var legacy = PetConfiguration.ReadObject(System.IO.Path.Combine(configuration.DirectoryPath, "standalone-ai.json"));
        Flag("", "automaticWhispers", "定时碎碎念", legacy.Flag("automaticWhispers"));
        Text("eventsRefreshSec", "whisper", "碎碎念间隔（秒）", "300", true);
        Text("", "chatMemoryRounds", "携带最近多少轮对话（0 关闭上下文）", "10", true);
        Text("", "chatImageLimit", "表情包候选上限（0 不限）", "20", true);
        Flag("", "chatAnimationEnabled", "允许对话触发动作", true);
        Flag("", "chatImageEnabled", "允许对话选择配图", true);
        Flag("", "whisperImageEnabled", "碎碎念随机配图", true);
        Title("语音");
        Flag("", "voiceEnabled", "自动朗读对话回复");
        Text("", "voiceName", "音色：例如 edge/zh-CN-XiaoyiNeural、edge/zh-CN-XiaoxiaoNeural、deepseek/音色编号", "edge/zh-CN-XiaoyiNeural");
        Text("", "voiceVolume", "音量（0–1）", "1", true);
        Choice("", "speechProvider", "语音识别服务", [("dsh", "DSH 插件（需连接外部 DSH）"), ("deepseek", "DeepSeek（需语音密钥）")]);
        Flag("", "voiceWhisperEnabled", "自动朗读碎碎念");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var save = new Button { Content = "保存并应用", Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var patch = new JsonObject();
                foreach (var action in saves) action(patch);
                var clickThreshold = patch.Number("clickReactionThreshold", 5);
                if (clickThreshold is < 2 or > 100 || clickThreshold != Math.Truncate(clickThreshold))
                    throw new InvalidOperationException("点击回应次数应为 2–100 的整数。");
                if (patch["font"]!.Number("size") is < 4 or > 200 || patch["voiceVolume"]!.GetValue<double>() > 1 || patch["physics"]!.Number("restitution") > 1)
                    throw new InvalidOperationException("字号、音量或反弹系数超出范围。");
                await saveConfiguration(patch);
                status.Text = "已保存。种类文件内的独立设置仍优先生效。";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = ex.Message; }
            finally { save.IsEnabled = true; }
        };
        panel.Children.Add(save);
        panel.Children.Add(status);
    }
}
