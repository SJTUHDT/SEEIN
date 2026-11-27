using Seein.Core.Storage;
namespace Seein.Tests;
public sealed class RepositoryHandleTests
{
    [Fact]public async Task ClosedApplicationRepositoryReleasesItsDatabaseWithoutGarbageCollection()
    {
        var root=Path.Combine(Path.GetTempPath(),"Seein-handles-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var file=Path.Combine(root,"apps.db");
        try
        {
            await using(var repository=new AppUsageRepository(file))
            {
                await repository.InitializeAsync();for(var i=0;i<30;i++){await repository.UpsertMetaAsync("app"+i,null,"App"+i);await repository.GetMetaAsync();}
            }
            using(var exclusive=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Assert.True(exclusive.Length>0);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]public async Task ClosedEnergyRepositoryReleasesItsDatabaseWithoutGarbageCollection()
    {
        var root=Path.Combine(Path.GetTempPath(),"Seein-handles-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var file=Path.Combine(root,"energy.db");
        try
        {
            await using(var repository=new EnergyRepository(file)){await repository.InitializeAsync();for(var i=0;i<30;i++)await repository.GetTotalsAsync(DateTimeOffset.Now);}
            using(var exclusive=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Assert.True(exclusive.Length>0);
        }
        finally{Directory.Delete(root,true);}
    }
}
