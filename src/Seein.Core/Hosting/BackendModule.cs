using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seein.Contracts;
namespace Seein.Core.Hosting;

public abstract class BackendModule : IServiceModule
{
    private ServiceProvider? _provider;
    private readonly List<IHostedService> _workers=[];
    protected IModuleServices Context {get;private set;}=null!;
    protected ModuleSettingsStore Settings=>Context.Required<ModuleSettingsStore>();
    protected PanelSettings CurrentSettings=>Settings.Current;
    protected T Required<T>()where T:class =>_provider!.GetRequiredService<T>();
    protected abstract void Configure(IServiceCollection services);
    protected virtual Task InitializeAsync(CancellationToken token)=>Task.CompletedTask;
    public async Task StartAsync(IModuleServices context,CancellationToken token)
    {
        Context=context;
        var services=new ServiceCollection();services.AddLogging();services.AddSingleton(context.Required<ILoggerFactory>());
        services.AddSingleton(context.Required<Seein.Service.PanelState>());
        Configure(services);_provider=services.BuildServiceProvider();
        await InitializeAsync(token);
        foreach(var worker in _provider.GetServices<IHostedService>()) {await worker.StartAsync(token);_workers.Add(worker);}
    }
    public abstract bool CanHandle(string messageType);
    public abstract Task HandleAsync(string messageType,System.Text.Json.JsonElement payload,CancellationToken token);
    public virtual async Task StopAsync(CancellationToken token)
    {
        foreach(var worker in _workers.AsEnumerable().Reverse())await worker.StopAsync(token);
        _workers.Clear();
    }
    public async ValueTask DisposeAsync(){if(_provider is not null){await _provider.DisposeAsync();_provider=null;}}
}
public sealed class ModuleMessageBus(IModuleMessageBus bus)
{
    public Task BroadcastAsync<T>(string type,T value,CancellationToken token=default)=>bus.BroadcastAsync(PipeEnvelope.Create(type,value),token);
}
public sealed class SharedHardware
{
    public Sensors.HardwareSensorCollector? Sensors {get;set;}
    private readonly object _gate=new();
    private readonly Dictionary<string,Func<SensorSnapshot,SensorSnapshot>> _enrichers=[];
    public void Register(string id,Func<SensorSnapshot,SensorSnapshot> enrich){lock(_gate)_enrichers[id]=enrich;}
    public void Remove(string id){lock(_gate)_enrichers.Remove(id);}
    public SensorSnapshot Read()
    {
        var snapshot=Sensors?.Read()??SensorSnapshot.Empty;
        Func<SensorSnapshot,SensorSnapshot>[] enrichers;lock(_gate)enrichers=_enrichers.Values.ToArray();
        foreach(var enrich in enrichers)snapshot=enrich(snapshot);
        return snapshot;
    }
}
