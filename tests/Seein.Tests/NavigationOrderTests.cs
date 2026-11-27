using Seein.Core.Configuration;
namespace Seein.Tests;
public sealed class NavigationOrderTests
{
    [Fact]public void SavedOrderIsStableAndNewPluginsGetAPlace()
    {
        Assert.Equal(new[]{"devices","overview","energy","new"},NavigationOrder.Arrange(["overview","energy","devices","new"],["devices","overview","missing"],["overview","energy","devices"]));
    }
    [Fact]public void RememberingVisibleOrderRetainsDisabledPluginPosition()
    {
        var saved=NavigationOrder.Remember(["overview","disabled-plugin","devices","energy"],["energy","overview","devices"]);
        Assert.Equal(new[]{"energy","disabled-plugin","overview","devices"},saved);
        Assert.Equal(saved,NavigationOrder.Arrange(["overview","disabled-plugin","devices","energy"],saved,[]));
    }
    [Fact]public void DuplicateAndRemovedNavigationCannotCreateDuplicateButtons()
    {
        Assert.Equal(new[]{"overview","devices"},NavigationOrder.Arrange(["overview","devices","devices"],["removed","overview","overview"],["overview","devices"]));
    }
}
