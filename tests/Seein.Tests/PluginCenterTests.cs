using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Seein.Contracts;
using Seein.Modularity;
namespace Seein.Tests;

public sealed class PluginCenterTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"Seein-plugin-center-"+Guid.NewGuid().ToString("N"));
    private readonly RSA _key=RSA.Create(3072);
    private string States=>Path.Combine(_root,"state","modules.json");
    private string Cache=>Path.Combine(_root,"cache");
    public PluginCenterTests(){Directory.CreateDirectory(Path.Combine(_root,"App"));File.WriteAllText(Path.Combine(_root,"App","module-publisher.pem"),_key.ExportSubjectPublicKeyInfoPem());}
    private string Package(string id,string content="old",string[]? dependencies=null)
    {
        var dir=Path.Combine(_root,"sources",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var data=System.Text.Encoding.UTF8.GetBytes(content);File.WriteAllBytes(Path.Combine(dir,"data.txt"),data);
        var manifest=new ModuleManifest(id,id,"2.2.0",1,"2.2.0",null,null,(dependencies??[]).Select(d=>new ModuleDependency(d,"2.2.0")).ToArray(),[new("data.txt",Convert.ToHexString(SHA256.HashData(data)),data.Length)]);
        var file=dir+".seeinplugin";ModulePackage.Pack(dir,manifest,_key.ExportPkcs8PrivateKeyPem(),file);return file;
    }
    private void Install(params ModuleChange[] changes){using var t=new ModuleBatchTransaction(_root,States,new(changes));t.Apply();t.Commit();}
    private PluginListing Listing(string id,string file,string[]? dependencies=null,bool selectable=true)=>new(id,id,"test","test","2.2.0",id+".seeinplugin",new FileInfo(file).Length,100,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),dependencies??[],selectable);
    private PluginFeed Feed(params PluginListing[] packages)=>new(1,"v2.2.0","2.2.0",[new("test","测试")],packages);
    private static InstalledModule Local(string id,bool enabled=true,string version="2.2.0")=>new(new(id,id,version,1,"2.1.0",null,null,[],[new("x",new('a',64),1)]),id,enabled,null);
    private PluginFeed Definitions()
    {
        var root=FindRoot();using var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"config/plugin-definitions.json")));
        return new(1,"v2.2.0","2.2.0",[new("display","副屏"),new("applications","应用"),new("devices","设备"),new("tools","小工具")],data.RootElement.EnumerateArray().Select(p=>new PluginListing(p.GetProperty("id").GetString()!,p.GetProperty("name").GetString()!,p.GetProperty("group").GetString()!,"test","2.2.0",p.GetProperty("id").GetString()+".seeinplugin",1,1,new('a',64),p.GetProperty("dependencies").EnumerateArray().Select(d=>d.GetString()!).ToArray(),p.GetProperty("selectable").GetBoolean())).ToArray());
    }
    private static string FindRoot(){var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir is not null&&!File.Exists(Path.Combine(dir.FullName,"Seein.sln")))dir=dir.Parent;return dir!.FullName;}
    [Fact] public void FourteenFeaturesHaveValidDependencyPlansForEverySelection()
    {
        var feed=Definitions();var leaves=feed.Packages.Where(p=>p.Selectable).ToArray();Assert.Equal(14,leaves.Length);
        for(var mask=0;mask<(1<<leaves.Length);mask++)
        {
            var selection=leaves.Where((_,i)=>(mask&(1<<i))!=0).Select(p=>p.Id).ToArray();var plan=PluginInstallPlan.Create(feed,[],selection);var ids=plan.Required.Select(p=>p.Id).ToHashSet();
            Assert.Equal(selection.Length,plan.Required.Count(p=>p.Selectable));Assert.Equal(ids.Count,plan.Downloads.Count);
            foreach(var p in plan.Required)Assert.All(p.Dependencies,d=>Assert.Contains(d,ids));
        }
    }
    [Fact] public void LegacyMigrationRetainsEnabledAndDisabledFeatureState()
    {
        var feed=Definitions();InstalledModule[] installed=[Local("devices",false,"2.1.0"),Local("companion",true,"2.1.0")];
        var selected=PluginInstallPlan.InitialSelection(feed,installed);Assert.Equal(10,selected.Count);
        var plan=PluginInstallPlan.Create(feed,installed,selected);
        Assert.All(plan.Changes.Where(c=>c.Id!.StartsWith("devices-")),c=>Assert.False(c.Enabled));
        Assert.All(plan.Changes.Where(c=>c.Id!.StartsWith("companion-")&&c.Operation=="import"),c=>Assert.True(c.Enabled));
        Assert.Equal(2,plan.Changes.Count(c=>c.Operation=="uninstall"));
    }
    [Fact] public void LampDoesNotPullOtherHardwareOrWebPackages()
    {
        var plan=PluginInstallPlan.Create(Definitions(),[],["devices-lamp"]);Assert.Equal("devices-lamp",Assert.Single(plan.Required).Id);
    }
    [Fact] public void WebPagesDoNotDownloadPetAndLastReferenceRemovesSupports()
    {
        var feed=Definitions();var plan=PluginInstallPlan.Create(feed,[],["companion-qq"]);Assert.DoesNotContain(plan.Required,p=>p.Id=="companion-pet");
        var installed=plan.Required.Select(p=>Local(p.Id)).Append(Local("custom-extension")).ToArray();var removed=PluginInstallPlan.Create(feed,installed,[]);
        Assert.Equal(plan.Required.Count,removed.Changes.Count);Assert.DoesNotContain(removed.Changes,c=>c.Id=="custom-extension");
    }
    [Fact] public void DisabledLeafCanBeEnabledWithoutDownloadingAgain()
    {
        var plan=PluginInstallPlan.Create(Definitions(),[Local("devices-lamp",false)],["devices-lamp"],new Dictionary<string,bool>{{"devices-lamp",true}});
        Assert.Empty(plan.Downloads);Assert.Equal("enable",Assert.Single(plan.Changes).Operation);
    }
    [Fact] public void BatchCanInstallDependentBeforeItsDependency()
    {
        Install(new ModuleChange("import","leaf",Package("leaf",dependencies:["common"])),new("import","common",Package("common")));
        Assert.All(new ModuleCatalog(_root,States).Read(),m=>{Assert.Null(m.Error);Assert.True(m.Enabled);});
    }
    [Fact] public void DisposingAnAppliedBatchRestoresEveryDirectoryAndState()
    {
        Install(new ModuleChange("import","one",Package("one")),new("import","two",Package("two")));var before=File.ReadAllBytes(States);
        using(var t=new ModuleBatchTransaction(_root,States,new([new("import","one",Package("one","new")),new("uninstall","two")]))){t.Apply();Assert.False(Directory.Exists(Path.Combine(_root,"Plugins","two")));}
        Assert.Equal("old",File.ReadAllText(Path.Combine(_root,"Plugins","one","data.txt")));Assert.True(Directory.Exists(Path.Combine(_root,"Plugins","two")));Assert.Equal(before,File.ReadAllBytes(States));
    }
    [Fact] public void CrashRecoveryRestoresWholeBatchAndRemovesNewFeature()
    {
        Install(new ModuleChange("import","one",Package("one")));var before=File.ReadAllBytes(States);
        var interrupted=new ModuleBatchTransaction(_root,States,new([new("import","one",Package("one","new")),new("import","extra",Package("extra"))]));interrupted.Apply();
        ModuleTransaction.Recover(_root,States);Assert.Equal("old",File.ReadAllText(Path.Combine(_root,"Plugins","one","data.txt")));Assert.False(Directory.Exists(Path.Combine(_root,"Plugins","extra")));Assert.Equal(before,File.ReadAllBytes(States));
    }
    [Fact] public void WrongPlannedIdCannotInstallSignedDifferentPackage()
    {
        Assert.Throws<InvalidDataException>(()=>new ModuleBatchTransaction(_root,States,new([new("import","expected",Package("different"))])));Assert.Empty(new ModuleCatalog(_root,States).Read());
    }
    [Fact] public void RemovingRequiredSupportIsRejectedBeforeChangingFiles()
    {
        Install(new ModuleChange("import","leaf",Package("leaf",dependencies:["common"])),new("import","common",Package("common")));
        Assert.Throws<InvalidDataException>(()=>new ModuleBatchTransaction(_root,States,new([new("uninstall","common")])));
        Assert.All(new ModuleCatalog(_root,States).Read(),m=>Assert.Null(m.Error));
    }
    [Fact] public void OfflineImportCannotRunLegacyAndChildImplementationsTogether()
    {
        Install(new ModuleChange("import","devices",Package("devices")));
        Assert.Throws<InvalidDataException>(()=>new ModuleBatchTransaction(_root,States,new([new("import","devices-lamp",Package("devices-lamp"))])));
        Assert.Equal("devices",Assert.Single(new ModuleCatalog(_root,States).Read()).Manifest.Id);
    }
    [Fact] public void PackageWithEmptyRuntimeFoldersRemainsImportable()
    {
        var source=Path.Combine(_root,"empty-folders");Directory.CreateDirectory(Path.Combine(source,"runtimes","win-x64"));File.WriteAllText(Path.Combine(source,"x.txt"),"x");
        var manifest=new ModuleManifest("empty-folders","test","2.2.0",1,"2.2.0",null,null,[],[new("x.txt",Convert.ToHexString(SHA256.HashData([120])),1)]);
        var file=Path.Combine(_root,"empty.seeinplugin");ModulePackage.Pack(source,manifest,_key.ExportPkcs8PrivateKeyPem(),file);
        ModulePackage.ExtractVerified(file,Path.Combine(_root,"extracted"),_key.ExportSubjectPublicKeyInfoPem(),HostVersion.Current);
    }
    [Fact] public void InvalidLastPackageLeavesInstalledFilesUntouched()
    {
        Install(new ModuleChange("import","one",Package("one")));var bad=Path.Combine(_root,"bad.seeinplugin");File.WriteAllText(bad,"not zip");
        Assert.ThrowsAny<Exception>(()=>new ModuleBatchTransaction(_root,States,new([new("import","one",Package("one","new")),new("import","bad",bad)])));
        Assert.Equal("old",File.ReadAllText(Path.Combine(_root,"Plugins","one","data.txt")));
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> run):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>run(request,token);}
    private static HttpResponseMessage Response(byte[] data)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(data)};
    [Fact] public async Task SignedOnlineFeedDownloadsAndAtomicallyInstallsWithoutNetworkOrHardware()
    {
        var package=Package("leaf");var feed=Feed(Listing("leaf",package));var bytes=JsonSerializer.SerializeToUtf8Bytes(feed,ModulePackage.Json);var signature=_key.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss);var calls=0;
        using var http=new HttpClient(new Handler((request,_)=>{calls++;var name=request.RequestUri!.Segments.Last();return Task.FromResult(Response(name=="plugin-catalog.json"?bytes:name=="plugin-catalog.sig"?signature:File.ReadAllBytes(package)));}));
        using var client=new PluginFeedClient(Cache,_key.ExportSubjectPublicKeyInfoPem(),http);var online=await client.RefreshAsync(CancellationToken.None);var item=Assert.Single(online.Packages);
        var download=await client.DownloadAsync(online,item,null,CancellationToken.None);Install(new ModuleChange("import",item.Id,download));Assert.Equal("old",File.ReadAllText(Path.Combine(_root,"Plugins","leaf","data.txt")));
        Assert.Equal(download,await client.DownloadAsync(online,item,null,CancellationToken.None));Assert.Equal(3,calls);Assert.NotNull(client.Cached());
    }
    [Fact] public async Task TamperedCatalogIsRejectedAndNeverCached()
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(Feed(Listing("leaf",Package("leaf"))),ModulePackage.Json);var signature=_key.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss);bytes[0]=0;
        using var http=new HttpClient(new Handler((request,_)=>Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith(".json")?bytes:signature))));using var client=new PluginFeedClient(Cache,_key.ExportSubjectPublicKeyInfoPem(),http);
        await Assert.ThrowsAsync<InvalidDataException>(()=>client.RefreshAsync(CancellationToken.None));Assert.Null(client.Cached());
    }
    [Fact] public async Task CorruptDownloadFailsWithoutChangingInstallation()
    {
        var package=Package("leaf");var feed=Feed(Listing("leaf",package));var bad=File.ReadAllBytes(package);bad[^1]^=1;
        using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(Response(bad))));using var client=new PluginFeedClient(Cache,_key.ExportSubjectPublicKeyInfoPem(),http);
        await Assert.ThrowsAsync<InvalidDataException>(()=>client.DownloadAsync(feed,feed.Packages[0],null,CancellationToken.None));Assert.Empty(new ModuleCatalog(_root,States).Read());Assert.Empty(Directory.GetFiles(Cache));
    }
    [Fact] public async Task CancelledDownloadDoesNotTouchInstallation()
    {
        var feed=Feed(Listing("leaf",Package("leaf")));using var http=new HttpClient(new Handler(async(_,token)=>{await Task.Delay(Timeout.Infinite,token);return Response([]);}));using var client=new PluginFeedClient(Cache,_key.ExportSubjectPublicKeyInfoPem(),http);using var stop=new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.DownloadAsync(feed,feed.Packages[0],null,stop.Token));Assert.Empty(new ModuleCatalog(_root,States).Read());Assert.Empty(Directory.GetFiles(Cache));
    }
    [Fact] public void DependencyCyclesAndFutureHostAreRejected()
    {
        var p=Package("leaf");Assert.Throws<InvalidDataException>(()=>Feed(Listing("one",p,["two"]),Listing("two",p,["one"])).Validate());
        Assert.Throws<InvalidDataException>(()=>(Feed(Listing("leaf",p)) with{MinimumHostVersion="99.0.0"}).Validate());
    }
    public void Dispose(){_key.Dispose();Directory.Delete(_root,true);}
}
