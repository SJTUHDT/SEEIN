using System.Security.Cryptography;
using System.Text.Json;
using Seein.Contracts;
using Seein.Modularity;
if(args.Length==0)return 2;
if(args[0]=="key")
{
    var path=Path.GetFullPath(args[1]);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var rsa=RSA.Create(3072);
    if(File.Exists(path)){var protectedKey=File.ReadAllBytes(path);var bytes=ProtectedData.Unprotect(protectedKey,null,DataProtectionScope.CurrentUser);try{rsa.ImportPkcs8PrivateKey(bytes,out _);}finally{CryptographicOperations.ZeroMemory(bytes);}}
    else{var bytes=rsa.ExportPkcs8PrivateKey();try{File.WriteAllBytes(path,ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser));}finally{CryptographicOperations.ZeroMemory(bytes);}}
    File.WriteAllText(args[2],rsa.ExportSubjectPublicKeyInfoPem());return 0;
}
if(args[0]=="pack")
{
    var directory=Path.GetFullPath(args[1]);var metadata=JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(args[2]),ModulePackage.Json)!;
    var files=Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).OrderBy(p=>p,StringComparer.Ordinal).Select(p=>new ModuleFile(Path.GetRelativePath(directory,p).Replace('\\','/'),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))),new FileInfo(p).Length)).ToArray();
    var manifest=metadata with{Files=files};ModuleValidation.Manifest(manifest,new Version(2,1,0));
    var bytes=ProtectedData.Unprotect(File.ReadAllBytes(args[4]),null,DataProtectionScope.CurrentUser);
    using var rsa=RSA.Create();try{rsa.ImportPkcs8PrivateKey(bytes,out _);}finally{CryptographicOperations.ZeroMemory(bytes);}
    ModulePackage.Pack(directory,manifest,rsa.ExportPkcs8PrivateKeyPem(),args[3]);
    ModulePackage.ReadVerified(directory,rsa.ExportSubjectPublicKeyInfoPem(),new Version(2,1,0));return 0;
}
if(args[0]=="verify")
{
    var catalog=new ModuleCatalog(args[1],Path.Combine(args[1],"nonexistent-states.json"));var modules=catalog.Read();
    foreach(var module in modules){Console.WriteLine(module.Manifest.Id+": "+(module.Error??"verified"));if(module.Error is not null)return 1;}return 0;
}
return 2;
