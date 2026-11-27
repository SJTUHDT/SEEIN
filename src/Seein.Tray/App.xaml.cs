using System.Globalization;
using System.Windows;

namespace Seein.Tray;

public partial class App : System.Windows.Application
{
    // The suffix lets a development build run next to the installed tray application.
    private static readonly string InstanceSuffix = Seein.Core.AppPaths.InstanceSuffix;
    private static readonly string InstanceMutexName = @"Local\Seein.Tray" + InstanceSuffix;
    private static readonly string ActivateEventName = @"Local\Seein.Tray.Activate" + InstanceSuffix;
    private static readonly string ExitEventName = @"Local\Seein.Tray.Exit" + InstanceSuffix;
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _exitEvent;
    private readonly CancellationTokenSource _activationShutdown = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        if(e.Args.Contains("--module-pool",StringComparer.Ordinal))
        {Seein.Modularity.SharedDependencyStore.Reconcile(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,"..")));Shutdown();return;}
        var maintenance=e.Args.FirstOrDefault(a=>a.StartsWith("--module-maintenance=",StringComparison.Ordinal)); if(maintenance is not null){await ModuleMaintenance.RunAsync(maintenance[21..]);Shutdown();return;}
        if (e.Args.Contains("--display-host", StringComparer.OrdinalIgnoreCase))
        {
            RunDisplayHost();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            try { EventWaitHandle.OpenExisting(ExitEventName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        var startInTray = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        _singleInstance = new Mutex(true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            if (!startInTray)
            {
                try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
            }
            Shutdown();
            return;
        }

        AnimatedReorder.RegisterPageStrips();
        base.OnStartup(e);
        _ = StartupStorageArchive.RunAsync();
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var window = new MainWindow();
        MainWindow = window;
        _ = Task.Run(WaitForActivationAsync);

        // The scheduled logon task creates only the tray icon.  A normal shortcut launch
        // still opens the management window, or activates it when it is already running.
        if (startInTray) window.Hide();
        else window.Show();
        var page = e.Args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        if (page is not null) window.NavigateTo(page[7..]);
    }

    private static void RunDisplayHost()
    {
        var path=Seein.Core.ProcessNames.ModuleDirectory("devices");
        var entry=new Seein.Contracts.ModuleEntry("Seein.Desktop.Devices.dll","Seein.Tray.DisplayNativeHost");
        var context=new Seein.Modularity.ModuleLoadContext(path,entry.Assembly);
        context.LoadFromAssemblyPath(System.IO.Path.Combine(path,entry.Assembly)).GetType(entry.Type,true)!.GetMethod("Run")!.Invoke(null,null);
    }
    private async Task WaitForActivationAsync()
    {
        while (!_activationShutdown.IsCancellationRequested)
        {
            var result = WaitHandle.WaitAny([_activateEvent!, _exitEvent!, _activationShutdown.Token.WaitHandle]);
            if (result == 2) break;
            if (result == 1) await Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.ExitFromExternalRequest());
            else await Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.ShowFromExternalRequest());
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StartupStorageArchive.Stop();
        _activationShutdown.Cancel();
        _activateEvent?.Dispose();
        _exitEvent?.Dispose();
        if (MainWindow is MainWindow window) window.Dispose();
        _singleInstance?.Dispose();
        _activationShutdown.Dispose();
        base.OnExit(e);
    }
}


