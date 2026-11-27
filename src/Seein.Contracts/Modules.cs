namespace Seein.Contracts;

public static class ModuleApi { public const int Version = 1; }
public sealed record ModuleEntry(string Assembly, string Type);
public sealed record ModuleDependency(string Id, string MinimumVersion);
public sealed record ModuleFile(string Path, string Sha256, long Length);
public sealed record ModuleManifest(string Id, string Name, string Version, int ApiVersion,
    string MinimumHostVersion, ModuleEntry? Desktop, ModuleEntry? Service,
    IReadOnlyList<ModuleDependency> Dependencies, IReadOnlyList<ModuleFile> Files,
    bool Private = false);
public sealed record ModuleState(bool Enabled = true);
public sealed record ModuleInventory(string Id, string Name, string Version, bool Enabled, string? Error = null);
public sealed record ModulePage(string Route, string Title, string Section, Func<object> Create);
public sealed record ModuleAction(string Id, string Label, Func<CancellationToken,Task> Execute);
public interface IModuleServices
{
    object? GetService(Type type);
    string InstallRoot { get; }
    string ModuleRoot { get; }
    string UserDataRoot { get; }
    string SystemDataRoot { get; }
}
public interface IDesktopModule : IDisposable
{
    void Initialize(IModuleServices services);
    IEnumerable<ModulePage> Pages { get; }
    IEnumerable<ModuleAction> Actions { get; }
    Task StartAsync(CancellationToken token);
    Task PrepareStopAsync(CancellationToken token)=>Task.CompletedTask;
    Task StopAsync(CancellationToken token);
}
public interface IServiceModule : IAsyncDisposable
{
    Task StartAsync(IModuleServices services, CancellationToken token);
    bool CanHandle(string messageType);
    Task HandleAsync(string messageType, System.Text.Json.JsonElement payload, CancellationToken token);
    Task StopAsync(CancellationToken token);
}
