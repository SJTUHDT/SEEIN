using Seein.Core.Apps;

namespace Seein.Tests;

public sealed class AppUsageTrackerTests
{
    private const int Pulse = 10;

    [Fact]
    public void HeartbeatInsideThePulseWindowExtendsTheInterval()
    {
        Assert.True(HeartbeatMerger.Extends(1000, 1005, Pulse));
        var (start, end) = HeartbeatMerger.Merge(1000, 1005, 1010, Pulse);
        Assert.Equal(1000, start);
        Assert.Equal(1010, end);
    }

    [Fact]
    public void HeartbeatAfterThePulseWindowStartsANewInterval()
    {
        Assert.False(HeartbeatMerger.Extends(1005, 1200, Pulse));
        var (start, end) = HeartbeatMerger.Merge(1000, 1005, 1200, Pulse);
        Assert.Equal(1200, start);
        Assert.Equal(1200, end);
    }

    [Fact]
    public void TrackerAccumulatesSecondsCpuAndPowerPerApp()
    {
        var tracker = new AppUsageTracker();
        tracker.BeginTick();
        tracker.Observe("wechat", 1000, "2026-11-16", 5, 0.5, 0, 0.01, 0, Pulse);
        tracker.EndTick();
        tracker.BeginTick();
        tracker.Observe("wechat", 1005, "2026-11-16", 5, 0.6, 0, 0.02, 0, Pulse);
        tracker.EndTick();

        var hours = tracker.DrainHours();
        var hour = Assert.Single(hours);
        Assert.Equal("wechat", hour.AppKey);
        Assert.Equal(10, hour.Seconds, 6);
        Assert.Equal(1.1, hour.CpuSeconds, 6);
        Assert.Equal(0.03, hour.Wh, 6);

        var days = tracker.DrainDays();
        var day = Assert.Single(days);
        Assert.Equal(10, day.Seconds, 6);
        Assert.Equal(0.03, day.CpuWh, 6);

        var spans = tracker.SnapshotSpans();
        var span = Assert.Single(spans);
        Assert.Equal(1000, span.StartUtc);
        Assert.Equal(1005, span.EndUtc);
    }

    [Fact]
    public void TrackerStartsANewSpanAfterAFullGap()
    {
        var tracker = new AppUsageTracker();
        tracker.BeginTick();
        tracker.Observe("qq", 1000, "2026-11-16", 5, 1, 0, 0, 0, Pulse);
        tracker.EndTick();

        tracker.BeginTick();
        tracker.Observe("qq", 1200, "2026-11-16", 5, 1, 0, 0, 0, Pulse);
        tracker.EndTick();

        var span = Assert.Single(tracker.SnapshotSpans());
        Assert.Equal(1200, span.StartUtc);
        Assert.Equal(1.0, span.CpuSeconds, 6);
    }

    [Fact]
    public void SystemRowsDoNotCountTowardsRuntime()
    {
        var tracker = new AppUsageTracker();
        tracker.BeginTick();
        tracker.Observe("__system__", 1000, "2026-11-16", 10, 2, 0, 0.1, 0, Pulse, countsAsRuntime: false);
        tracker.EndTick();

        var day = Assert.Single(tracker.DrainDays());
        Assert.Equal(0, day.Seconds, 6);
        Assert.Equal(0.1, day.Wh, 6);
    }

    [Fact]
    public void AnApplicationThatDisappearsIsDroppedFromTheLiveSet()
    {
        var tracker = new AppUsageTracker();
        tracker.BeginTick();
        tracker.Observe("gone", 1000, "2026-11-16", 5, 0, 0, 0, 0, Pulse);
        tracker.EndTick();
        Assert.Single(tracker.SnapshotSpans());

        tracker.BeginTick();
        tracker.EndTick();
        Assert.Empty(tracker.SnapshotSpans());
    }
}
