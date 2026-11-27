namespace Seein.Modularity;
public sealed record PluginInstallPlan(IReadOnlyList<PluginListing> Downloads,IReadOnlyList<ModuleChange> Changes,IReadOnlyList<PluginListing> Required)
{
    public static HashSet<string> InitialSelection(PluginFeed feed,IReadOnlyList<InstalledModule> installed)
    {
        var ids=installed.Select(m=>m.Manifest.Id).ToHashSet();foreach(var old in PluginFeed.Legacy)if(ids.Contains(old.Key))ids.UnionWith(old.Value);
        ids.IntersectWith(feed.Packages.Where(p=>p.Selectable).Select(p=>p.Id));return ids;
    }
    public static PluginInstallPlan Create(PluginFeed feed,IReadOnlyList<InstalledModule> installed,IEnumerable<string> selection,IReadOnlyDictionary<string,bool>? toggles=null)
    {
        feed.Validate();var map=installed.ToDictionary(m=>m.Manifest.Id);var selected=selection.ToHashSet();
        if(selected.Any(id=>!feed.Packages.Any(p=>p.Id==id&&p.Selectable)))throw new InvalidDataException("选择包含无效功能。");
        bool Enabled(string id){if(toggles is not null&&toggles.TryGetValue(id,out var value))return value;if(map.TryGetValue(id,out var existing))return existing.Enabled;foreach(var old in PluginFeed.Legacy)if(old.Value.Contains(id)&&map.TryGetValue(old.Key,out existing))return existing.Enabled;return true;}
        var required=feed.Closure(selected);var desired=required.ToDictionary(p=>p.Id,_=>false);
        foreach(var leaf in selected)if(Enabled(leaf))foreach(var p in feed.Closure([leaf]))desired[p.Id]=true;
        var downloads=new List<PluginListing>();var changes=new List<ModuleChange>();
        foreach(var p in required)
            if(!map.TryGetValue(p.Id,out var existing)||existing.Manifest.Version!=p.Version||existing.Error is not null){downloads.Add(p);changes.Add(new("import",p.Id,Enabled:desired[p.Id]));}
            else if(existing.Enabled!=desired[p.Id])changes.Add(new(desired[p.Id]?"enable":"disable",p.Id));
        foreach(var existing in installed)if(!desired.ContainsKey(existing.Manifest.Id)&&(feed.Packages.Any(p=>p.Id==existing.Manifest.Id)||PluginFeed.Legacy.ContainsKey(existing.Manifest.Id)))changes.Add(new("uninstall",existing.Manifest.Id));
        return new(downloads,changes,required);
    }
}
