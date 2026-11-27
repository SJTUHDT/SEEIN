using Seein.Contracts;
using Seein.Core.Ipc;
namespace Seein.Core.Hosting;

public interface IModuleMessageBus
{
    Task BroadcastAsync(PipeEnvelope message,CancellationToken token=default);
}
public sealed class ModuleServices(string moduleRoot,IServiceProvider provider) : IModuleServices
{
    public object? GetService(Type type)=>provider.GetService(type);
    public string InstallRoot=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    public string ModuleRoot=>moduleRoot;
    public string UserDataRoot=>AppPaths.UserDefault.DataRoot;
    public string SystemDataRoot=>AppPaths.SystemDefault.DataRoot;
}
public static class ModuleContextExtensions
{
    public static T Required<T>(this IModuleServices services) where T:class => services.GetService(typeof(T)) as T
        ?? throw new InvalidOperationException("模块缺少宿主服务："+typeof(T).Name);
}
