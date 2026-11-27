using Seein.Core;
using Seein.Core.Tools;
using Seein.Tray;
using System.IO;
using System.Reflection;
using System.Windows.Interop;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static void Main()
    {
        // No real hook, keyboard injection, foreground window, or production data.
        var root = Path.Combine(Path.GetTempPath(), "SeeinRightAlt-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SEEIN_INSTANCE", "RightAltTests");
        Environment.SetEnvironmentVariable("SEEIN_PREVIEW_USER_DATA", root);
        Directory.CreateDirectory(root);
        try
        {
            var toast = CaptureShortcutToast.Create("截图快捷键已暂停，双击右 Alt 可重新开启。");
            Check(!toast.ShowActivated && !toast.Focusable && !toast.IsHitTestVisible && !toast.ShowInTaskbar, "feedback cannot take game input focus");
            toast.Close();
            var source = new HwndSource(new HwndSourceParameters("SEEIN hotkey test") { Width = 1, Height = 1, WindowStyle = 0 });
            var left = 0; var right = 0;
            using (var listener = new ToolDoubleAltListener(source.Handle, () => left++, () => right++))
            {
                Set(listener, "_running", true); Set(listener, "_leftEnabled", true); Set(listener, "_rightEnabled", true); Set(listener, "_generation", (nuint)9);
                Message(listener, 9, 2); Check(left == 1 && right == 0, "left only calls clipboard");
                Message(listener, 9, 4); Check(left == 1 && right == 1, "right only toggles capture");
                Message(listener, 8, 4); Message(listener, 9, 1); Check(right == 1 && left == 1, "stale and legacy slots ignored");
                Set(listener, "_leftEnabled", false); Message(listener, 9, 2); Message(listener, 9, 4);
                Check(left == 1 && right == 2, "right switch independent of clipboard");
            }
            source.Dispose();
            lock (ToolRuntime.ActiveModules) { ToolRuntime.ActiveModules.Add("snow"); ToolRuntime.ActiveModules.Add("pane"); }
            using (var controller = new ToolShortcutController())
            {
                var bindings = (Dictionary<string, ShortcutBinding>)Get(controller, "_bindings")!;
                bindings["snow.capture"] = new("snow.capture", 1, 65);
                bindings["pane.clipboard"] = new("pane.clipboard", DoubleAlt: true);
                var captures = 0; var clips = 0; var notices = 0;
                controller.ExecuteRequested += action => { if (action == "snow.capture") captures++; if (action == "pane.clipboard") clips++; };
                controller.ShortcutStatusChanged += _ => notices++;
                Dispatch(controller, "snow.capture"); Check(captures == 1, "capture enabled initially");
                Dispatch(controller, CaptureShortcutGate.ToggleAction);
                Check(!CaptureShortcutGate.Load(Path.Combine(root, "capture-shortcuts.json")).Enabled, "pause saved to isolated SSD state");
                Dispatch(controller, "snow.capture"); Dispatch(controller, "pane.clipboard");
                Check(captures == 1 && clips == 1, "paused capture rejects queued action; clipboard unaffected");
                Check(controller.Items.First(i => i.Id == "snow.capture").Binding == bindings["snow.capture"], "pause preserves Alt+A binding");
                Dispatch(controller, CaptureShortcutGate.ToggleAction); Dispatch(controller, "snow.capture");
                Check(captures == 2 && notices == 2, "right switch resumes and reports both changes");
                Dispatch(controller, CaptureShortcutGate.ToggleAction);
            }
            using (var restarted = new ToolShortcutController())
            {
                Check(restarted.Items.First(i => i.Id == CaptureShortcutGate.ToggleAction).Name.Contains("已暂停"), "restart restores pause state");
                Dispatch(restarted, CaptureShortcutGate.ToggleAction);
                Check(CaptureShortcutGate.Load(Path.Combine(root, "capture-shortcuts.json")).Enabled, "restart retains resume switch");
            }
            Console.WriteLine($"PASS: {_checks} headless shortcut checks; no input injected.");
        }
        finally { ToolRuntime.Instance.Dispose(); Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; }
    private static object? Get(object target, string field) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static void Dispatch(ToolShortcutController controller, string action) => typeof(ToolShortcutController).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, [action]);
    private static void Message(ToolDoubleAltListener listener, int generation, int slot) => typeof(ToolDoubleAltListener).GetMethod("HandleMessage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(listener, [(nint)0, 0x8059, (nint)generation, (nint)slot, false]);
}
