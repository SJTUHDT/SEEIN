using Seein.Core;
using Seein.Core.Hosting;
namespace Seein.Tests;

public sealed class ModuleStartupReadinessTests
{
    [Fact] public void EmptyEarlySnapshotDoesNotLoseDelayedModuleRefresh() =>
        Assert.False(ModuleStartupReadiness.IsSettled(["display"], new(false, [])));
    [Fact] public void StartingSnapshotDoesNotFinishReadinessHandshake() =>
        Assert.False(ModuleStartupReadiness.IsSettled(["display"], new(true, [new("display", true)])));
    [Fact] public void SlowModuleKeepsInitialPageRefreshPending() =>
        Assert.False(ModuleStartupReadiness.IsSettled(["display", "applications"], new(false, [new("display", true), new("applications", false)])));
    [Fact] public void ReadyModulesTriggerTheFinalHelloAndRefresh() =>
        Assert.True(ModuleStartupReadiness.IsSettled(["display", "devices"], new(false, [new("display", true), new("devices", true)])));
    [Fact] public void FailedOptionalModuleDoesNotBlockHealthyModuleRefresh() =>
        Assert.True(ModuleStartupReadiness.IsSettled(["display", "devices"], new(false, [new("display", true), new("devices", false, "load failed")])));
}
