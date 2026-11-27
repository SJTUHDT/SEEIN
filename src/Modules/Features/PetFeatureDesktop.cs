using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Seein.Tray;

public sealed class PetFeatureDesktop : DesktopModule
{
    private PetControlView? _view;
    private PetControlView DafeiyuPage => _view ??= new PetControlView();
    protected override void Initialize(){_desktopPet.SettingsRequested+=RequestPetSettings;Host.AddTrayItem(CreatePetTrayMenu());}
    private System.Windows.Controls.DockPanel? _pageView;
    private System.Windows.FrameworkElement PetPage()
    {
        if(_pageView is not null)return _pageView;_pageView=new();
        var settings=new System.Windows.Controls.Button{Content="大肥鱼连接设置",HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new(0,0,0,10)};
        settings.Click+=(_,_)=>new CompanionConnectionDialog(Host.Window).ShowDialog();
        System.Windows.Controls.DockPanel.SetDock(settings,System.Windows.Controls.Dock.Top);_pageView.Children.Add(settings);_pageView.Children.Add(DafeiyuPage);return _pageView;
    }
    public override IEnumerable<Seein.Contracts.ModulePage> Pages=>[Page("tool-pet","桌宠","小工具",PetPage)];
    public override async Task StartAsync(CancellationToken token){if(_desktopPet.StartAtLogin)try{await _desktopPet.StartAsync();}catch(InvalidOperationException){}}
    public override async Task StopAsync(CancellationToken token){await _desktopPet.StopAsync();}
    public override void Dispose(){_desktopPet.SettingsRequested-=RequestPetSettings;_view?.ReleaseView();base.Dispose();}
    private readonly DesktopPetRuntime _desktopPet = DesktopPetRuntime.Instance;
    private const string PetConfigPath = "/dsh-pet-7340/config";
    private const string PetDisplayPath = "/dsh-pet-7340/pet-display";

    private void RequestPetSettings() => Dispatcher.BeginInvoke(new Action(() => OpenPetSettings(model: !_desktopPet.IsNative)));

    private void OpenPetSettings(bool model)
    {
        if (_shutdown.IsCancellationRequested) return;
        Host.ShowWindow();
        if (model) RaisePetSettingsWindow();
        Host.Navigate("tool-pet");
        
    }

    private void RaisePetSettingsWindow()
    {
        var handle = new WindowInteropHelper(Host.Window).Handle;
        if (handle == IntPtr.Zero || GetForegroundWindow() == handle || SetForegroundWindow(handle)) return;

        // Windows may leave keyboard focus with the Electron window that received the click.
        // Raise SEEIN in the visual order, then return it to the normal window band.
        const uint positioning = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE, NOMOVE, NOACTIVATE, NOOWNERZORDER
        if (SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, positioning))
            SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, positioning);
        Host.Window.Activate();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    private Forms.ToolStripMenuItem CreatePetTrayMenu()
    {
        var menu = FluentTrayMenu.Item("桌宠", "\uE8D4");
        FluentTrayMenu.StyleSubmenu(menu);
        menu.DropDownItems.Add(FluentTrayMenu.Item("桌宠设置", "\uE713", (_, _) => OpenPetSettings(model: false)));
        menu.DropDownItems.Add(new Forms.ToolStripSeparator());
        var start = FluentTrayMenu.Item("启动桌宠", "\uE768", async (_, _) => await RunPetTrayActionAsync(() => _desktopPet.StartAsync()));
        var stop = FluentTrayMenu.Item("停止桌宠", "\uE71A", async (_, _) => await RunPetTrayActionAsync(_desktopPet.DisableAsync));
        var show = FluentTrayMenu.Item("显示桌宠", "\uE890", async (_, _) => await RunPetTrayActionAsync(() => _desktopPet.CallAsync("show")));
        var hide = FluentTrayMenu.Item("隐藏桌宠", "\uED1A", async (_, _) => await RunPetTrayActionAsync(() => _desktopPet.CallAsync("hide")));
        menu.DropDownItems.AddRange([start, stop, show, hide]);
        var pets = FluentTrayMenu.Item("按宠物显示", "\uE8EF");
        FluentTrayMenu.StyleSubmenu(pets);
        menu.DropDownItems.Add(pets);
        menu.DropDownOpening += (_, _) =>
        {
            var running = _desktopPet.IsRunning;
            start.Enabled = !running;
            stop.Enabled = show.Enabled = hide.Enabled = pets.Enabled = running;
        };
        pets.DropDownItems.Add(DisabledTrayItem("正在读取…"));
        pets.DropDownOpening += async (_, _) => await UpdatePetTrayEntriesAsync(pets);
        return menu;
    }

    private async Task UpdatePetTrayEntriesAsync(Forms.ToolStripMenuItem menu)
    {
        ClearPetTrayEntries(menu);
        menu.DropDownItems.Add(DisabledTrayItem("正在读取…"));
        try
        {
            var response = await _desktopPet.CallAsync("request", "GET", PetConfigPath);
            using var config = ReadPetResponse(response);
            if (menu.IsDisposed || !menu.DropDown.Visible) return;
            ClearPetTrayEntries(menu);
            foreach (var section in config.RootElement.EnumerateObject())
            {
                if (section.Value.ValueKind != JsonValueKind.Object ||
                    !section.Value.TryGetProperty("pets", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out var idValue) || idValue.GetString() is not { Length: > 0 } id) continue;
                    var name = entry.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                    var display = entry.TryGetProperty("display", out var displayValue) ? displayValue.GetString() : null;
                    var selection = FluentTrayMenu.Item((name ?? id).Replace("&", "&&"));
                    selection.Checked = display is "desktop" or "both";
                    var next = display switch { "desktop" => "none", "both" => "web", "web" => "both", _ => "desktop" };
                    selection.Click += async (_, _) => await RunPetTrayActionAsync(async () =>
                    {
                        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, display = next });
                        var result = await _desktopPet.CallAsync("request", "POST", PetDisplayPath, payload, "application/json");
                        using var applied = ReadPetResponse(result);
                    });
                    menu.DropDownItems.Add(selection);
                }
            }
            if (menu.DropDownItems.Count == 0)
                menu.DropDownItems.Add(DisabledTrayItem("没有可切换的桌宠"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            if (menu.IsDisposed || !menu.DropDown.Visible) return;
            ClearPetTrayEntries(menu);
            menu.DropDownItems.Add(DisabledTrayItem("无法读取桌宠配置"));
        }
    }

    private static Forms.ToolStripMenuItem DisabledTrayItem(string text)
    {
        var item = FluentTrayMenu.Item(text);
        item.Enabled = false;
        return item;
    }

    private static void ClearPetTrayEntries(Forms.ToolStripMenuItem menu)
    {
        while (menu.DropDownItems.Count > 0)
        {
            var item = menu.DropDownItems[0];
            menu.DropDownItems.RemoveAt(0);
            item.Dispose();
        }
    }

    private static JsonDocument ReadPetResponse(JsonElement response)
    {
        if (response.GetProperty("status").GetInt32() != 200)
            throw new InvalidOperationException("桌宠未能完成该操作。");
        var document = JsonDocument.Parse(response.GetProperty("body").GetBytesFromBase64());
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            (!document.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False))
            return document;
        document.Dispose();
        throw new InvalidOperationException("桌宠未能完成该操作。");
    }

    private async Task RunPetTrayActionAsync(Func<Task> action)
    {
        try
        {
            await action();
            await DafeiyuPage.RefreshAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            if (!_shutdown.IsCancellationRequested)
                Host.Notify("桌宠",ex.Message,true);
        }
    }
}
