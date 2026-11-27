using Seein.Contracts;
using Seein.Core.Tools;
namespace Seein.Tray;
public abstract class ToolDesktopModule : DesktopModule
{
    protected abstract string Tool { get; }
    private ToolboxView? _view;
    private static ToolShortcutController? Controller;
    private static IDesktopHost? Shell;
    public override IEnumerable<ModulePage> Pages => [Page("tool-" + Tool, Tool == "snow" ? "截图录屏" : "效率工具", "小工具", () => { _view ??= new ToolboxView(); _view.Open(Tool); return _view; })];
    protected override void Initialize()
    {
        lock (ToolRuntime.ActiveModules) ToolRuntime.ActiveModules.Add(Tool);
        if (Controller is null)
        {
            Shell = Host; Controller = new(); Controller.ExecuteRequested += Execute;
            Controller.ShortcutStatusChanged += message =>
            {
                Shell.Notify("SEEIN 截图快捷键", message, false);
                ShortcutSettingsView.NotifyBindingsChanged();
                CaptureShortcutToast.Show(message);
            };
            Controller.Initialize(Host.Handle);
            Host.AddHotkeyHandler(Controller.Handle); Host.AddShortcutProvider("tools", new(() => Controller.Items, binding => (Controller.Save(binding), Controller.Status)));
            ToolRuntime.Instance.SettingsRequested += SettingsRequested;
        }
        else Controller.InitializeAdditionalModule();
        if (Tool == "snow") Host.CaptureCommand = (operation, action, values) => ToolRuntime.Instance.SendAsync("snow", operation, action, values);
    }
    private static void SettingsRequested(string module) => Shell!.Window.Dispatcher.BeginInvoke(() => { Shell.ShowWindow(); Shell.Navigate("tool-" + module); });
    private static async void Execute(string action)
    {
        var response = await ToolRuntime.Instance.ExecuteAsync(action); if (!response.Success) Shell!.Notify("SEEIN 小工具", response.Error ?? "操作失败", true);
    }
    public override Task StartAsync(CancellationToken token) => Tool == "pane" ? ToolRuntime.Instance.StartBackgroundAsync() : ToolRuntime.Instance.WarmSettingsAsync(Tool, token);
    public override Task PrepareStopAsync(CancellationToken token) => Tool == "snow" ? ToolRuntime.Instance.StopAsync(Tool) : Task.CompletedTask;
    public override Task StopAsync(CancellationToken token) => ToolRuntime.Instance.StopAsync(Tool);
    public override void Dispose()
    {
        lock (ToolRuntime.ActiveModules) ToolRuntime.ActiveModules.Remove(Tool);
        if (ToolRuntime.ActiveModules.Count == 0) { ToolRuntime.Instance.SettingsRequested -= SettingsRequested; Controller?.Dispose(); Controller = null; ToolRuntime.Instance.Dispose(); }
        base.Dispose();
    }
}
