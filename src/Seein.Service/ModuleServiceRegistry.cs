using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seein.Contracts;
using Seein.Core;
using Seein.Core.Hosting;
using Seein.Modularity;
namespace Seein.Service;

public sealed class ModuleServiceRegistry(IServiceProvider provider, ILogger<ModuleServiceRegistry> logger)
{
    private sealed class Slot(InstalledModule installed, IServiceModule module)
    {
        public InstalledModule Installed = installed;
        public IServiceModule Module = module;
        public volatile bool Ready;
        public string? Error;
    }
    private readonly List<Slot> _slots = [];
    private readonly Dictionary<string,string> _loadFailures=[];
    public ModuleHealthResult Health(string? requestId){lock(_slots)return new(_starting is {IsCompleted:false},_slots.Select(s=>new ModuleRuntimeStatus(s.Installed.Manifest.Id,s.Ready,s.Error)).Concat(_loadFailures.Select(p=>new ModuleRuntimeStatus(p.Key,false,p.Value))).ToArray(),requestId);}
    private readonly CancellationTokenSource _stop = new();
    private Task? _starting;
    public void Start(CancellationToken token)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        _starting = Task.Run(async () =>
        {
            using (linked)
            {
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
                var catalog = new ModuleCatalog(root, Path.Combine(AppPaths.SystemDefault.DataRoot, "modules.json"));
                var installed = catalog.Read();
                foreach (var installedModule in installed.Where(m => m.Enabled && m.Manifest.Service is not null))
                {
                    try
                    {
                        var entry = installedModule.Manifest.Service!;
                        var context = new ModuleLoadContext(installedModule.Directory, entry.Assembly);
                        var module = context.Create<IServiceModule>(entry);
                        var slot = new Slot(installedModule, module); lock (_slots) _slots.Add(slot);
                    }
                    catch (Exception ex) { lock(_slots)_loadFailures[installedModule.Manifest.Id]=ex.Message;logger.LogError(ex, "Module {Module} could not load", installedModule.Manifest.Id); }
                }
                Slot[] slots; lock (_slots) slots = _slots.ToArray();
                // Each module starts independently; slow optional-module startup cannot block sampling.
                var ordered = ModuleValidation.Order(installed.Where(m=>m.Enabled).Select(s=>s.Manifest)).Where(m=>slots.Any(s=>s.Installed.Manifest.Id==m.Id));
                var starts = new Dictionary<string, Task>();
                foreach (var manifest in ordered)
                {
                    var slot = slots.Single(s => s.Installed.Manifest.Id == manifest.Id);
                    var dependencies = manifest.Dependencies.Where(d=>starts.ContainsKey(d.Id)).Select(d => starts[d.Id]).ToArray();
                    starts[manifest.Id] = Task.Run(() => StartSlotAsync(slot, dependencies), CancellationToken.None);
                }
                await Task.WhenAll(starts.Values);
                async Task StartSlotAsync(Slot slot, Task[] dependencies)

                {
                    try { await Task.WhenAll(dependencies); if (slot.Installed.Manifest.Dependencies.Any(d => slots.Any(s=>s.Installed.Manifest.Id==d.Id&&!s.Ready))) throw new InvalidOperationException("Required module did not start."); await slot.Module.StartAsync(new ModuleServices(slot.Installed.Directory, provider), linked.Token); slot.Ready = true; logger.LogInformation("Module {Module} ready", slot.Installed.Manifest.Id); }
                    catch (Exception ex) { slot.Error=ex.Message;logger.LogError(ex, "Module {Module} startup failed", slot.Installed.Manifest.Id); try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await slot.Module.StopAsync(timeout.Token); await slot.Module.DisposeAsync(); } catch (Exception cleanup) { logger.LogError(cleanup, "Module startup cleanup failed"); } }
                }
            }
        }, CancellationToken.None);
    }
    public async Task RouteAsync(PipeEnvelope envelope, CancellationToken token)
    {
        var name = JsonNamingPolicy.CamelCase.ConvertName(envelope.Type.ToString());
        Slot[] slots; lock (_slots) slots = _slots.Where(s => s.Ready && s.Module.CanHandle(name)).ToArray();
        foreach (var slot in slots)
            try { await slot.Module.HandleAsync(name, envelope.Payload, token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Module {Module} request failed", slot.Installed.Manifest.Id); }
    }
    public async Task StopAsync(CancellationToken token)
    {
        _stop.Cancel();
        if (_starting is not null) try { await _starting.WaitAsync(token); } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        Slot[] slots; lock (_slots) { slots = _slots.Where(s=>s.Ready).AsEnumerable().Reverse().ToArray(); }
        foreach (var slot in slots)
        {
            try { await slot.Module.StopAsync(token); }
            catch (Exception ex) { logger.LogError(ex, "Module {Module} shutdown failed", slot.Installed.Manifest.Id); }
            finally { slot.Ready = false; await slot.Module.DisposeAsync(); }
        }
    }
}
