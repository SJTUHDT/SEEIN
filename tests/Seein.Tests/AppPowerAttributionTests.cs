using Seein.Core.Apps;

namespace Seein.Tests;

public sealed class AppPowerAttributionTests
{
    [Fact]
    public void CpuShareIsProportionalToProcessCpuTime()
    {
        Assert.Equal(40d * 0.25d / 3600d, AppPowerAttributor.CpuWh(40, 2.5, 10, 1), 9);
        Assert.Equal(40d * 0.5d / 3600d, AppPowerAttributor.CpuWh(40, 5, 10, 1), 9);
        // Five seconds of a half share: 40 W * 0.5 * 5 / 3600.
        Assert.Equal(40d * 0.5d * 5 / 3600d, AppPowerAttributor.CpuWh(40, 5, 10, 5), 9);
    }

    [Fact]
    public void GpuShareIsProportionalToEngineUtilization()
    {
        Assert.Equal(120d * 0.25d / 3600d, AppPowerAttributor.GpuWh(120, 25, 100, 1), 9);
        Assert.Equal(120d * 0.25d * 60 / 3600d, AppPowerAttributor.GpuWh(120, 25, 100, 60), 9);
    }

    [Fact]
    public void AttributionIsClampedToOneAndGuardsAgainstBadInput()
    {
        Assert.Equal(0, AppPowerAttributor.CpuWh(40, 0, 10, 5));
        Assert.Equal(0, AppPowerAttributor.CpuWh(0, 5, 10, 5));
        Assert.Equal(0, AppPowerAttributor.CpuWh(40, 5, 0, 5));
        Assert.Equal(0, AppPowerAttributor.CpuWh(40, 5, 10, 0));
        Assert.Equal(0, AppPowerAttributor.GpuWh(double.NaN, 5, 10, 5));
        Assert.True(AppPowerAttributor.CpuWh(40, 20, 10, 5) <= 40d * 5 / 3600d + 1e-12);
    }

    [Fact]
    public void BaselineCoversWhateverTheCpuAndGpuRailsDoNotExplain()
    {
        // 200 W estimated system power, 40 W CPU and 100 W GPU over one hour.
        Assert.Equal(60, AppPowerAttributor.BaselineWh(200, 40, 100, 3600), 6);
        // Never negative, even if the sensors over-report.
        Assert.Equal(0, AppPowerAttributor.BaselineWh(100, 80, 60, 3600), 6);
        Assert.Equal(200, AppPowerAttributor.BaselineWh(200, double.NaN, double.PositiveInfinity, 3600), 6);
    }
}
