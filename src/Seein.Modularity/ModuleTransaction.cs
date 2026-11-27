using Seein.Contracts;
using System.Text.Json;
namespace Seein.Modularity;
public sealed record ModuleChange(string Operation,string? Id=null,string? Package=null,bool? Enabled=null);
public sealed class ModuleTransaction : IDisposable
{
    private readonly string _root,_settings,_transaction,_plugins;
    private readonly Dictionary<string,ModuleState> _before,_after;
    private readonly string _id;
    private readonly bool _hadSettings;
    private string? _incoming,_previous;
    private bool _applied,_committed;
    public ModuleManifest? Imported {get;}
    public string Id=>_id;
    public ModuleTransaction(string root,string settings,ModuleChange change)
    {
        _root=Path.GetFullPath(root);_plugins=Path.Combine(_root,"Plugins");_settings=settings;
        if(change.Operation is not ("import" or "enable" or "disable" or "uninstall"))throw new InvalidDataException("不支持的插件操作。");
        Directory.CreateDirectory(_plugins);_transaction=Path.Combine(_plugins,".transactions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(_transaction);
        var catalog=new ModuleCatalog(_root,settings);_hadSettings=File.Exists(settings);_before=catalog.LoadStates();_after=new(_before,StringComparer.Ordinal);
        try
        {
            if(_hadSettings)File.Copy(settings,Path.Combine(_transaction,"states.before.json"));
            if(change.Operation=="import")
            {
                _incoming=Path.Combine(_transaction,"incoming");
                Imported=ModulePackage.ExtractVerified(change.Package??throw new InvalidDataException("未选择插件包。"),_incoming,File.ReadAllText(Path.Combine(_root,"App","module-publisher.pem")),HostVersion.Current);
                _id=Imported.Id;_after[_id]=new(true);
            }
            else
            {
                _id=change.Id??"";
                if(!System.Text.RegularExpressions.Regex.IsMatch(_id,@"\A[a-z][a-z0-9-]{0,63}\z"))throw new InvalidDataException("插件标识无效。");
                if(!Directory.Exists(Path.Combine(_plugins,_id)))throw new InvalidDataException("插件不存在。");
                _after[_id]=new(change.Operation=="enable");
            }
            var installed=catalog.Read();
            var candidates=installed.Where(m=>m.Manifest.Id!=_id&&m.Error is null&&(!_after.TryGetValue(m.Manifest.Id,out var state)||state.Enabled)).Select(m=>m.Manifest).ToList();
            if(change.Operation=="enable")
            {
                var module=installed.Single(m=>m.Manifest.Id==_id);
                // Disabled dependency diagnostics may be repaired by enabling this module; its files still must verify.
                candidates.Add(ModulePackage.ReadVerified(module.Directory,File.ReadAllText(Path.Combine(_root,"App","module-publisher.pem")),HostVersion.Current));
            }
            if(Imported is not null)candidates.Add(Imported);
            ModuleValidation.Order(candidates);ModuleValidation.SharedAssemblies(candidates);
            File.WriteAllText(Path.Combine(_transaction,"journal.json"),JsonSerializer.Serialize(new{Id=_id,Operation=change.Operation,Settings=settings,HadSettings=_hadSettings},ModulePackage.Json));
            if(change.Operation is "uninstall" or "import")_previous=Path.Combine(_transaction,"previous");
        }
        catch{Cleanup();throw;}
    }
    public void Apply()
    {
        if(_applied)throw new InvalidOperationException("插件事务已应用。");_applied=true;
        try
        {
            if(_previous is not null&&Directory.Exists(Path.Combine(_plugins,_id)))Directory.Move(Path.Combine(_plugins,_id),_previous);
            if(_incoming is not null)Directory.Move(_incoming,Path.Combine(_plugins,_id));
            new ModuleCatalog(_root,_settings).SaveStates(_after);
            File.WriteAllText(Path.Combine(_transaction,"applied"),"1");
        }
        catch{Rollback();throw;}
    }
    public void Commit(){if(!_applied)throw new InvalidOperationException("事务尚未应用。");File.WriteAllText(Path.Combine(_transaction,"committed"),"1");_committed=true;try{Cleanup();}catch(IOException){}}
    public void Rollback()
    {
        if(!_applied||_committed)return;
        var current=Path.Combine(_plugins,_id);
        if(_incoming is not null&&Directory.Exists(current)&&!Directory.Exists(_incoming))Directory.Move(current,_incoming);
        if(_previous is not null&&Directory.Exists(_previous))Directory.Move(_previous,current);
        if(_hadSettings)File.Copy(Path.Combine(_transaction,"states.before.json"),_settings,true);
        else if(File.Exists(_settings))File.Delete(_settings);
        _applied=false;Cleanup();
    }
    private void Cleanup(){if(Directory.Exists(_transaction))Directory.Delete(_transaction,true);}
    public static void Recover(string installRoot,string settingsPath)
    {
        var root=Path.GetFullPath(installRoot);var transactions=Path.Combine(root,"Plugins",".transactions");if(!Directory.Exists(transactions))return;
        FileStream gate;
        try{gate=new(Path.Combine(root,".module-maintenance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){return;}
        using(gate)foreach(var directory in Directory.EnumerateDirectories(transactions))
        {
            if(!Guid.TryParseExact(Path.GetFileName(directory),"N",out _)||(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)continue;
            var journal=Path.Combine(directory,"journal.json");if(!File.Exists(journal))continue;
            if(File.Exists(Path.Combine(directory,"committed"))){Directory.Delete(directory,true);continue;}
            using var document=JsonDocument.Parse(File.ReadAllText(journal));var data=document.RootElement;
            if(data.TryGetProperty("changes",out _)){ModuleBatchTransaction.RecoverDirectory(root,settingsPath,directory);continue;}
            var id=data.GetProperty("id").GetString()!;var operation=data.GetProperty("operation").GetString();
            if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-z][a-z0-9-]{0,63}\z")||!string.Equals(Path.GetFullPath(data.GetProperty("settings").GetString()!),Path.GetFullPath(settingsPath),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("插件恢复日志路径无效。");
            var previous=Path.Combine(directory,"previous");var current=Path.Combine(root,"Plugins",id);var incoming=Path.Combine(directory,"incoming");
            if(operation=="import"&&!Directory.Exists(incoming)&&Directory.Exists(current))Directory.Move(current,incoming);
            if(Directory.Exists(previous)){if(Directory.Exists(current))throw new IOException("插件回滚目录冲突。");Directory.Move(previous,current);}
            if(data.GetProperty("hadSettings").GetBoolean()){Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);File.Copy(Path.Combine(directory,"states.before.json"),settingsPath,true);}
            else if(File.Exists(settingsPath))File.Delete(settingsPath);
            Directory.Delete(directory,true);
        }
    }
    public void Dispose(){if(_applied&&!_committed)Rollback();else Cleanup();}
}

