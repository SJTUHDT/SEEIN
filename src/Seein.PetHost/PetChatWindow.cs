using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Seein.Pet;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace Seein.PetHost;

internal sealed class PetChatWindow : Window
{
    private PetWindow _pet;
    private readonly PetEngine _engine;
    private readonly StackPanel _messages = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input = new() { AcceptsReturn = true, MinHeight = 60, MaxHeight = 160, Padding = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button _send = new() { Content = "发送", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly CancellationTokenSource _closed;
    private string? _image;
    private NAudio.Wave.WaveInEvent? _microphone;
    private readonly Button _record = new() { Content = "语音输入", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
    public string PetId => _pet.Profile.Id;
    public void Rebind(PetWindow pet) { _pet = pet; Title = pet.Profile.Name; }

    public PetChatWindow(PetWindow pet, PetEngine engine)
    {
        NativeTheme.Configure(this);
        _pet = pet;
        _engine = engine;
        _closed = CancellationTokenSource.CreateLinkedTokenSource(engine.Lifetime);
        Title = pet.Profile.Name;
        Width = 600;
        Height = 680;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        var attach = new Button { Content = "添加图片", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        attach.Click += (_, _) => Attach();
        var clear = new Button { Content = "清空对话", Padding = new Thickness(10, 6, 10, 6) };
        var capture = new Button { Content = "截图", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        capture.Click += async (_, _) => await CaptureAsync();
        clear.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show("清空这只宠物的对话记忆？", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            engine.Bridge.ClearMemory(_pet.Profile);
            _messages.Children.Clear();
        };
        actions.Children.Add(attach);
        actions.Children.Add(capture);
        actions.Children.Add(_record);
        _record.Click += (_, _) => ToggleRecording();
        actions.Children.Add(clear);
        var silence = new Button { Content = "停止朗读", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0) };
        silence.Click += (_, _) => engine.StopSpeech();
        actions.Children.Add(silence);
        footer.Children.Add(actions);
        footer.Children.Add(_status);
        var compose = new DockPanel();
        DockPanel.SetDock(_send, Dock.Right);
        compose.Children.Add(_send);
        compose.Children.Add(_input);
        footer.Children.Add(compose);
        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(footer);
        root.Children.Add(_scroll);
        Content = root;
        RefreshHistory();
        _send.Click += async (_, _) => await SendAsync();
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { e.Handled = true; await SendAsync(); }
        };
        Closed += (_, _) => { _closed.Cancel(); _microphone?.StopRecording(); };
    }

    public async Task CaptureAsync()
    {
        var visible = IsVisible;
        var pets = _engine.PauseForCapture();
        Hide();
        try
        {
            var image = await ScreenSelectionWindow.CaptureAsync(_closed.Token);
            if (image is null) return;
            if (image.Length > 5 * 1024 * 1024) throw new InvalidDataException("截图不能超过 5 MiB，请缩小选区。");
            _image = "data:image/png;base64," + Convert.ToBase64String(image);
            _status.Text = "已附加截图。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_closed.IsCancellationRequested) _status.Text = ex.Message; }
        finally
        {
            if (!_closed.IsCancellationRequested)
            {
                _engine.ResumeAfterCapture(pets);
                if (visible) { Show(); Activate(); }
            }
        }
    }

    private void ToggleRecording()
    {
        if (_microphone is not null) { _microphone.StopRecording(); return; }
        try
        {
            var recorded = new MemoryStream();
            var recording = new NAudio.Wave.WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(recorded), new NAudio.Wave.WaveFormat(16000, 16, 1));
            var microphone = new NAudio.Wave.WaveInEvent { WaveFormat = new NAudio.Wave.WaveFormat(16000, 16, 1) };
            _microphone = microphone;
            microphone.DataAvailable += (_, e) =>
            {
                recording.Write(e.Buffer, 0, e.BytesRecorded);
                if (recorded.Length >= 3_840_000) microphone.StopRecording();
            };
            microphone.RecordingStopped += async (_, e) =>
            {
                recording.Dispose();
                var bytes = recorded.ToArray();
                recorded.Dispose();
                microphone.Dispose();
                await Dispatcher.InvokeAsync(async () =>
                {
                    _record.Content = "语音输入";
                    _microphone = null;
                    if (_closed.IsCancellationRequested) return;
                    try
                    {
                        if (e.Exception is not null) throw e.Exception;
                        _status.Text = "正在识别…";
                        _input.Text += await _engine.Voice.TranscribeAsync(bytes, _pet.Profile.EntrySettings.Text("speechProvider", "dsh"), _closed.Token);
                        _status.Text = "";
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_closed.IsCancellationRequested) _status.Text = ex.Message; }
                });
            };
            try { microphone.StartRecording(); }
            catch { microphone.Dispose(); recording.Dispose(); recorded.Dispose(); _microphone = null; throw; }
            _record.Content = "结束录音";
            _status.Text = "正在录音…";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _microphone?.Dispose();
            _microphone = null;
            _status.Text = ex.Message;
        }
    }

    private void RefreshHistory()
    {
        _messages.Children.Clear();
        foreach (var message in _engine.Memory.Read(_pet.Profile).OfType<JsonObject>()) AddMessage(message);
    }

    private void AddMessage(JsonObject message)
    {
        var role = message.Text("role");
        var text = message.Text("content");
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = role == "user" ? "你" : _pet.Profile.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent });
        if (message["image"] is not null) panel.Children.Add(new TextBlock { Text = "这条消息附带过图片", FontSize = 12 });
        if (message.Text("meme") is { Length: > 0 } meme)
        {
            var path = PetPictureWindow.Find(_engine, _pet.Profile, meme);
            if (path is not null) panel.Children.Add(new PetImage(path) { MaxHeight = 150, HorizontalAlignment = HorizontalAlignment.Left });
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var remove = new Button { Content = "删除这轮", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 3, 6, 3) };
        remove.Click += (_, _) => { _engine.Bridge.ClearMemory(_pet.Profile, new HashSet<long> { (long)message.Number("ts") }); RefreshHistory(); };
        actions.Children.Add(remove);
        if (role == "assistant")
        {
            var speak = new Button { Content = "朗读", Padding = new Thickness(6, 3, 6, 3) };
            speak.Click += (_, _) => _ = _engine.SpeakAsync(_pet, text, true);
            actions.Children.Add(speak);
        }
        panel.Children.Add(actions);
        var bubble = new Border { Child = panel, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10) };
        bubble.SetResourceReference(Border.BackgroundProperty, role == "user" ? "AccentSoftBrush" : "PanelBrush");
        _messages.Children.Add(bubble);
        _scroll.ScrollToEnd();
    }

    private void Attach()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var file = new FileInfo(dialog.FileName);
        if (file.Length > 5 * 1024 * 1024) { _status.Text = "图片不能超过 5 MiB。"; return; }
        var media = file.Extension.ToLowerInvariant() switch { ".png" => "png", ".webp" => "webp", ".gif" => "gif", _ => "jpeg" };
        _image = "data:image/" + media + ";base64," + Convert.ToBase64String(File.ReadAllBytes(file.FullName));
        _status.Text = "已附加 " + file.Name;
    }

    private async Task SendAsync()
    {
        if (!_send.IsEnabled || string.IsNullOrWhiteSpace(_input.Text)) return;
        if (_pet.IsSleeping) { _status.Text = "宠物正在睡觉，请先叫醒。"; return; }
        var text = _input.Text;
        _send.IsEnabled = false;
        _status.Text = "正在回复…";
        try
        {
            var reply = await _engine.Bridge.ChatAsync(_pet.Profile, text, _image, _closed.Token);
            if (_closed.IsCancellationRequested) return;
            RefreshHistory();
            _input.Clear();
            _image = null;
            _status.Text = "";
            _pet.Say(reply.Text);
            if (reply.Meme is not null) _engine.ShowPicture(_pet, reply.Meme);
            _ = _engine.SpeakAsync(_pet, reply.Text);
            if (reply.Action is not null) _pet.Play(reply.Action, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closed.IsCancellationRequested) _status.Text = ex is OperationCanceledException ? "对话超时，请重试。" : ex.Message;
        }
        finally { _send.IsEnabled = true; }
    }
}
