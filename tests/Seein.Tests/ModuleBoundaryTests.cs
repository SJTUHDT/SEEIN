using Seein.Core;
using Seein.Core.Hosting;
using Seein.Modules;
using Seein.Service;
namespace Seein.Tests;
public sealed class ModuleBoundaryTests
{
    [Fact]public void MandatoryServiceHasNoOptionalAssemblyReferences()
    {
        Assert.DoesNotContain(typeof(PanelWorker).Assembly.GetReferencedAssemblies(),name=>name.Name!.StartsWith("Seein.Modules.")||name.Name.StartsWith("Seein.Desktop."));
        Assert.DoesNotContain(typeof(AppPaths).Assembly.GetReferencedAssemblies(),name=>name.Name!.StartsWith("Seein.Modules.")||name.Name.StartsWith("SkiaSharp")||name.Name.StartsWith("OpenCvSharp"));
        Assert.Null(typeof(AppPaths).Assembly.GetType("Seein.Core.Lighting.OpenRgbProvider"));
    }
    [Theory]
    [InlineData("appCatalogRequest")][InlineData("appMetaRequest")][InlineData("afkSample")][InlineData("appStatsRequest")][InlineData("appLoadRequest")][InlineData("appSummaryRequest")]
    public void ApplicationMonitoringModuleKeepsMonitoringRoutes(string type)=>Assert.True(new ApplicationsBackend().CanHandle(type));
    [Theory]
    [InlineData("fanStateRequest")][InlineData("fanConfigRequest")][InlineData("fanRestoreRequest")][InlineData("rgbDevicesRequest")][InlineData("rgbCommandRequest")]
    public void DevicesModuleKeepsEveryOriginalRoute(string type)=>Assert.True(new DevicesBackend().CanHandle(type));
}
