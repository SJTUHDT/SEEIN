using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Seein.Contracts;
namespace Seein.Modularity;

public static class ModulePackage
{
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    public static ModuleManifest ReadVerified(string directory,string publicKey,Version host)
    {
        var bytes=File.ReadAllBytes(Path.Combine(directory,"module.json"));
        if(bytes.Length>4*1024*1024)throw new InvalidDataException("插件清单过大。");
        using var rsa=RSA.Create();rsa.ImportFromPem(publicKey);
        if(!rsa.VerifyData(bytes,File.ReadAllBytes(Path.Combine(directory,"module.sig")),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new InvalidDataException("插件签名无效或来源未受信任。");
        var manifest=JsonSerializer.Deserialize<ModuleManifest>(bytes,Json)??throw new InvalidDataException("插件清单无效。");
        ModuleValidation.Manifest(manifest,host);ModuleValidation.VerifyFiles(directory,manifest);return manifest;
    }
    public static ModuleManifest ExtractVerified(string package,string destination,string publicKey,Version host)
    {
        if(Directory.Exists(destination))throw new IOException("插件暂存目录已存在。");
        using var zip=ZipFile.OpenRead(package);
        if(zip.Entries.Count>20002)throw new InvalidDataException("插件包文件过多。");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var e in zip.Entries)
        {
            var name=ModuleValidation.RelativeFile(e.FullName);
            if(!names.Add(name) || (e.ExternalAttributes&0xF0000000)==unchecked((int)0xA0000000))throw new InvalidDataException("插件包包含重复文件或链接。");
            total=checked(total+e.Length);
            if(e.Length>2L*1024*1024*1024 || total>4L*1024*1024*1024)throw new InvalidDataException("插件包解压大小超出限制。");
        }
        Directory.CreateDirectory(destination);
        try
        {
            foreach(var e in zip.Entries)
            {
                var path=ModuleValidation.Within(destination,e.FullName);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input=e.Open();using var output=new FileStream(path,FileMode.CreateNew);input.CopyTo(output);
            }
            var manifest=ReadVerified(destination,publicKey,host);
            var expected=manifest.Files.Select(f=>f.Path).Append("module.json").Append("module.sig").ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(!expected.SetEquals(names))throw new InvalidDataException("插件包与签名文件清单不一致。");
            return manifest;
        }
        catch {if(Directory.Exists(destination))Directory.Delete(destination,true);throw;}
    }
    public static void Pack(string directory,ModuleManifest manifest,string privateKey,string output)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest,Json);
        File.WriteAllBytes(Path.Combine(directory,"module.json"),bytes);
        using var rsa=RSA.Create();rsa.ImportFromPem(privateKey);
        File.WriteAllBytes(Path.Combine(directory,"module.sig"),rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
        ZipFile.CreateFromDirectory(directory,output,CompressionLevel.Optimal,false);
    }
}
