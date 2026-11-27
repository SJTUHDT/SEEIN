using System.Text.Json;
using Seein.Contracts;

namespace Seein.Modularity;

public sealed record ModuleChangeSet(IReadOnlyList<ModuleChange> Changes);
public sealed class ModuleBatchTransaction:IDisposable
{
    public sealed record Item(ModuleChange Change,bool HadDirectory);
    public sealed record Journal(string Settings,bool HadSettings,IReadOnlyList<Item> Changes);
    private readonly string _root,_settings,_work;
    private readonly Journal _journal;
    private readonly Dictionary<string,ModuleState> _after;
    private bool _applied,_committed;
    public IReadOnlyList<ModuleManifest> Imported {get;}
    public IReadOnlyList<ModuleChange> Changes=>_journal.Changes.Select(i=>i.Change).ToArray();
    public ModuleBatchTransaction(string root,string settings,ModuleChangeSet changes)
    {
        _root=Path.GetFullPath(root);_settings=Path.GetFullPath(settings);
        if(changes.Changes.Count is 0 or >256)throw new InvalidDataException("批量插件操作数量无效。");
        _work=Path.Combine(_root,"Plugins",".transactions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(_work);
        var catalog=new ModuleCatalog(_root,_settings);_after=catalog.LoadStates();
        var imported=new List<ModuleManifest>();var items=new List<Item>();var seen=new HashSet<string>();
        try
        {
            var key=File.ReadAllText(Path.Combine(_root,"App","module-publisher.pem"));
            foreach(var requested in changes.Changes)
            {
                var change=requested;
                if(change.Operation=="import")
                {
                    var stage=Path.Combine(_work,"staged-"+items.Count);
                    var manifest=ModulePackage.ExtractVerified(change.Package??throw new InvalidDataException("缺少插件包。"),stage,key,HostVersion.Current);
                    if(change.Id is not null&&change.Id!=manifest.Id)throw new InvalidDataException("下载插件与安装计划不一致。");
                    change=change with{Id=manifest.Id};imported.Add(manifest);Directory.Move(stage,Path.Combine(_work,"incoming-"+manifest.Id));
                }
                else if(change.Operation is not ("enable" or "disable" or "uninstall"))throw new InvalidDataException("不支持的插件操作。");
                var id=change.Id??"";
                if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-z][a-z0-9-]{0,63}\z")||!seen.Add(id))throw new InvalidDataException("插件标识无效或重复。");
                var current=Path.Combine(_root,"Plugins",id);var exists=Directory.Exists(current);
                if(exists&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("插件目录不允许链接。");
                if(!exists&&change.Operation!="import")throw new InvalidDataException("插件不存在："+id);
                items.Add(new(change,exists));
                _after[id]=new(change.Operation is "import" or "enable" ? change.Enabled??true : false);
            }
            var installed=catalog.Read();
            var candidates=installed.Where(m=>!seen.Contains(m.Manifest.Id)&&m.Enabled&&m.Error is null).Select(m=>m.Manifest).ToList();
            foreach(var item in items.Where(i=>_after[i.Change.Id!].Enabled))
                candidates.Add(imported.FirstOrDefault(m=>m.Id==item.Change.Id)??ModulePackage.ReadVerified(Path.Combine(_root,"Plugins",item.Change.Id!),key,HostVersion.Current));
            ModuleValidation.Order(candidates);ModuleValidation.SharedAssemblies(candidates);
            _journal=new(_settings,File.Exists(_settings),items);Imported=imported;
            if(_journal.HadSettings)File.Copy(_settings,Path.Combine(_work,"states.before.json"));
            File.WriteAllText(Path.Combine(_work,"journal.json"),JsonSerializer.Serialize(_journal,ModulePackage.Json));
        }
        catch{Directory.Delete(_work,true);throw;}
    }
    public void Apply()
    {
        if(_applied)throw new InvalidOperationException("插件事务已执行。");_applied=true;
        try
        {
            foreach(var item in _journal.Changes)
            {
                var id=item.Change.Id!;var current=Path.Combine(_root,"Plugins",id);
                if(item.Change.Operation is "import" or "uninstall")
                {
                    if(item.HadDirectory)Directory.Move(current,Path.Combine(_work,"previous-"+id));
                    if(item.Change.Operation=="import")Directory.Move(Path.Combine(_work,"incoming-"+id),current);
                }
            }
            new ModuleCatalog(_root,_settings).SaveStates(_after);File.WriteAllText(Path.Combine(_work,"applied"),"1");
        }
        catch{Rollback();throw;}
    }
    public void Commit(){if(!_applied)throw new InvalidOperationException("事务尚未执行。");File.WriteAllText(Path.Combine(_work,"committed"),"1");_committed=true;Cleanup();}
    public void Rollback(){if(!_applied||_committed)return;RecoverDirectory(_root,_settings,_work);_applied=false;}
    public static void RecoverDirectory(string root,string settings,string directory)
    {
        if(File.Exists(Path.Combine(directory,"committed"))){Directory.Delete(directory,true);return;}
        var journal=JsonSerializer.Deserialize<Journal>(File.ReadAllText(Path.Combine(directory,"journal.json")),ModulePackage.Json)??throw new InvalidDataException("批量恢复日志无效。");
        if(!string.Equals(Path.GetFullPath(journal.Settings),Path.GetFullPath(settings),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("恢复路径无效。");
        foreach(var item in journal.Changes.Reverse())
        {
            var id=item.Change.Id??"";if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-z][a-z0-9-]{0,63}\z"))throw new InvalidDataException("恢复插件标识无效。");
            var current=Path.Combine(root,"Plugins",id);var incoming=Path.Combine(directory,"incoming-"+id);var previous=Path.Combine(directory,"previous-"+id);
            if(item.Change.Operation=="import"&&!Directory.Exists(incoming)&&Directory.Exists(current))Directory.Move(current,incoming);
            if(Directory.Exists(previous)){if(Directory.Exists(current))throw new IOException("恢复目录冲突。");Directory.Move(previous,current);}
        }
        if(journal.HadSettings){Directory.CreateDirectory(Path.GetDirectoryName(settings)!);File.Copy(Path.Combine(directory,"states.before.json"),settings,true);}else if(File.Exists(settings))File.Delete(settings);
        Directory.Delete(directory,true);
    }
    private void Cleanup(){try{if(Directory.Exists(_work))Directory.Delete(_work,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
    public void Dispose(){if(_applied&&!_committed)Rollback();else Cleanup();}
}
