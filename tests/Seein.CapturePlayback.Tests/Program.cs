using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Seein.Devices.CapturePlayback;
using Seein.Tray;
using Seein.Tray.CapturePlayback;

internal static class Program
{
    private static int _passed;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = FindRoot();
            Environment.SetEnvironmentVariable("SEEIN_INSTANCE", "CapturePlaybackTests");
            Environment.SetEnvironmentVariable("SEEIN_PREVIEW_USER_DATA", Path.Combine(root, ".build", "capture-playback-work", "headless-data"));
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(Path.Combine(root, "src/Seein.Tray/Styles/Theme.Light.xaml")) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(Path.Combine(root, "src/Seein.Tray/Styles/Controls.xaml")) });
            var profileStore = new CaptureProfileStore(Path.Combine(Seein.Core.AppPaths.UserDefault.DataRoot, "devices", "capture-playback.json"));
            profileStore.Save(new CaptureProfile { VideoId = "offline-card", ModeKey = "manual-mode", Volume = 24, Muted = true, AudioSelectionSaved = true });
            var view = new CapturePlaybackView(false);
            Check("constructing the page does not start playback", !(bool)typeof(CaptureSession).GetProperty("IsActive")!.GetValue(Session(view))!);
            Check("independent volume and mute controls are removed", view.FindName("VolumeSlider") == null && view.FindName("MuteToggle") == null);
            Check("system volume behavior is explained", ((TextBlock)view.FindName("SystemVolumeHint")).Text == "声音跟随系统音量");
            foreach (var width in new[] { 760d, 1100d, 1500d })
            {
                view.Measure(new Size(width, 800)); view.Arrange(new Rect(0, 0, width, 800)); view.UpdateLayout();
                var video = (ComboBox)view.FindName("VideoSelector");
                var mode = (ComboBox)view.FindName("ModeSelector");
                Check($"selectors fit at {width}px", video.ActualWidth >= 180 && mode.ActualWidth >= 180);
                Check($"selectors align at {width}px", Math.Abs(video.TranslatePoint(new Point(), view).Y - mode.TranslatePoint(new Point(), view).Y) < .1);
            }
            Check("Switch power controls cannot fake success", FindButtons(view).Where(b => b.Content is "开机" or "关机").Count(b => !b.IsEnabled) == 2);
            RenderPreview(view, root, "light");
            var originalSession = Session(view);
            view.OpenPlayer(false);
            var popup = (Window)typeof(CapturePlaybackView).GetField("_playerWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            Check("pop-out contains only the video frame", ReferenceEquals(popup.Content, view.FindName("VideoFrame")));
            var videoFrame = (Border)view.FindName("VideoFrame");
            Check("pop-out video has no card stroke", videoFrame.BorderThickness == new Thickness(0));
            Check("player window has no content border or padding", popup.BorderThickness == new Thickness(0) && popup.Padding == new Thickness(0));
            view.ToggleFullscreen();
            Check("fullscreen removes window chrome and video stroke", popup.WindowStyle == WindowStyle.None && popup.ResizeMode == ResizeMode.NoResize && videoFrame.BorderThickness == new Thickness(0));
            Check("fullscreen geometry verification does not display a window", !popup.IsVisible);
            view.ToggleFullscreen();
            Check("leaving fullscreen restores standard window chrome", popup.WindowStyle == WindowStyle.SingleBorderWindow && videoFrame.BorderThickness == new Thickness(0));
            Check("main page retains playback controls and status", ((Grid)view.FindName("PlayerPanel")).IsAncestorOf((Border)view.FindName("PlayerControls")) && ((StackPanel)view.FindName("PlayerDetails")).Visibility == Visibility.Visible);
            Check("main page offers show, recall and window fullscreen", ((Button)view.FindName("ShowPlayerButton")).Content.ToString() == "显示播放窗口" && ((Button)view.FindName("RecallPlayerButton")).Content.ToString() == "召回窗口" && ((Button)view.FindName("DetachedFullscreenButton")).Content.ToString() == "窗口全屏");
            RenderPreview(view, root, "detached");
            view.ReturnToPage();
            Check("recall restores only the video without replacing the session", ((Grid)view.FindName("VideoArea")).Children.Contains((Border)view.FindName("VideoFrame")) && ReferenceEquals(originalSession, Session(view)));
            Check("recall removes detached placeholder", ((Border)view.FindName("DetachedHint")).Visibility == Visibility.Collapsed);
            Check("recall restores the inline card border", videoFrame.BorderThickness == new Thickness(1));
            view.Dispose();
            var retained = profileStore.Load();
            Check("closing before discovery preserves card and mode", retained.VideoId == "offline-card" && retained.ModeKey == "manual-mode" && retained.AudioSelectionSaved);
            Check("legacy independent volume and mute are cleared", retained.Volume == 100 && !retained.Muted);
            app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(Path.Combine(root, "src/Seein.Tray/Styles/Theme.Dark.xaml")) };
            var darkView = new CapturePlaybackView(false);
            Check("dark page follows SEEIN dark palette", ((SolidColorBrush)darkView.Background).Color == Color.FromRgb(32, 32, 32));
            RenderPreview(darkView, root, "dark"); darkView.Dispose();
            DecodeTests();
            if (args.Contains("--verify-gpu"))
            {
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("SEEIN hidden GPU test")
                    { Width = 64, Height = 36, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 });
                CaptureRenderer.ValidateGpuSurface(source.Handle);
                Check("D3D swapchain creation, resize and frame submission", true);
            }
            SessionTests().GetAwaiter().GetResult();
            if (args.Contains("--verify-package") || args.Contains("--verify-framefix") || args.Contains("--verify-playbackfix"))
                VerifyPackage(root, args.Contains("--verify-framefix"), args.Contains("--verify-playbackfix"));
            Console.WriteLine($"PASS: {_passed} headless checks. No capture devices enumerated/opened; no windows shown.");
            app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void VerifyPackage(string root, bool frameFix, bool playbackFix)
    {
        var work = Path.Combine(root, ".build", "capture-playback-work");
        var directory = Path.Combine(work, playbackFix ? "prepared-playbackfix" : frameFix ? "prepared-framefix" : "prepared-final");
        var key = File.ReadAllText(Path.Combine(work, "module-publisher.pem"));
        var manifest = Seein.Modularity.ModulePackage.ReadVerified(directory, key, new Version(2, 1, 0));
        using var zip = ZipFile.OpenRead(Path.Combine(work, playbackFix ? "devices-playbackfix-2.1.0.seeinplugin" : frameFix ? "devices-framefix-2.1.0.seeinplugin" : "candidate-devices-2.1.0.seeinplugin"));
        Check("signed package has complete exact file list", manifest.Files.Select(f => f.Path).Append("module.json").Append("module.sig")
            .Order().SequenceEqual(zip.Entries.Select(e => e.FullName).Order()));
        foreach (var file in manifest.Files)
        {
            using var stream = zip.GetEntry(file.Path)!.Open();
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package hash mismatch: " + file.Path);
        }
        Check("zip payload hashes match signed module", true);
        Check("existing RGB engine is retained", manifest.Files.Any(f => f.Path == "rgb-engine/seergb.exe"));
        Check("runtime package includes notices and excludes debug symbols", manifest.Files.Any(f => f.Path == "CapturePlayback/THIRD-PARTY-NOTICES.txt") && !manifest.Files.Any(f => f.Path.EndsWith(".pdb")));
        var context = new Seein.Modularity.ModuleLoadContext(directory, "Seein.Desktop.Devices.dll");
        var entry = context.LoadFromAssemblyPath(Path.Combine(directory, "Seein.Desktop.Devices.dll"));
        Check("desktop entry matches host contract", typeof(Seein.Contracts.IDesktopModule).IsAssignableFrom(entry.GetType("Seein.Tray.DevicesDesktop")));
        foreach (var name in new[] { "FlashCap", "Vortice.Direct3D11", "Vortice.Direct2D1", "NAudio.Wasapi" })
            Check("isolated module resolves " + name, context.LoadFromAssemblyName(new AssemblyName(name)) != null);
        foreach (var name in new[] { "NAudio.Core", "NAudio.Wasapi", "NAudio.WinMM", "HidSharp" })
            Check("process-wide device library is shared with host: " + name, ReferenceEquals(
                context.LoadFromAssemblyName(new AssemblyName(name)), System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name))));
    }
    private static void RenderPreview(CapturePlaybackView view, string root, string theme)
    {
        view.Measure(new Size(1100, 800)); view.Arrange(new Rect(0, 0, 1100, 800)); view.UpdateLayout();
        var render = new RenderTargetBitmap(1100, 800, 96, 96, PixelFormats.Pbgra32); render.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
        using var output = File.Create(Path.Combine(root, ".build", "capture-playback-work", $"page-{theme}.png")); encoder.Save(output);
        Check($"{theme} page renders entirely offscreen", render.PixelWidth == 1100);
    }
    private static CaptureSession Session(CapturePlaybackView view) => (CaptureSession)typeof(CapturePlaybackView)
        .GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node)
            {
                if (node is Button button) yield return button;
                foreach (var nested in FindButtons(node)) yield return nested;
            }
    }
    private static void DecodeTests()
    {
        using var decoder = new CaptureFrameDecoder();
        var pixels = Enumerable.Repeat((byte)120, 64 * 36 * 4).ToArray();
        var bitmap = BitmapSource.Create(64, 36, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        foreach (BitmapEncoder encoder in new BitmapEncoder[] { new JpegBitmapEncoder(), new BmpBitmapEncoder() })
        {
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); var bytes = stream.ToArray();
            var frame = decoder.Decode(bytes, bytes.Length);
            Check($"{encoder.GetType().Name} decode dimensions", frame.Width == 64 && frame.Height == 36);
            Check($"{encoder.GetType().Name} decode pixel data", frame.Pixels[0] is > 100 and < 140);
            var reused = decoder.Decode(bytes, bytes.Length);
            Check("decoded pixel storage is reused", ReferenceEquals(frame.Pixels, reused.Pixels));
            try { decoder.Decode([1, 2, 3], 3); throw new InvalidOperationException("corrupt JPEG was accepted"); }
            catch (SharpGen.Runtime.SharpGenException) { Check("corrupt image is rejected", true); }
            Check("decoder recovers after corrupt frame", decoder.Decode(bytes, bytes.Length).Width == 64);
        }
    }
    private static async Task SessionTests()
    {
        var firstFrame = new CaptureFirstFrameGate();
        try { await firstFrame.WaitAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None); throw new Exception("Missing frame accepted"); }
        catch (TimeoutException) { Check("opening without samples cannot report playback success", true); }
        firstFrame.FrameArrived(); firstFrame.FrameArrived();
        await firstFrame.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Check("actual first frame completes startup once", true);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await new CaptureFirstFrameGate().WaitAsync(TimeSpan.FromSeconds(10), canceled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check("stop cancels first-frame waiting", true); }
        }
        var profile = new CaptureProfile { VideoId = "fake-card" };
        var opened = 0; var connection = new FakeConnection();
        await using (var session = new CaptureSession((_, _, _) => { Interlocked.Increment(ref opened); return Task.FromResult<ICaptureConnection>(connection); },
            (_, ct) => Task.Delay(Timeout.Infinite, ct)))
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => session.Start(profile))));
            await WaitUntil(() => session.ActiveMode != null);
            Check("rapid starts share one capture connection", opened == 1);
            session.Renderer.Attach(0); session.Renderer.SetVisible(false); session.Renderer.SetVisible(true);
            Check("changing visible surface does not reopen capture", opened == 1 && connection.Stopped == 0);
            await session.StopAsync();
            Check("stop awaits device release", !session.IsActive && connection.Stopped == 1 && connection.Disposed == 1);
        }
        await using (var session = new CaptureSession(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new Exception(); }))
        {
            session.Start(profile); await session.StopAsync();
            Check("stop cancels an in-flight device open", !session.IsActive);
        }
        var retries = 0;
        await using (var session = new CaptureSession((_, _, _) => throw new InvalidOperationException("busy"),
            (_, _) => { retries++; return Task.CompletedTask; }))
        {
            session.Start(profile); await WaitUntil(() => !session.IsActive);
            Check("busy device is not reopened endlessly", retries == 0 && session.Status == "busy");
        }
        var ids = new List<string>();
        await using (var session = new CaptureSession((p, _, _) =>
        {
            ids.Add(p.VideoId);
            if (ids.Count == 1) throw new IOException("unplugged");
            return Task.FromResult<ICaptureConnection>(new FakeConnection());
        }, (delay, ct) => delay.TotalSeconds == 1 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask))
        {
            session.Start(profile); await WaitUntil(() => session.ActiveMode != null);
            await session.StopAsync();
            Check("reconnect preserves original card identity", ids.Count == 2 && ids.All(id => id == "fake-card"));
        }
        var backoffStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using (var session = new CaptureSession((_, _, _) => { calls++; throw new IOException("missing"); },
            (_, ct) => { backoffStarted.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); }))
        {
            session.Start(profile); await backoffStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await session.StopAsync();
            Check("stop cancels pending reconnect", calls == 1 && !session.IsActive);
        }
    }
    private sealed class FakeConnection : ICaptureConnection
    {
        public int Stopped, Disposed;
        public CaptureMode Mode => new(2560, 1440, 120, 1, "JPEG");
        public Task StopAsync() { Stopped++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var stop = DateTime.UtcNow.AddSeconds(3);
        while (!condition()) { if (DateTime.UtcNow > stop) throw new TimeoutException(); await Task.Delay(10); }
    }
    private static void Check(string name, bool passed)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + name);
        _passed++; Console.WriteLine("PASS: " + name);
    }
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Seein.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("SEEIN project root not found");
    }
}
