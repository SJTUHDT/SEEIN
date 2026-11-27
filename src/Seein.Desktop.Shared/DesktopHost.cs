using Seein.Contracts;
using Seein.Core;
using Seein.Core.Ipc;
using Seein.Core.Tools;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
namespace Seein.Tray;
public sealed record ShortcutItem(string Id, string Name, string Group, ShortcutBinding Binding);
public sealed record ShortcutProvider(Func<IEnumerable<ShortcutItem>> Items, Func<ShortcutBinding, (bool Success, string Message)> Save);
public interface IDesktopHost
{
    Window Window { get; }
    PanelPipeClient Pipe { get; }
    CancellationTokenSource Shutdown { get; }
    TextBlock Status { get; }
    string ActiveRoute { get; }
    nint Handle { get; }
    event Action<PipeEnvelope>? Message;
    event Action? Connected;
    void Navigate(string route);
    void ShowWindow();
    void ShowContent(string route, FrameworkElement view, string title, string section);
    void Notify(string title, string text, bool error = false);
    void AddTrayItem(Forms.ToolStripMenuItem item);
    void AddShortcutProvider(string id, ShortcutProvider provider);
    void AddHotkeyHandler(Func<nint, bool> handler);
    IEnumerable<ShortcutItem> Shortcuts { get; }
    (bool Success, string Message) SaveShortcut(ShortcutBinding binding);
    Func<string, string, System.Text.Json.Nodes.JsonObject?, Task<ToolResponse>>? CaptureCommand { get; set; }
}
public abstract class DesktopModule : IDesktopModule
{
    protected IDesktopHost Host { get; private set; } = null!;
    protected IModuleServices Services { get; private set; } = null!;
    protected PanelPipeClient _pipe => Host.Pipe;
    protected CancellationTokenSource _shutdown => Host.Shutdown;
    protected TextBlock StatusText => Host.Status;
    protected string _page => Host.ActiveRoute;
    protected System.Windows.Threading.Dispatcher Dispatcher => Host.Window.Dispatcher;
    public void Initialize(IModuleServices services)
    {
        Services = services; Host = (IDesktopHost)services.GetService(typeof(IDesktopHost))!;
        Host.Message += HandleMessage; Host.Connected += RefreshCurrentPageData; Initialize();
    }
    protected virtual void Initialize() { }
    protected virtual void HandleMessage(PipeEnvelope message) { }
    protected virtual void RefreshCurrentPageData() { }
    public abstract IEnumerable<ModulePage> Pages { get; }
    public virtual IEnumerable<ModuleAction> Actions => [];
    public virtual Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public virtual Task PrepareStopAsync(CancellationToken token) => Task.CompletedTask;
    public virtual Task StopAsync(CancellationToken token) => Task.CompletedTask;
    protected void ShowPage(FrameworkElement page, object? nav, string key, string title, string subtitle) => Host.ShowContent(key, page, title, Pages.FirstOrDefault(p => p.Route == key)?.Section ?? title);
    protected ModulePage Page(string route, string title, string section, Func<FrameworkElement> create, Action? open = null) => new(route, title, section, () => { var view = create(); open?.Invoke(); return view; });
    public virtual void Dispose() { Host.Message -= HandleMessage; Host.Connected -= RefreshCurrentPageData; }
}
