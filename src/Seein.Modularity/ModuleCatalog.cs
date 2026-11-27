using System.Reflection;
using System.Runtime.Loader;
using Seein.Contracts;

namespace Seein.Modularity;

public sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly string directory;
    private readonly AssemblyDependencyResolver _resolver;
    private readonly List<string> _dependencies=[];
    private static readonly Dictionary<string,string> SharedPaths=new(StringComparer.OrdinalIgnoreCase);
    static ModuleLoadContext(){Default.Resolving+=(_,name)=>{lock(SharedGate)return name.Name is not null&&SharedPaths.TryGetValue(name.Name,out var path)?Default.LoadFromAssemblyPath(path):null;};}
    public ModuleLoadContext(string directory,string entryAssembly):base(isCollectible:false)
    {
        this.directory=directory;_resolver=new(Path.Combine(directory,entryAssembly));
        var entryName=Path.GetFileNameWithoutExtension(entryAssembly);
        Default.Resolving+=(_,name)=>name.Name==entryName?LoadFromAssemblyName(name):null;
        var manifestPath=Path.Combine(directory,"module.json");
        if(File.Exists(manifestPath))
        {
            var plugins=Directory.GetParent(directory)!.FullName;var root=Directory.GetParent(plugins)!.FullName;
            var key=File.ReadAllText(Path.Combine(root,"App","module-publisher.pem"));var seen=new HashSet<string>();
            void Register(string id)
            {
                if(!seen.Add(id))return;var path=Path.Combine(plugins,id);
                var manifest=ModulePackage.ReadVerified(path,key,HostVersion.Current);
                foreach(var child in manifest.Dependencies)Register(child.Id);
                _dependencies.Add(path);
                lock(SharedGate)foreach(var file in manifest.Files.Where(f=>f.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&!f.Path.Contains('/')))
                {
                    var name=Path.GetFileNameWithoutExtension(file.Path);var full=Path.Combine(path,file.Path);
                    if(SharedPaths.TryGetValue(name,out var old)&&!System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(old)).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))))throw new InvalidDataException("共享组件版本冲突："+name);
                    SharedPaths[name]=full;
                }
            }
            var own=System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(manifestPath),ModulePackage.Json)!;
            foreach(var d in own.Dependencies)Register(d.Id);
        }
    }
    private static readonly object SharedGate=new();
    protected override Assembly? Load(AssemblyName name)
    {
        if(name.Name is not null&&_dependencies.Any(d=>File.Exists(Path.Combine(d,name.Name+".dll"))))lock(SharedGate)
            return Default.Assemblies.FirstOrDefault(a=>a.GetName().Name==name.Name)??Default.LoadFromAssemblyPath(SharedPaths[name.Name]);
        // Windows caches COM wrappers by CLSID. Separate copies of NAudio's COM coclasses
        // cannot safely cast the same MMDeviceEnumerator RCW across module load contexts.
        // Audio endpoints and capture/playback therefore share the host's NAudio identity.
        // HidSharp also owns one process-wide notification window class. A second copy
        // registers that name again and terminates the service's HID thread.
        if(name.Name=="NAudio" || name.Name?.StartsWith("NAudio.",StringComparison.Ordinal)==true || name.Name=="HidSharp")
        {
            var shared=Default.LoadFromAssemblyName(name);
            if(name.Version is not null && shared.GetName().Version!=name.Version)
                throw new FileLoadException("设备共享库与宿主的版本不一致。",name.FullName);
            return shared;
        }
        if(name.Name=="Seein.Desktop.Tools")lock(SharedGate)
        {
            var sharedPath=Path.Combine(directory,name.Name+".dll");
            var existing=Default.Assemblies.FirstOrDefault(a=>a.GetName().Name==name.Name);
            if(existing is not null)
            {
                if(!System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(existing.Location)).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sharedPath))))throw new InvalidDataException("工具共享组件版本不一致。");
                return existing;
            }
            return Default.LoadFromAssemblyPath(sharedPath);
        }

        if(name.Name is "Seein.Contracts" or "Seein.Core" or "Seein.Modularity" or "Seein.Desktop.Shared"
           || name.Name?.StartsWith("Microsoft.Extensions.",StringComparison.Ordinal)==true)
            return Default.LoadFromAssemblyName(name);
        var path=_resolver.ResolveAssemblyToPath(name) ?? Path.Combine(directory,name.Name+".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        var resolved=_resolver.ResolveUnmanagedDllToPath(name);
        if(resolved is not null&&File.Exists(resolved))return LoadUnmanagedDllFromPath(resolved);
        foreach(var dependency in _dependencies)foreach(var leaf in new[]{name,name+".dll","lib"+name+".dll"})
            if(!leaf.Contains('/')&&!leaf.Contains('\\'))foreach(var relative in new[]{leaf,Path.Combine("runtimes","win-x64","native",leaf)})if(File.Exists(Path.Combine(dependency,relative)))return LoadUnmanagedDllFromPath(Path.Combine(dependency,relative));
        foreach(var candidate in new[]{name,name+".dll","lib"+name+".dll"})
            if(!candidate.Contains('/')&&!candidate.Contains('\\')&&File.Exists(Path.Combine(directory,candidate)))return LoadUnmanagedDllFromPath(Path.Combine(directory,candidate));
        return 0;
    }
    public T Create<T>(ModuleEntry entry) where T:class => Activator.CreateInstance(LoadFromAssemblyPath(Path.Combine(directory,entry.Assembly)).GetType(entry.Type,true)!) as T
        ?? throw new InvalidDataException("插件入口未实现所需接口。");
}

public sealed record InstalledModule(ModuleManifest Manifest,string Directory,bool Enabled,string? Error=null);
public sealed class ModuleCatalog(string installRoot,string settingsPath)
{
    public IReadOnlyList<InstalledModule> Read(bool verify=true)
    {
        var root=Path.Combine(installRoot,"Plugins");
        if(!System.IO.Directory.Exists(root))return [];
        Dictionary<string,ModuleState> states;string? stateError=null;
        try{states=LoadStates();}catch(InvalidDataException ex){states=[];stateError=ex.Message;}
        var result=new List<InstalledModule>();
        var keyPath=Path.Combine(installRoot,"App","module-publisher.pem");
        var publicKey=File.Exists(keyPath)?File.ReadAllText(keyPath):"";
        foreach(var directory in System.IO.Directory.GetDirectories(root).OrderBy(x=>x,StringComparer.Ordinal))
        {
            if(Path.GetFileName(directory).StartsWith('.'))continue;
            try
            {
                var manifest=verify ? ModulePackage.ReadVerified(directory,publicKey,HostVersion.Current)
                    : System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(Path.Combine(directory,"module.json")),ModulePackage.Json)!;
                if(Path.GetFileName(directory)!=manifest.Id)throw new InvalidDataException("插件目录与标识不一致。");
                result.Add(new(manifest,directory,stateError is null&&(!states.TryGetValue(manifest.Id,out var state)||state.Enabled),stateError));
            }
            catch(Exception ex) when(ex is not OutOfMemoryException)
            {
                var id=Path.GetFileName(directory);
                result.Add(new(new(id,id,"0.0.0",1,"2.1.0",null,null,[],[]),directory,false,ex.Message));
            }
        }
        var enabled=result.Where(m=>m.Enabled).ToDictionary(m=>m.Manifest.Id,StringComparer.Ordinal);
        foreach(var legacy in PluginFeed.Legacy)if(enabled.ContainsKey(legacy.Key))foreach(var id in legacy.Value)
            if(enabled.TryGetValue(id,out var overlapping)){var index=result.IndexOf(overlapping);result[index]=overlapping with{Enabled=false,Error="旧整包仍已启用，请通过插件中心统一迁移。"};}
        enabled=result.Where(m=>m.Enabled).ToDictionary(m=>m.Manifest.Id,StringComparer.Ordinal);
        foreach(var module in enabled.Values)
        {
            try
            {
                var closure=new Dictionary<string,ModuleManifest>();
                void Collect(ModuleManifest current){if(!closure.TryAdd(current.Id,current))return;foreach(var d in current.Dependencies)if(enabled.TryGetValue(d.Id,out var dependency))Collect(dependency.Manifest);}
                Collect(module.Manifest);ModuleValidation.Order(closure.Values);
            }
            catch(InvalidDataException ex){var index=result.IndexOf(module);result[index]=module with{Enabled=false,Error=ex.Message};}
        }
        var sharing=result.Where(m=>m.Enabled&&m.Manifest.Files.Any(f=>f.Path=="Seein.Desktop.Tools.dll")).ToArray();
        try{ModuleValidation.SharedAssemblies(sharing.Select(m=>m.Manifest));}
        catch(InvalidDataException ex){foreach(var module in sharing){var index=result.IndexOf(module);result[index]=module with{Enabled=false,Error=ex.Message};}}
        return result;
    }
    public Dictionary<string,ModuleState> LoadStates()
    {
        if(!File.Exists(settingsPath))return new(StringComparer.Ordinal);
        try{return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,ModuleState>>(File.ReadAllText(settingsPath),ModulePackage.Json)??new(StringComparer.Ordinal);}
        catch(System.Text.Json.JsonException){throw new InvalidDataException("插件启用状态文件无效，原文件已保留。");}
    }
    public void SaveStates(Dictionary<string,ModuleState> states)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temp=settingsPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllBytes(temp,System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(states,ModulePackage.Json));File.Move(temp,settingsPath,true);}
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
}

