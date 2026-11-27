using Seein.Core;
using Seein.Modularity;
using Seein.Contracts;
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
            var requestBytes=File.ReadAllBytes(requestPath);if(requestBytes.Length>1024*1024)throw new InvalidDataException("安装请求过大。");
            using var request=JsonDocument.Parse(requestBytes);
            var changes=request.RootElement.TryGetProperty("changes",out _)?JsonSerializer.Deserialize<ModuleChangeSet>(requestBytes,ModulePackage.Json)!:new ModuleChangeSet([JsonSerializer.Deserialize<ModuleChange>(requestBytes,ModulePackage.Json)!]);
            using var transaction = new ModuleBatchTransaction(root, Path.Combine(AppPaths.SystemDefault.DataRoot, "modules.json"), changes);
            var before=new ModuleCatalog(root,Path.Combine(AppPaths.SystemDefault.DataRoot,"modules.json")).Read();
            var changed=transaction.Changes.Select(c=>c.Id!).ToHashSet();var graph=before.ToDictionary(m=>m.Manifest.Id,m=>m.Manifest);
            bool Affected(ModuleManifest module,HashSet<string> seen){if(!seen.Add(module.Id))return false;return changed.Contains(module.Id)||module.Dependencies.Any(d=>graph.TryGetValue(d.Id,out var dependency)&&Affected(dependency,seen));}
            var touchesService=transaction.Imported.Any(m=>m.Service is not null)||before.Any(m=>m.Manifest.Service is not null&&Affected(m.Manifest,[]));
            using var index=transaction.Changes.Any(c=>c.Id=="efficiency")?new IndexModuleLifecycle(root):null;
            var tray = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath)).Where(p => { try { return p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase); } catch { return false; } }).ToArray();
            try { EventWaitHandle.OpenExisting(@"Local\Seein.Tray.Exit" + AppPaths.InstanceSuffix).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            foreach (var process in tray) { using (process) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
            using var service = new ServiceController("SeeinService"); var wasRunning = service.Status != ServiceControllerStatus.Stopped;
            try
            {
                if (wasRunning&&touchesService) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)); }
                index?.StopOwned();transaction.Apply();SharedDependencyStore.Reconcile(root);
                if(index is not null){var enabled=new ModuleCatalog(root,Path.Combine(AppPaths.SystemDefault.DataRoot,"modules.json")).Read().Any(m=>m.Manifest.Id=="efficiency"&&m.Enabled);await index.ApplyAsync(enabled);}
                if (wasRunning&&touchesService) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
                var active=new ModuleCatalog(root,Path.Combine(AppPaths.SystemDefault.DataRoot,"modules.json")).Read();
                foreach(var module in active.Where(m=>m.Enabled&&m.Manifest.Service is not null&&transaction.Changes.Any(c=>c.Id==m.Manifest.Id)))if(wasRunning)await WaitForModuleAsync(module.Manifest.Id);
                if(index is not null&&transaction.Changes.Any(c=>c.Id=="efficiency"&&c.Operation=="uninstall"))await index.CompleteRemovalAsync();transaction.Commit();
                var incoming=new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(requestPath))!);
                if(incoming.Name.Equals("incoming",StringComparison.OrdinalIgnoreCase)&&incoming.Parent is not null)
                {
                    var cache=Path.Combine(incoming.Parent.FullName,"PluginDownloads")+Path.DirectorySeparatorChar;
                    foreach(var change in transaction.Changes.Where(c=>c.Operation=="import"&&c.Package is not null))
                    {
                        var package=Path.GetFullPath(change.Package!);
                        if(package.StartsWith(cache,StringComparison.OrdinalIgnoreCase)&&System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(package),@"\A[a-f0-9]{64}\.seeinplugin\z"))
                            try{File.Delete(package);}catch(Exception cleanup)when(cleanup is IOException or UnauthorizedAccessException){File.AppendAllText(log,"Cache cleanup deferred: "+cleanup.Message+"\n");}
                    }
                }
                File.AppendAllText(log, $"{DateTimeOffset.Now:O} batch {string.Join(',',transaction.Changes.Select(c=>c.Id))}: applied\n");
            }
            catch
            {
                service.Refresh(); if (touchesService&&service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)); }
                transaction.Rollback(); SharedDependencyStore.Reconcile(root);if(index is not null)await index.RestoreAsync(); if (wasRunning&&touchesService) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
                throw;
            }
            File.WriteAllText(requestPath+".result",JsonSerializer.Serialize(new{success=true,message="插件安装完成。"}));StartTray();
        }
        catch (Exception ex) { File.AppendAllText(log, $"{DateTimeOffset.Now:O} failed: {ex}\n");try{File.WriteAllText(requestPath+".result",JsonSerializer.Serialize(new{success=false,message=ex.Message}));}catch(IOException){}StartTray(); }
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
