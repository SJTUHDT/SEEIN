using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Seein.Contracts;

namespace Seein.Modularity;

public sealed record PluginGroup(string Id,string Name);
public sealed record PluginListing(string Id,string Name,string Group,string Description,string Version,
    string Asset,long DownloadBytes,long InstalledBytes,string Sha256,IReadOnlyList<string> Dependencies,bool Selectable=true);
public sealed record PluginFeed(int Schema,string Release,string MinimumHostVersion,
    IReadOnlyList<PluginGroup> Groups,IReadOnlyList<PluginListing> Packages)
{
    public const string Repository = "https://github.com/SJTUHDT/SEEIN";
    public static readonly IReadOnlyDictionary<string,string[]> Legacy = new Dictionary<string,string[]>
    {
        ["devices"]=["devices-monitor","devices-playback","devices-rgb","devices-fan","devices-router","devices-lamp"],
        ["companion"]=["companion-pet","companion-workbench","companion-astrbot","companion-qq"]
    };
    public void Validate()
    {
        if(Schema!=1 || !System.Text.RegularExpressions.Regex.IsMatch(Release,@"\Av\d+\.\d+\.\d+\z") ||
           !Version.TryParse(MinimumHostVersion,out var minimum)||minimum>HostVersion.Current)
            throw new InvalidDataException("插件目录需要更新版本的 SEEIN。");
        if(Groups.Count is 0 or >32 || Groups.Select(g=>g.Id).Distinct().Count()!=Groups.Count || Packages.Count is 0 or >256 || Packages.Select(p=>p.Id).Distinct().Count()!=Packages.Count)
            throw new InvalidDataException("插件目录无效。");
        var map=Packages.ToDictionary(p=>p.Id);
        foreach(var p in Packages)
        {
            ModuleValidation.RelativeFile(p.Id);ModuleValidation.RelativeFile(p.Asset);
            if(p.Id.Contains('/')||p.Asset.Contains('/')||!p.Asset.EndsWith(".seeinplugin",StringComparison.Ordinal)||
               !Version.TryParse(p.Version,out _)||p.DownloadBytes<=0||p.DownloadBytes>2L*1024*1024*1024||p.InstalledBytes<0||p.Sha256.Length!=64||!p.Sha256.All(Uri.IsHexDigit)||
               (p.Selectable&&!Groups.Any(g=>g.Id==p.Group))||p.Dependencies.Any(d=>!map.ContainsKey(d)||d==p.Id))
                throw new InvalidDataException("插件目录包含无效条目。");
        }
        _=Closure(map.Keys);
    }
    public IReadOnlyList<PluginListing> Closure(IEnumerable<string> ids)
    {
        var map=Packages.ToDictionary(p=>p.Id);var active=new HashSet<string>();var done=new HashSet<string>();var output=new List<PluginListing>();
        void Visit(string id){if(done.Contains(id))return;if(!map.TryGetValue(id,out var p)||!active.Add(id))throw new InvalidDataException("插件依赖缺失或存在循环。");foreach(var d in p.Dependencies)Visit(d);active.Remove(id);done.Add(id);output.Add(p);}
        foreach(var id in ids)Visit(id);return output;
    }
}

public sealed record PluginProgress(string Id,long Received,long Total);
public sealed class PluginFeedClient(string cache,string publicKey,HttpClient? transport=null):IDisposable
{
    private readonly HttpClient _http=transport??new(){Timeout=TimeSpan.FromMinutes(30)};
    private readonly bool _owns=transport is null;
    private static void Verify(byte[] bytes,byte[] signature,string key)
    {
        using var rsa=RSA.Create();rsa.ImportFromPem(key);
        if(!rsa.VerifyData(bytes,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new InvalidDataException("插件目录签名无效。");
    }
    public PluginFeed Read(string json,string signature)
    {
        var bytes=File.ReadAllBytes(json);if(bytes.Length>4*1024*1024)throw new InvalidDataException("插件目录过大。");Verify(bytes,File.ReadAllBytes(signature),publicKey);
        var feed=JsonSerializer.Deserialize<PluginFeed>(bytes,ModulePackage.Json)??throw new InvalidDataException("插件目录无效。");feed.Validate();return feed;
    }
    public PluginFeed? Cached(string? app=null)
    {
        foreach(var path in new[]{cache,app}.OfType<string>())try{return Read(Path.Combine(path,"plugin-catalog.json"),Path.Combine(path,"plugin-catalog.sig"));}catch(Exception e)when(e is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or JsonException or ArgumentException){}
        return null;
    }
    public async Task<PluginFeed> RefreshAsync(CancellationToken token)
    {
        Directory.CreateDirectory(cache);
        var url=PluginFeed.Repository+"/releases/latest/download/";
        async Task<byte[]> Get(string name,int maximum)
        {
            using var response=await _http.GetAsync(url+name,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);response.EnsureSuccessStatusCode();
            await using var input=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);using var output=new MemoryStream();var buffer=new byte[16384];int count;
            while((count=await input.ReadAsync(buffer,token).ConfigureAwait(false))>0){if(output.Length+count>maximum)throw new InvalidDataException("插件目录过大。");output.Write(buffer,0,count);}return output.ToArray();
        }
        var json=await Get("plugin-catalog.json",4*1024*1024).ConfigureAwait(false);var signature=await Get("plugin-catalog.sig",16384).ConfigureAwait(false);Verify(json,signature,publicKey);
        var feed=JsonSerializer.Deserialize<PluginFeed>(json,ModulePackage.Json)??throw new InvalidDataException("插件目录无效。");feed.Validate();
        // A mismatched cache pair never validates; the bundled signed catalog remains a fallback.
        File.WriteAllBytes(Path.Combine(cache,"plugin-catalog.json"),json);File.WriteAllBytes(Path.Combine(cache,"plugin-catalog.sig"),signature);return feed;
    }
    public async Task<string> DownloadAsync(PluginFeed feed,PluginListing package,IProgress<PluginProgress>? progress,CancellationToken token)
    {
        Directory.CreateDirectory(cache);var file=Path.Combine(cache,package.Sha256.ToLowerInvariant()+".seeinplugin");
        bool Matches(string path){if(!File.Exists(path)||new FileInfo(path).Length!=package.DownloadBytes)return false;using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).Equals(package.Sha256,StringComparison.OrdinalIgnoreCase);}
        if(await Task.Run(()=>Matches(file),token).ConfigureAwait(false)){progress?.Report(new(package.Id,package.DownloadBytes,package.DownloadBytes));return file;}
        var temporary=file+"."+Guid.NewGuid().ToString("N")+".part";
        try
        {
            for(var attempt=0;;attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var response=await _http.GetAsync(PluginFeed.Repository+"/releases/download/"+feed.Release+"/"+package.Asset,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);response.EnsureSuccessStatusCode();
                    if(response.Content.Headers.ContentLength is long length&&length!=package.DownloadBytes)throw new InvalidDataException("下载大小与签名目录不一致。");
                    await using(var input=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    await using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,81920,true))
                    {
                        var buffer=new byte[81920];long total=0;int count;var reportAt=Environment.TickCount64;
                        while((count=await input.ReadAsync(buffer,token).ConfigureAwait(false))>0){total+=count;if(total>package.DownloadBytes)throw new InvalidDataException("下载超过声明大小。");await output.WriteAsync(buffer.AsMemory(0,count),token).ConfigureAwait(false);if(Environment.TickCount64-reportAt>=100){progress?.Report(new(package.Id,total,package.DownloadBytes));reportAt=Environment.TickCount64;}}
                    }
                    if(!Matches(temporary))throw new InvalidDataException("插件下载校验失败。");
                    File.Move(temporary,file,true);progress?.Report(new(package.Id,package.DownloadBytes,package.DownloadBytes));return file;
                }
                catch(Exception e)when(attempt<2&&(e is HttpRequestException or IOException)&&e is not InvalidDataException){await Task.Delay(TimeSpan.FromSeconds(attempt+1),token).ConfigureAwait(false);}
            }
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public void Dispose(){if(_owns)_http.Dispose();}
}
