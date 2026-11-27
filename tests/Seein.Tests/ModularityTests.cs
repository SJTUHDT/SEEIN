using Seein.Contracts;
using Seein.Core;
using Seein.Core.Hosting;
using Seein.Core.Power;
using Seein.Modularity;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace Seein.Tests;
public sealed class ModularityTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"Seein-module-tests-"+Guid.NewGuid().ToString("N"));
    private readonly RSA _key=RSA.Create(3072);
    private string States=>Path.Combine(_root,"data","modules.json");
    public ModularityTests(){Directory.CreateDirectory(Path.Combine(_root,"App"));File.WriteAllText(Path.Combine(_root,"App","module-publisher.pem"),_key.ExportSubjectPublicKeyInfoPem());}
    private string Package(string id,string content="hello",ModuleDependency[]? dependencies=null,int api=1)
    {
        var source=Path.Combine(_root,"source-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"data.txt"),content);
        var bytes=File.ReadAllBytes(Path.Combine(source,"data.txt"));var manifest=new ModuleManifest(id,id,"2.1.0",api,"2.1.0",null,null,dependencies??[],[new("data.txt",Convert.ToHexString(SHA256.HashData(bytes)),bytes.Length)]);
        var package=source+".seeinplugin";ModulePackage.Pack(source,manifest,_key.ExportPkcs8PrivateKeyPem(),package);return package;
    }
    private void Install(string package){using var transaction=new ModuleTransaction(_root,States,new("import",Package:package));transaction.Apply();transaction.Commit();}
    [Fact]public void EmptyBaseNeedsNeitherPluginsNorPublisher(){Directory.Delete(Path.Combine(_root,"App"),true);Assert.Empty(new ModuleCatalog(_root,States).Read());}
    [Theory]
    [InlineData("../escape")][InlineData("a/../../escape")][InlineData("C:/data")][InlineData("/data")][InlineData("a\\b")][InlineData("a//b")][InlineData("CON")][InlineData("a/NUL.dll")][InlineData("a.")][InlineData("a ")][InlineData("a:stream")]
    public void RejectsWindowsPathAliases(string path)=>Assert.Throws<InvalidDataException>(()=>ModuleValidation.RelativeFile(path));
    [Fact]public void TamperFailsAndDoesNotReplaceInstalledVersion()
    {
        Install(Package("display"));var package=Package("display","changed");
        using(var zip=ZipFile.Open(package,ZipArchiveMode.Update)){zip.GetEntry("data.txt")!.Delete();using var writer=new StreamWriter(zip.CreateEntry("data.txt").Open());writer.Write("tampered");}
        Assert.Throws<InvalidDataException>(()=>new ModuleTransaction(_root,States,new("import",Package:package)));
        Assert.Equal("hello",File.ReadAllText(Path.Combine(_root,"Plugins","display","data.txt")));
    }
    [Fact]public void AnotherPublisherIsRejected()
    {
        var package=Package("display");using var other=RSA.Create(3072);File.WriteAllText(Path.Combine(_root,"App","module-publisher.pem"),other.ExportSubjectPublicKeyInfoPem());
        Assert.Throws<InvalidDataException>(()=>new ModuleTransaction(_root,States,new("import",Package:package)));
    }
    [Fact]public void RejectsUnsupportedApiWithoutCreatingInstalledModule(){Assert.Throws<InvalidDataException>(()=>new ModuleTransaction(_root,States,new("import",Package:Package("display",api:2))));Assert.False(Directory.Exists(Path.Combine(_root,"Plugins","display")));}
    [Fact]public void FailedRestartRollsBackCodeAndEnableState()
    {
        Install(Package("display"));var original=File.ReadAllBytes(States);
        using(var transaction=new ModuleTransaction(_root,States,new("import",Package:Package("display","next")))){transaction.Apply();Assert.Equal("next",File.ReadAllText(Path.Combine(_root,"Plugins","display","data.txt")));}
        Assert.Equal("hello",File.ReadAllText(Path.Combine(_root,"Plugins","display","data.txt")));Assert.Equal(original,File.ReadAllBytes(States));
    }
    [Fact]public void DisableAndUninstallKeepUserData()
    {
        Install(Package("display"));var history=Path.Combine(_root,"data","history.db");File.WriteAllText(history,"retained");
        using(var transaction=new ModuleTransaction(_root,States,new("disable","display"))){transaction.Apply();transaction.Commit();}
        Assert.False(new ModuleCatalog(_root,States).Read().Single().Enabled);
        using(var transaction=new ModuleTransaction(_root,States,new("uninstall","display"))){transaction.Apply();transaction.Commit();}
        Assert.Empty(new ModuleCatalog(_root,States).Read());Assert.Equal("retained",File.ReadAllText(history));
    }
    [Fact]public void MissingDependencyDoesNotDisableUnrelatedModule()
    {
        Install(Package("display"));var package=Package("broken",dependencies:[new("missing","1.0.0")]);
        ModulePackage.ExtractVerified(package,Path.Combine(_root,"Plugins","broken"),_key.ExportSubjectPublicKeyInfoPem(),new Version(2,1,0));
        var catalog=new ModuleCatalog(_root,States).Read();Assert.True(catalog.Single(m=>m.Manifest.Id=="display").Enabled);Assert.False(catalog.Single(m=>m.Manifest.Id=="broken").Enabled);
    }
    [Fact]public void RequiredModuleCannotBeDisabledOrRemoved()
    {
        Install(Package("base-feature"));Install(Package("dependent",dependencies:[new("base-feature","2.1.0")]));
        Assert.Throws<InvalidDataException>(()=>new ModuleTransaction(_root,States,new("disable","base-feature")));Assert.Throws<InvalidDataException>(()=>new ModuleTransaction(_root,States,new("uninstall","base-feature")));
    }
    [Fact]public void CorruptModuleAndMissingKeyLeaveCatalogReadable()
    {
        Install(Package("display"));File.Delete(Path.Combine(_root,"App","module-publisher.pem"));var module=Assert.Single(new ModuleCatalog(_root,States).Read());Assert.False(module.Enabled);Assert.NotNull(module.Error);
    }
    [Fact]public void CycleRejected(){var file=new ModuleFile("a",new string('0',64),0);ModuleManifest M(string id,string target)=>new(id,id,"2.1.0",1,"2.1.0",null,null,[new(target,"1.0.0")],[file]);Assert.Throws<InvalidDataException>(()=>ModuleValidation.Order([M("one","two"),M("two","one")]));}
    [Fact]public void IpcWireNamesRemainUnchangedAndUnknownPluginCommandsRoundTrip()
    {
        var json=JsonSerializer.Serialize(PipeEnvelope.Create(PipeMessageType.SensorSnapshot,new{requestId="123"}),JsonDefaults.Options);
        Assert.Contains("\"type\":\"sensorSnapshot\"",json);var unknown=JsonSerializer.Deserialize<PipeEnvelope>("{\"type\":\"customPluginRequest\",\"payload\":{\"requestId\":\"123\"}}",JsonDefaults.Options)!;
        Assert.Equal("customPluginRequest",unknown.Type);Assert.Equal("123",unknown.Payload.GetProperty("requestId").GetString());
    }
    [Fact]public void SettingsPreserveUnknownPluginSectionsAcrossConcurrentWrites()
    {
        var path=Path.Combine(_root,"data","settings.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"{\"activeThemeId\":\"old\",\"CustomPlugin\":{\"secret\":\"keep\"}}");
        var store=new ModuleSettingsStore(path);Parallel.For(0,100,i=>store.Update(s=>s with{PowerBaselineWatts=s.PowerBaselineWatts+1}));
        Assert.Equal(174,store.Current.PowerBaselineWatts);Assert.Equal("keep",store.Current.GetSection<Dictionary<string,string>>("customplugin")!["secret"]);
        var saved=new ModuleSettingsStore(path);Assert.Equal("keep",saved.Current.GetSection<Dictionary<string,string>>("CUSTOMPLUGIN")!["secret"]);
    }
    [Fact]public async Task SlowMinutePersistenceDoesNotBlockSamplingAndFailedWritesRemainPending()
    {
        var now=DateTimeOffset.Now;var buffer=new MinutePowerBuffer(now);buffer.Add(now,100);buffer.Add(now.AddMinutes(1),200);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flush=buffer.FlushAsync(async(_,_)=>{entered.SetResult();await release.Task;},CancellationToken.None);await entered.Task;
        await Task.Run(()=>{for(var i=1;i<=5000;i++)buffer.Add(now.AddMinutes(1).AddMilliseconds(i),200);}).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(5001,buffer.Snapshot(true).Last().Count);release.SetResult();await flush;Assert.Empty(buffer.Snapshot());
        buffer.Close(now.AddMinutes(2));await Assert.ThrowsAsync<IOException>(()=>buffer.FlushAsync((_,_)=>throw new IOException("busy"),CancellationToken.None));Assert.Single(buffer.Snapshot());
        await buffer.FlushAsync((_,_)=>Task.CompletedTask,CancellationToken.None);Assert.Empty(buffer.Snapshot());
    }
    [Fact]public void InterruptedUpdateRecoversOriginalFilesAndState()
    {
        Install(Package("display"));var before=File.ReadAllBytes(States);
        var interrupted=new ModuleTransaction(_root,States,new("import",Package:Package("display","interrupted")));interrupted.Apply();
        ModuleTransaction.Recover(_root,States);Assert.Equal("hello",File.ReadAllText(Path.Combine(_root,"Plugins","display","data.txt")));Assert.Equal(before,File.ReadAllBytes(States));
    }
    [Fact]public void SharedStoreSeparatesDifferentHashesAndCollectsLastReference()
    {
        var one=Path.Combine(_root,"Plugins","one");var two=Path.Combine(_root,"Plugins","two");Directory.CreateDirectory(one);Directory.CreateDirectory(two);
        var bytes=new byte[20000];RandomNumberGenerator.Fill(bytes);File.WriteAllBytes(Path.Combine(one,"same.dll"),bytes);File.WriteAllBytes(Path.Combine(two,"same.dll"),bytes);
        File.WriteAllBytes(Path.Combine(one,"different.dll"),new byte[20000]);File.WriteAllBytes(Path.Combine(two,"different.dll"),Enumerable.Repeat((byte)1,20000).ToArray());
        SharedDependencyStore.Reconcile(_root);var refs=JsonSerializer.Deserialize<Dictionary<string,string[]>>(File.ReadAllText(Path.Combine(_root,"Shared","references.json")))!;Assert.Single(refs);Assert.Equal(2,refs.Single().Value.Length);
        Directory.Delete(two,true);SharedDependencyStore.Reconcile(_root);Assert.Empty(Directory.GetDirectories(Path.Combine(_root,"Shared")));Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(one,"same.dll")));
    }
    public void Dispose(){_key.Dispose();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
