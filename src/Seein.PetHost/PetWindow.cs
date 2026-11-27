using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Seein.Pet;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brushes = System.Windows.Media.Brushes;

namespace Seein.PetHost;

internal sealed class PetWindow : Window
{
    private readonly PetEngine _engine;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _bubbleText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black, FontSize = 16 };
    private readonly Border _bubble;
    private CancellationTokenSource? _animation;
    private readonly List<Task> _playbacks = [];
    private WriteableBitmap? _bitmap;
    private AnimationFrame? _frame;
    private bool _dragging;
    private Point _dragOrigin;
    private readonly Stopwatch _pointerTime = Stopwatch.StartNew();
    private readonly List<PetDragSample> _dragTrail = [];
    private Point _dragBoxOrigin;
    private Point? _dragTarget;
    private double _dragScaleX = 1, _dragScaleY = 1;
    private double _lastPressAt = double.NegativeInfinity;
    private bool _slingshot;
    private int? _lastWindowX, _lastWindowY;
    private bool _moved;
    private nint _handle;
    private long _bubbleUntil;
    private bool _stopped;
    private bool _sleeping;
    private readonly PetClickReaction _clicks;
    private bool _clickReactionPlaying;
    private string? _sleepLoop;
    private string? _sleepEnd;
    private string _current = "";
    private bool _facingRight;
    private bool _mirrored;
    private (double Start, double Target, double Lead, double Tail)? _movement;
    private double _clipDuration;
    private long _movementStarted;
    private readonly HashSet<int> _pressedKeys = [];
    public PetProfile Profile { get; }
    public PetMotion Motion { get; } = new();
    public bool IsSleeping => _sleeping;
    public bool IsFixed { get; private set; }
    public bool IsDragging => _dragging;
    public bool IsMoving => _dragging || Motion.IsFlying || _movement is not null;
    public event Action<string>? AnimationFailed;
    public ContextMenu CreateMenu() { _movement = null; Motion.Place(); return BuildMenu(); }

    public PetWindow(PetProfile profile, PetEngine engine)
    {
        Profile = profile;
        _engine = engine;
        _clicks = new((int)profile.EntrySettings.Number("clickReactionThreshold", 5));
        IsFixed = profile.Settings.Flag("fixedEnabled");
        Title = profile.Name;
        ToolTip = profile.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Width = profile.Size;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        var font = profile.EntrySettings["font"];
        _bubbleText.FontSize = Math.Clamp((font?.Number("size", 21) ?? 21) * (font?.Flag("followPet", true) != false ? Width / 462 : 1), 4, 200);
        _bubbleText.FontFamily = new FontFamily(font?.Text("family") switch
        {
            "youyuan" or "candy" => "YouYuan, Microsoft YaHei", "kaiti" => "KaiTi", "simsun" => "SimSun",
            "comic" => "Comic Sans MS, Microsoft YaHei", "system" => "Segoe UI, Microsoft YaHei", _ => "Microsoft YaHei"
        });
        if (font?.Text("family", "candy") is null or "candy")
        {
            var folder = Path.Combine(engine.Assets, "fonts");
            if (Directory.Exists(folder)) _bubbleText.FontFamily = new FontFamily(new Uri(folder + Path.DirectorySeparatorChar), "./#上首软糖体");
        }
        Height = profile.Size * 9 / 16 + 100;
        _bubble = new Border { Child = _bubbleText, Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(12, 0, 12, 5), Visibility = Visibility.Hidden, MaxHeight = 95 };
        var root = new Grid { Background = Brushes.Transparent };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(_image, 1);
        root.Children.Add(_bubble);
        root.Children.Add(_image);
        Content = root;
        ContextMenu = BuildMenu();
        ContextMenuOpening += (_, _) => { _movement = null; Motion.Place(); };
        SourceInitialized += (_, _) => { _handle = new WindowInteropHelper(this).Handle; PlaceHome(); };
        DpiChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(() => { if (IsVisible) { if (_sleeping && _sleepLoop is not null) Play(_sleepLoop, true); else PlayRandom(); } });
        };
        MouseLeftButtonDown += BeginDrag;
        MouseMove += MoveDrag;
        MouseLeftButtonUp += EndDrag;
        LostMouseCapture += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            _dragTarget = null;
            _dragTrail.Clear();
            Motion.Place();
            ResumeAfterDrag();
            SavePosition();
        };
        Closed += (_, _) => StopAnimation();
        PlaceHome();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new PetContextMenu();
        MenuItem Add(string text, Action action, bool duringSleep = false, string hint = "")
        {
            var item = new MenuItem { Header = text, InputGestureText = hint };
            item.Click += (_, _) => { if (_sleeping && !duringSleep) RefuseSleep(); else action(); };
            menu.Items.Add(item);
            return item;
        }
        Add("桌宠设置", _engine.OpenSettings, true, "Ctrl+左键");
        Add("碎碎念", () => _ = _engine.WhisperAsync(this));
        Add("对话", () => _engine.OpenChat(this), hint: "L");
        var pictures = new MenuItem { Header = "配图" };
        var available = PetPictureWindow.Names(_engine, Profile).ToHashSet(StringComparer.Ordinal);
        foreach (var group in PetMemeMenu.Build(Profile.EntrySettings, available))
        {
            var branch = new MenuItem { Header = group.Label };
            foreach (var entry in group.Entries)
            {
                var path = PetPictureWindow.Find(_engine, Profile, entry.Name);
                if (path is null) continue;
                var picture = new MenuItem { Header = entry.Name, Icon = new PetMenuThumbnail(path) };
                picture.Click += (_, _) => { if (_sleeping) RefuseSleep(); else _ = _engine.WhisperAsync(this, entry.Name); };
                branch.Items.Add(picture);
            }
            if (branch.Items.Count > 0) pictures.Items.Add(branch);
        }
        if (pictures.Items.Count > 0) menu.Items.Add(pictures);
        var sleep = Add(_sleeping ? "起来" : "睡觉", ToggleSleep, true, "Z");
        var fixedPosition = Add(IsFixed ? "解除固定" : "固定", () => { IsFixed = !IsFixed; _engine.Configuration.UpdatePet(Profile.Id, p => p["fixedEnabled"] = IsFixed); });
        menu.Opened += (_, _) => { sleep.Header = _sleeping ? "起来" : "睡觉"; fixedPosition.Header = IsFixed ? "解除固定" : "固定"; };
        var actions = new MenuItem { Header = "动作" };
        var menuPools = new JsonObject();
        var animationPools = Profile.EntrySettings["animations"];
        foreach (var pool in new[] { ("待机", "idle"), ("转向", "turn"), ("拖拽", "drag"), ("点击回应", "clicks") })
            menuPools[pool.Item1] = animationPools?[pool.Item2]?.DeepClone();
        menuPools["移动"] = new JsonArray((animationPools?["moves"]?["actions"] as JsonArray ?? []).Select(a => JsonValue.Create(a!.Text("name")) as JsonNode).ToArray());
        foreach (var category in Categories()) menuPools[category.Key] = category.Value?.DeepClone();
        if (animationPools?["events"] is JsonObject eventPools)
            foreach (var pool in eventPools.Where(p => p.Key != "balance")) menuPools[pool.Key switch { "whisper" => "碎碎念", "workStatus" => "工作状态", _ => pool.Key }] = pool.Value?.DeepClone();
        foreach (var category in menuPools)
        {
            var sub = new MenuItem { Header = category.Key };
            foreach (var name in Names(category.Value))
            {
                var item = new MenuItem { Header = name };
                item.Click += (_, _) => { if (_sleeping) RefuseSleep(); else if (!PlayMove(name)) Play(name, false); };
                sub.Items.Add(item);
            }
            if (sub.Items.Count > 0) actions.Items.Add(sub);
        }
        menu.Items.Add(actions);
        var display = new MenuItem { Header = "显示位置" };
        var displayIndex = 1;
        foreach (var entry in new[] { ("仅网页", "web"), ("仅桌面", "desktop"), ("两者", "both"), ("都不", "none") })
        {
            var item = new MenuItem { Header = entry.Item1, InputGestureText = (displayIndex++).ToString() };
            item.Click += (_, _) => SetDisplay(entry.Item2);
            display.Items.Add(item);
        }
        menu.Items.Add(display);
        Add("重载配置", _engine.Reload, true);
        Add("回到初始位置", PlaceHome, true);
        Add("隐藏此宠物", () => _engine.HidePet(this), true);
        return menu;
    }

    private void RefuseSleep()
    {
        var lines = Names(Profile.EntrySettings["sleepRefuseTexts"]).ToArray();
        Say(lines.Length > 0 ? lines[Random.Shared.Next(lines.Length)] : "我在睡觉，请先叫醒我。" );
    }

    private void SetDisplay(string value)
    {
        _engine.Configuration.UpdatePet(Profile.Id, p => p["display"] = value);
        _engine.Reload();
    }

    public JsonObject Categories()
    {
        var output = new JsonObject();
        if (Profile.EntrySettings["animations"]?["categories"] is JsonArray categories)
            foreach (var category in categories.OfType<JsonObject>()) output[category.Text("id")] = category["actions"]?.DeepClone();
        return output;
    }
    private static IEnumerable<string> Names(JsonNode? value)
    {
        if (value is JsonArray array) return array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var text) ? text : "").Where(t => t.Length > 0);
        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? [text] : [];
    }

    public void PlayRandom(string? category = null)
    {
        var animations = Profile.EntrySettings["animations"];
        if (category is null)
        {
            var weights = Profile.EntrySettings["animationWeights"];
            var choice = Random.Shared.NextDouble() * 100;
            var idle = weights?.Number("idle", 10) ?? 10;
            var turn = IsFixed || Motion.IsFlying || ContextMenu?.IsOpen == true ? 0 : weights?.Number("turn", 5) ?? 5;
            var move = IsFixed || Motion.IsFlying || ContextMenu?.IsOpen == true ? 0 : weights?.Number("move", 5) ?? 5;
            if (choice < idle)
            {
                var idleNames = Names(animations?["idle"]).ToArray();
                if (idleNames.Length > 0) { Play(idleNames[Random.Shared.Next(idleNames.Length)], false); return; }
            }
            else if (choice < idle + turn)
            {
                var turns = Names(animations?["turn"]).ToArray();
                if (turns.Length > 0)
                {
                    Play(turns[Random.Shared.Next(turns.Length)], false, () =>
                    {
                        _facingRight = !_facingRight;
                        _image.RenderTransformOrigin = new Point(.5, .5);
                        _mirrored = _facingRight;
                        _image.RenderTransform = new ScaleTransform(_mirrored ? -1 : 1, 1);
                        PlayRandom();
                    });
                    return;
                }
            }
            else if (choice < idle + turn + move && animations?["moves"]?["actions"] is JsonArray moves && moves.Count > 0)
            {
                PlayMove(moves[Random.Shared.Next(moves.Count)]!.Text("name"));
                return;
            }
        }
        var categories = Profile.EntrySettings["animations"]?["categories"] as JsonArray;
        var available = categories?.OfType<JsonObject>().Where(c => category is null || c.Text("id") == category)
            .Where(c => !_facingRight || !c.Flag("noMirror")).ToArray() ?? [];
        var total = available.Sum(c => Math.Max(0, c.Number("weight", 1)));
        var pick = Random.Shared.NextDouble() * total;
        JsonObject? selected = available.FirstOrDefault();
        foreach (var item in available) { selected = item; pick -= Math.Max(0, item.Number("weight", 1)); if (pick <= 0) break; }
        var names = Names(selected?["actions"]).ToArray();
        if (names.Length == 0) return;
        var candidates = names.Length > 1 ? names.Where(n => n != _current).ToArray() : names;
        Play(candidates[Random.Shared.Next(candidates.Length)], false);
    }

    private bool PlayMove(string name)
    {
        var moves = Profile.EntrySettings["animations"]?["moves"];
        var action = (moves?["actions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => a.Text("name") == name);
        if (action is null) return false;
        var defaults = moves?["default"] as JsonObject ?? new();
        var settings = action["params"] is JsonObject parameters ? PetConfiguration.Overlay(defaults, parameters) : defaults;
        var min = settings.Number("minDist", 60) * Width / 462;
        var max = Math.Max(min, settings.Number("maxDist", 240) * Width / 462);
        var area = WorkArea(System.Windows.Forms.Screen.FromHandle(_handle).WorkingArea);
        var target = Math.Clamp(Motion.X + (_facingRight ? 1 : -1) * (min + Random.Shared.NextDouble() * (max - min)), area.Left, Math.Max(area.Left, area.Right - Width));
        Play(name, false);
        _movement = (Motion.X, target, settings.Number("leadSec", 2), settings.Number("tailSec", 2));
        _movementStarted = 0;
        _engine.StartMotionClock();
        return true;
    }

    public void Play(string animation, bool loop, Action? completed = null, bool clickReaction = false)
    {
        if (_stopped) return;
        if (Path.GetFileName(animation) != animation || animation is "." or "..") { Say("动画名称无效。"); return; }
        var path = Path.Combine(Profile.AssetDirectory, animation + ".webm");
        if (Profile.Entry == "main" || !Directory.Exists(Profile.AssetDirectory))
        {
            var custom = Path.Combine(_engine.Configuration.DirectoryPath, "main-animation", "webm", animation + ".webm");
            path = File.Exists(custom) ? custom : Path.Combine(_engine.Assets, "webm", animation + ".webm");
        }
        if (!File.Exists(path)) { AnimationFailed?.Invoke(animation); Say("找不到动画素材。"); return; }
        StopAnimation();
        _clickReactionPlaying = clickReaction;
        var noMirror = (Profile.EntrySettings["animations"]?["categories"] as JsonArray)?.OfType<JsonObject>()
            .Any(c => c.Flag("noMirror") && Names(c["actions"]).Contains(animation)) == true;
        _mirrored = _facingRight && !noMirror;
        _image.RenderTransformOrigin = new Point(.5, .5);
        _image.RenderTransform = new ScaleTransform(_mirrored ? -1 : 1, 1);
        _current = animation;
        _movement = null;
        var cancellation = new CancellationTokenSource();
        _animation = cancellation;
        var task = Task.Run(async () =>
        {
            try
            {
                // Keep the original video detail. WPF performs the single final scale at the monitor DPI.
                using var decoder = new AnimationDecoder(path);
                _clipDuration = decoder.Duration;
                do
                {
                    var clock = Stopwatch.StartNew();
                    double? first = null;
                    AnimationFrame? frame;
                    while ((frame = decoder.Read(cancellation.Token)) is not null)
                    {
                        first ??= frame.Timestamp;
                        var delay = frame.Timestamp - first.Value - clock.Elapsed.TotalSeconds;
                        if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay, 1)), cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        await Dispatcher.InvokeAsync(() => { if (_animation == cancellation && IsVisible) Draw(frame); }, System.Windows.Threading.DispatcherPriority.Render, cancellation.Token);
                    }
                    if (loop) decoder.Rewind();
                } while (loop && !cancellation.IsCancellationRequested);
                await Dispatcher.InvokeAsync(() => { if (_animation == cancellation) { _clickReactionPlaying = false; if (completed is not null) completed(); else if (!_sleeping) PlayRandom(); } });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => { if (_animation == cancellation) { _clickReactionPlaying = false; Say(ex.Message); AnimationFailed?.Invoke(animation); } });
            }
            finally { cancellation.Dispose(); }
        });
        _playbacks.RemoveAll(t => t.IsCompleted);
        _playbacks.Add(task);
    }

    private void Draw(AnimationFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
            _image.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
        _frame = frame;
        if (_movement is not null && _movementStarted == 0) _movementStarted = Stopwatch.GetTimestamp();
    }

    public void Say(string message)
    {
        _bubbleText.Text = message;
        _bubble.Visibility = Visibility.Visible;
        _bubbleUntil = Environment.TickCount64 + Math.Clamp(message.Length * 160, 4000, 15000);
    }

    public void Pause()
    {
        StopAnimation();
        Hide();
    }

    public void Resume()
    {
        Show();
        if (_sleeping && _sleepLoop is not null) Play(_sleepLoop, true);
        else PlayRandom();
    }

    public void ToggleSleep()
    {
        _clicks.Reset();
        if (_sleeping)
        {
            _sleeping = false;
            if (_sleepEnd is not null) Play(_sleepEnd, false); else PlayRandom();
            Say(RandomText("sleepEndTexts", "睡醒啦。"));
            return;
        }
        if (Profile.EntrySettings["animations"]?["sleep"] is not JsonArray sets || sets.Count == 0) return;
        var set = sets[Random.Shared.Next(sets.Count)]!;
        _sleeping = true;
        Say(RandomText("sleepStartTexts", "我先睡一会儿。"));
        Motion.Place();
        _sleepLoop = Names(set["loop"]).FirstOrDefault();
        _sleepEnd = Names(set["end"]).FirstOrDefault();
        var start = Names(set["start"]).FirstOrDefault();
        if (start is not null) Play(start, false, () => { if (_sleepLoop is not null) Play(_sleepLoop, true); });
        else if (_sleepLoop is not null) Play(_sleepLoop, true);
    }

    private string RandomText(string field, string fallback)
    {
        var values = Names(Profile.EntrySettings[field]).ToArray();
        return values.Length > 0 ? values[Random.Shared.Next(values.Length)] : fallback;
    }

    public void PlayEvent(string name, int? index = null)
    {
        if (_sleeping) return;
        var names = Names(Profile.EntrySettings["animations"]?["events"]?[name]).ToArray();
        if (names.Length > 0) Play(names[index is null ? Random.Shared.Next(names.Length) : Math.Clamp(index.Value, 0, names.Length - 1)], false);
    }

    public void PlaceHome()
    {
        if (_handle == 0) return;
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == Profile.Settings.Text("nativeMonitor"))
            ?? System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        // Move in device pixels first so WPF adopts the destination monitor's DPI before DIP placement.
        SetWindowPos(_handle, 0, screen.WorkingArea.Left + 20, screen.WorkingArea.Top + 20, 0, 0, 0x15);
        Dispatcher.BeginInvoke(() => PlaceOnScreen(screen));
    }

    private void PlaceOnScreen(System.Windows.Forms.Screen screen)
    {
        if (_stopped) return;
        var position = Profile.Settings["position"];
        var corner = position?.Text("corner", "bottom-right") ?? "bottom-right";
        var area = WorkArea(screen.WorkingArea);
        var percent = Profile.Settings.Number("sizePercent");
        if (percent > 0) { Width = Math.Clamp(area.Width * percent / 100, 32, 4096); Height = Width * 9 / 16 + 100; }
        Motion.X = corner.Contains("right") ? area.Right - Width - (position?.Number("marginX", 20) ?? 20) : area.Left + (position?.Number("marginX", 20) ?? 20);
        Motion.Y = corner.Contains("bottom") ? area.Bottom - Height - (position?.Number("marginY", 0) ?? 0) : area.Top + (position?.Number("marginY", 0) ?? 0);
        Motion.X = Math.Clamp(Motion.X, area.Left, Math.Max(area.Left, area.Right - Width));
        Motion.Y = Math.Clamp(Motion.Y, area.Top - 100, Math.Max(area.Top - 100, area.Bottom - Height));
        Motion.Place();
        Left = Motion.X;
        Top = Motion.Y;
    }

    private Rect WorkArea(System.Drawing.Rectangle area)
    {
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = scale.Transform(new Point(area.Left, area.Top));
        var bottomRight = scale.Transform(new Point(area.Right, area.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    public void Tick(double seconds)
    {
        if (!IsVisible) return;
        if (_bubble.Visibility == Visibility.Visible && Environment.TickCount64 >= _bubbleUntil) _bubble.Visibility = Visibility.Hidden;
        if (GetCursorPos(out var cursor))
        {
            var point = PointFromScreen(new Point(cursor.X, cursor.Y));
            var over = HitVisible(point);
            // WPF's layered window already passes alpha-zero pixels through to underlying apps.
            // Toggling WS_EX_TRANSPARENT on a timer loses clicks made immediately after pointer movement.
            PollKeys(over);
        }
        if (_dragging) { FollowDrag(seconds); return; }
        if (ContextMenu?.IsOpen == true) return;
        if (_movement is { } move && _movementStarted != 0 && !Motion.IsFlying)
        {
            var time = Stopwatch.GetElapsedTime(_movementStarted).TotalSeconds;
            var progress = Math.Clamp((time - move.Lead) / Math.Max(.1, _clipDuration - move.Lead - move.Tail), 0, 1);
            Motion.X = move.Start + (move.Target - move.Start) * progress;
            if (progress >= 1) _movement = null;
        }
        var screen = System.Windows.Forms.Screen.FromHandle(_handle);
        var bounds = Profile.EntrySettings.Flag("confineToScreen", true) ? screen.WorkingArea : System.Windows.Forms.SystemInformation.VirtualScreen;
        var area = WorkArea(bounds);
        var physics = Profile.EntrySettings["physics"];
        var wasFlying = Motion.IsFlying;
        Motion.Step(seconds, area.Left, area.Top - 100, Math.Max(area.Left, area.Right - Width), Math.Max(area.Top - 100, area.Bottom - Height),
            physics?.Number("gravity", 1400) ?? 1400, physics?.Number("restitution", .78) ?? .78,
            physics?.Number("groundFriction", 2.5) ?? 2.5, physics?.Flag("ceilingBounce", true) ?? true);
        ApplyPosition();
        if (wasFlying && !Motion.IsFlying) SavePosition();
    }

    private void PollKeys(bool over)
    {
        var modifiers = (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0
            || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
        foreach (var key in new[] { 0x4C, 0x4F, 0x53, 0x5A, 0x31, 0x32, 0x33, 0x34 })
        {
            var down = (GetAsyncKeyState(key) & 0x8000) != 0;
            if (!down) { _pressedKeys.Remove(key); continue; }
            if (!_pressedKeys.Add(key) || !over || modifiers || _dragging) continue;
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var pid);
            if (pid == Environment.ProcessId && foreground != _handle) continue;
            if (_sleeping && key is 0x4C or 0x53) { RefuseSleep(); continue; }
            switch (key)
            {
                case 0x4C: _engine.OpenChat(this); break;
                case 0x4F: _engine.OpenSettings(); break;
                case 0x53: _engine.OpenCaptureChat(this); break;
                case 0x5A: ToggleSleep(); break;
                default:
                    var target = new[] { "web", "desktop", "both", "none" }[key - 0x31];
                    Dispatcher.BeginInvoke(() => SetDisplay(target));
                    break;
            }
        }
    }

    private bool HitVisible(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X >= ActualWidth || point.Y >= ActualHeight) return false;
        if (_bubble.Visibility == Visibility.Visible && point.Y < 100) return true;
        if (_frame is null || point.Y < 100) return false;
        var x = (int)(point.X / ActualWidth * _frame.Width);
        if (_mirrored) x = _frame.Width - 1 - x;
        var y = (int)((point.Y - 100) / (ActualHeight - 100) * _frame.Height);
        return x >= 0 && y >= 0 && x < _frame.Width && y < _frame.Height && _frame.Pixels[(y * _frame.Width + x) * 4 + 3] > 16;
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _moved = false;
        _dragOrigin = PointToScreen(e.GetPosition(this));
        _dragBoxOrigin = new Point(Motion.X, Motion.Y);
        var dpi = VisualTreeHelper.GetDpi(this);
        _dragScaleX = dpi.DpiScaleX;
        _dragScaleY = dpi.DpiScaleY;
        var now = _pointerTime.Elapsed.TotalMilliseconds;
        _slingshot = now - _lastPressAt < 200;
        _lastPressAt = _slingshot ? double.NegativeInfinity : now;
        _dragTarget = null;
        _dragTrail.Clear();
        _movement = null;
        Motion.Place();
        CaptureMouse();
        _engine.StartMotionClock();
    }

    private void MoveDrag(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var point = PointToScreen(e.GetPosition(this));
        var delta = new Vector((point.X - _dragOrigin.X) / _dragScaleX, (point.Y - _dragOrigin.Y) / _dragScaleY);
        if (!_moved)
        {
            if (delta.Length < 5) return;
            _moved = true;
            _clicks.Reset();
            var drag = Names(Profile.EntrySettings["animations"]?["drag"]).FirstOrDefault();
            if (!_sleeping && drag is not null) Play(drag, true);
        }
        var now = _pointerTime.Elapsed.TotalMilliseconds;
        _dragTrail.Add(new(now, point.X / _dragScaleX, point.Y / _dragScaleY));
        _dragTrail.RemoveAll(s => s.Milliseconds < now - PetDragRelease.TrailMilliseconds);
        _dragTarget = _dragBoxOrigin + delta;
    }

    private void FollowDrag(double seconds)
    {
        if (_dragTarget is not { } target) return;
        Motion.X = target.X;
        Motion.Y = target.Y;
        ApplyPosition();
    }

    private void ApplyPosition()
    {
        if (_handle == 0) return;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var physical = transform.Transform(new Point(Motion.X, Motion.Y));
        var x = (int)Math.Round(physical.X);
        var y = (int)Math.Round(physical.Y);
        if (_lastWindowX == x && _lastWindowY == y) return;
        // Move the existing layered surface once. Setting WPF Left and Top separately causes two moves/layout passes.
        SetWindowPos(_handle, 0, x, y, 0, 0, 0x215);
        _lastWindowX = x;
        _lastWindowY = y;
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        FollowDrag(0);
        _dragging = false;
        _dragTarget = null;
        ReleaseMouseCapture();
        if (!_moved)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _clicks.Reset(); _engine.OpenSettings(); return; }
            if (!_clicks.Click(_clickReactionPlaying)) return;
            if (_sleeping) { ToggleSleep(); return; }
            var clicks = Names(Profile.EntrySettings["animations"]?["clicks"]).ToArray();
            if (clicks.Length > 0) Play(clicks[Random.Shared.Next(clicks.Length)], false, clickReaction: true);
            return;
        }
        else
        {
            var power = Profile.EntrySettings["physics"]?.Number("throwPower", 1) ?? 1;
            if (_slingshot)
            {
                var velocity = PetDragRelease.Slingshot(Motion.X - _dragBoxOrigin.X, Motion.Y - _dragBoxOrigin.Y, Width, power);
                Motion.Launch(velocity.X, velocity.Y);
            }
            else Motion.Release(_dragTrail, _pointerTime.Elapsed.TotalMilliseconds, power);
            ResumeAfterDrag();
        }
        _dragTrail.Clear();
        SavePosition();
    }

    private void ResumeAfterDrag()
    {
        if (_sleeping) return;
        var idle = Names(Profile.EntrySettings["animations"]?["idle"]).ToArray();
        if (idle.Length > 0) Play(idle[Random.Shared.Next(idle.Length)], false); else PlayRandom();
    }

    private void SavePosition()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(_handle);
        var area = WorkArea(screen.WorkingArea);
        _engine.Configuration.UpdatePet(Profile.Id, p =>
        {
            p["nativeMonitor"] = screen.DeviceName;
            p["position"] = new JsonObject { ["corner"] = "bottom-left", ["marginX"] = Motion.X - area.Left, ["marginY"] = area.Bottom - (Motion.Y + Height) };
        });
    }

    private void StopAnimation()
    {
        _clickReactionPlaying = false;
        var previous = _animation;
        _animation = null;
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task ShutdownAsync()
    {
        _stopped = true;
        StopAnimation();
        await Task.WhenAll(_playbacks);
        Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
