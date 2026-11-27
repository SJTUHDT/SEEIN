using Seein.Core;
using Seein.Modularity;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
namespace Seein.Tray;
internal static class ModuleMaintenance
{
    public static async Task RunAsync(string requestPath)
    {
        var log = Path.Combine(AppPaths.UserDefault.Logs, "module-maintenance.log"); Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var app = Path.Combine(root, "App");
        try
        {
            if (!File.Exists(Path.Combine(app, "SEEIN.modular"))) throw new InvalidOperationException("请先安装 2.1.0 模块化版本。");
            ModuleTransaction.Recover(root, Path.Combine(AppPaths.SystemDefault.DataRoot, "modules.json"));
            using var maintenanceLock = new FileStream(Path.Combine(root, ".module-maintenance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var change = JsonSerializer.Deserialize<ModuleChange>(File.ReadAllText(requestPath), ModulePackage.Json) ?? throw new InvalidDataException("插件操作无效。");
            using var transaction = new ModuleTransaction(root, Path.Combine(AppPaths.SystemDefault.DataRoot, "modules.json"), change);
            var expectedService=transaction.Imported?.Service??new ModuleCatalog(root,Path.Combine(AppPaths.SystemDefault.DataRoot,"modules.json")).Read().FirstOrDefault(m=>m.Manifest.Id==transaction.Id)?.Manifest.Service;
            using var index=transaction.Id=="efficiency"?new IndexModuleLifecycle(root):null;
            var tray = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath)).Where(p => { try { return p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase); } catch { return false; } }).ToArray();
            try { EventWaitHandle.OpenExisting(@"Local\Seein.Tray.Exit" + AppPaths.InstanceSuffix).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            foreach (var process in tray) { using (process) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
            using var service = new ServiceController("SeeinService"); var wasRunning = service.Status != ServiceControllerStatus.Stopped;
            try
            {
                if (wasRunning) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)); }
                index?.StopOwned();transaction.Apply();SharedDependencyStore.Reconcile(root);
                if(index is not null){var enabled=new ModuleCatalog(root,Path.Combine(AppPaths.SystemDefault.DataRoot,"modules.json")).Read().Any(m=>m.Manifest.Id=="efficiency"&&m.Enabled);await index.ApplyAsync(enabled);}
                if (wasRunning) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
                if(wasRunning&&expectedService is not null&&change.Operation is "import" or "enable")await WaitForModuleAsync(transaction.Id);
                transaction.Commit();if(index is not null&&change.Operation=="uninstall")await index.CompleteRemovalAsync(); File.AppendAllText(log, $"{DateTimeOffset.Now:O} {change.Operation} {transaction.Id}: applied\n");
            }
            catch
            {
                service.Refresh(); if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)); }
                transaction.Rollback(); SharedDependencyStore.Reconcile(root);if(index is not null)await index.RestoreAsync(); if (wasRunning) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
                throw;
            }
            StartTray();
        }
        catch (Exception ex) { File.AppendAllText(log, $"{DateTimeOffset.Now:O} failed: {ex}\n"); StartTray(); }
        static async Task WaitForModuleAsync(string id)
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",Seein.Core.Ipc.PipeNames.Service,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);using var writer=new StreamWriter(pipe,new System.Text.UTF8Encoding(false),4096,true){AutoFlush=true};using var reader=new StreamReader(pipe,System.Text.Encoding.UTF8,true,4096,true);
            while(!deadline.IsCancellationRequested)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(Seein.Core.PipeEnvelope.Create("moduleHealthRequest",new ModuleHealthRequest("maintenance")),JsonDefaults.Options));
                while(true)
                {
                    var line=await reader.ReadLineAsync(deadline.Token)??throw new IOException("插件后台连接已结束。");var response=JsonSerializer.Deserialize<PipeEnvelope>(line,JsonDefaults.Options);
                    if(response?.Type!="moduleHealthResult")continue;var health=response.Read<ModuleHealthResult>()!;
                    var module=health.Modules.SingleOrDefault(m=>m.Id==id);if(module?.Ready==true)return;if(module?.Error is not null)throw new IOException(module.Error);
                    // The pipe can accept clients before module discovery has begun. An empty
                    // first snapshot is not a failed start; keep waiting within the deadline.
                    break;
                }
                await Task.Delay(250,deadline.Token);
            }
            throw new System.TimeoutException("插件后台未能在规定时间内就绪。");
        }
        static void StartTray() { using var process = Process.Start(new ProcessStartInfo("schtasks.exe", "/Run /TN SeeinTray") { UseShellExecute = false, CreateNoWindow = true }); process?.WaitForExit(); }
    }
}
