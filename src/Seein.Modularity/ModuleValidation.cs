using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Seein.Contracts;

namespace Seein.Modularity;

public static partial class ModuleValidation
{
    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
    public static string RelativeFile(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/')
            || relative.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))) throw new InvalidDataException("插件包含不安全的文件路径。");
        return relative;
    }
    public static string Within(string root, string relative)
    {
        RelativeFile(relative);
        var fullRoot=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var full=Path.GetFullPath(Path.Combine(root,relative));
        if (!full.StartsWith(fullRoot,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件文件超出安装目录。");
        return full;
    }
    public static void Manifest(ModuleManifest manifest, Version host)
    {
        if (manifest is null || manifest.Id is null || manifest.Files is null || manifest.Dependencies is null || manifest.Files.Any(f=>f is null || f.Path is null || f.Sha256 is null) || manifest.Dependencies.Any(d=>d is null || d.Id is null) || !IdPattern().IsMatch(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name)) throw new InvalidDataException("插件标识或名称无效。");
        if (!Version.TryParse(manifest.Version,out _) || !Version.TryParse(manifest.MinimumHostVersion,out var minimum)) throw new InvalidDataException("插件版本无效。");
        if (manifest.ApiVersion!=ModuleApi.Version || minimum>host) throw new InvalidDataException("插件与本机主程序版本不兼容。");
        if (manifest.Files.Count is 0 or > 20000 || manifest.Files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=manifest.Files.Count)
            throw new InvalidDataException("插件文件清单为空、过大或重复。");
        foreach(var file in manifest.Files)
        {
            RelativeFile(file.Path);
            if(file.Path.Equals("module.json",StringComparison.OrdinalIgnoreCase)||file.Path.Equals("module.sig",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("插件不能覆盖签名元数据。");
            if (file.Length<0 || file.Length>2L*1024*1024*1024 || file.Sha256.Length!=64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("插件文件校验信息无效。");
        }
        foreach(var entry in new[]{manifest.Desktop,manifest.Service}.OfType<ModuleEntry>())
            if (!manifest.Files.Any(f=>f.Path==entry.Assembly) || string.IsNullOrWhiteSpace(entry.Type)) throw new InvalidDataException("插件入口不在文件清单中。");
        foreach(var dependency in manifest.Dependencies)
            if (!IdPattern().IsMatch(dependency.Id) || dependency.Id==manifest.Id || !Version.TryParse(dependency.MinimumVersion,out _)) throw new InvalidDataException("插件依赖声明无效。");
    }
    public static void SharedAssemblies(IEnumerable<ModuleManifest> modules)
    {
        var hashes=modules.SelectMany(m=>m.Files.Where(f=>f.Path=="Seein.Desktop.Tools.dll").Select(f=>f.Sha256)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(hashes.Length>1)throw new InvalidDataException("截图与效率插件的共享适配层版本不一致，请使用同一版本的插件包。");
    }
    public static IReadOnlyList<ModuleManifest> Order(IEnumerable<ModuleManifest> modules)
    {
        var map=modules.ToDictionary(m=>m.Id,StringComparer.Ordinal);
        var output=new List<ModuleManifest>();var visited=new HashSet<string>();var active=new HashSet<string>();
        void Visit(ModuleManifest m)
        {
            if(visited.Contains(m.Id))return;
            if(!active.Add(m.Id))throw new InvalidDataException("插件依赖存在循环。");
            foreach(var d in m.Dependencies)
            {
                if(!map.TryGetValue(d.Id,out var target) || Version.Parse(target.Version)<Version.Parse(d.MinimumVersion)) throw new InvalidDataException($"插件 {m.Name} 缺少兼容依赖 {d.Id}。");
                Visit(target);
            }
            active.Remove(m.Id);visited.Add(m.Id);output.Add(m);
        }
        foreach(var module in map.Values.OrderBy(m=>m.Id,StringComparer.Ordinal))Visit(module);
        return output;
    }
    public static void VerifyFiles(string root,ModuleManifest manifest)
    {
        foreach(var f in manifest.Files)
        {
            var path=Within(root,f.Path);var info=new FileInfo(path);
            if(!info.Exists || info.Length!=f.Length || (info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("插件文件不完整："+f.Path);
            var parent=info.Directory;
            while(parent is not null && parent.FullName.StartsWith(Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase))
            {if((parent.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("插件目录包含链接。");parent=parent.Parent;}
            using var stream=info.OpenRead();
            if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(f.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("插件文件校验失败："+f.Path);
        }
    }
}
