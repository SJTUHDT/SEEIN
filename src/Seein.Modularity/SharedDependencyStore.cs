using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
namespace Seein.Modularity;
/// <summary>Identical module DLLs share disk blocks; different hashes always remain separate.</summary>
public static class SharedDependencyStore
{
    public static void Reconcile(string installRoot)
    {
        var plugins=Path.Combine(installRoot,"Plugins");var store=Path.Combine(installRoot,"Shared");if(!Directory.Exists(plugins))return;
        var groups=Directory.EnumerateDirectories(plugins).Where(d=>!Path.GetFileName(d).StartsWith('.')).SelectMany(d=>Directory.EnumerateFiles(d,"*.dll",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}).Select(p=>new{Path=p,Owner=Path.GetFileName(d),Info=new FileInfo(p)})).Where(f=>f.Info.Length>=16384&&(f.Info.Attributes&FileAttributes.ReparsePoint)==0)
            .Select(f=>new{f.Path,f.Owner,Hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Path))).ToLowerInvariant()}).GroupBy(f=>f.Hash).Where(g=>g.Count()>1).ToArray();
        Directory.CreateDirectory(store);var refs=new Dictionary<string,string[]>();
        foreach(var group in groups)
        {
            var directory=Path.Combine(store,group.Key);Directory.CreateDirectory(directory);var pool=Path.Combine(directory,"payload.dll");
            if(!File.Exists(pool))File.Copy(group.First().Path,pool);
            if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pool))).ToLowerInvariant()!=group.Key)throw new InvalidDataException("共享依赖校验失败。");
            foreach(var file in group)
            {
                var temporary=file.Path+".link-"+Guid.NewGuid().ToString("N");
                try{if(CreateHardLink(temporary,pool,0))File.Move(temporary,file.Path,true);}
                finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
            refs[group.Key]=group.Select(f=>f.Owner).Distinct().Order().ToArray();
        }
        foreach(var directory in Directory.EnumerateDirectories(store))
        {
            var id=Path.GetFileName(directory);
            if(id.Length==64&&id.All(Uri.IsHexDigit)&&!refs.ContainsKey(id)&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)==0)Directory.Delete(directory,true);
        }
        File.WriteAllText(Path.Combine(store,"references.json"),JsonSerializer.Serialize(refs,ModulePackage.Json));
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLink(string link,string target,nint reserved);
}
