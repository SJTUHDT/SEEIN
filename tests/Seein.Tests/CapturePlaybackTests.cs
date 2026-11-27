using Seein.Devices.CapturePlayback;

namespace Seein.Tests;

public sealed class CapturePlaybackTests
{
    private static readonly CaptureMode High = new(2560, 1440, 120, 1, "JPEG");
    private static readonly CaptureMode Medium = new(2560, 1440, 60, 1, "JPEG");
    private static readonly CaptureMode Low = new(1920, 1080, 60, 1, "YUYV");

    [Fact] public void AutoPrefersActual1440p120Mjpeg() => Assert.Equal(High, CapturePolicy.SelectMode([Low, Medium, High], ""));
    [Fact] public void AutoFallsBackTo1440p60Before1080p60() => Assert.Equal(Medium, CapturePolicy.SelectMode([Low, Medium], ""));
    [Fact] public void Fractional120DoesNotGetRoundedIntoInventedFrames()
    {
        var fractional = High with { Numerator = 120000, Denominator = 1001 };
        Assert.Equal(fractional, CapturePolicy.SelectMode([Low, fractional], ""));
        Assert.Equal(119.88011988, fractional.FramesPerSecond, 7);
    }
    [Fact] public void ManualChoiceNeverSilentlyFallsBack() => Assert.Null(CapturePolicy.SelectMode([Low], High.Key));
    [Fact] public void ManualSelectionPreservesPixelFormat()
    {
        var raw = High with { PixelFormat = "YUYV" };
        Assert.Equal(raw, CapturePolicy.SelectMode([High, raw], raw.Key));
    }
    [Fact] public void InvalidModesAreNotSelected() => Assert.Null(CapturePolicy.SelectMode([High with { Denominator = 0 }], ""));
    [Fact] public void AudioPairingNeverPicksUnrelatedMicrophone() => Assert.Null(CapturePolicy.MatchAudio("UGREEN 45647", [new("mic", "麦克风")]));
    [Fact] public void AudioPairingRequiresUniqueMatchingDevice()
    {
        var card = new CaptureEndpoint("card", "UGREEN 45647 (UGREEN 45647)");
        Assert.Equal(card, CapturePolicy.MatchAudio("UGREEN 45647", [new("mic", "麦克风"), card]));
        Assert.Null(CapturePolicy.MatchAudio("UGREEN 45647", [card, card with { Id = "second" }]));
    }
    [Fact] public void ReconnectBackoffIsBounded() => Assert.Equal(new[] { 2d, 4, 8, 15, 15 }, Enumerable.Range(0, 5).Select(i => CapturePolicy.ReconnectDelay(i).TotalSeconds));

    private sealed class Frame : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
    [Fact] public void SlowRendererOnlyReceivesLatestFrameAndReleasesOldOne()
    {
        using var slot = new LatestFrameSlot<Frame>();
        var old = new Frame(); var latest = new Frame();
        slot.Offer(old); slot.Offer(latest);
        Assert.Equal(1, old.Disposals); Assert.Equal(1, slot.Dropped);
        Assert.Same(latest, slot.Take()); Assert.Null(slot.Take()); Assert.Equal(0, latest.Disposals);
        latest.Dispose();
    }
    [Fact] public void StopReleasesWaitingAndSubsequentFramesExactlyOnce()
    {
        var slot = new LatestFrameSlot<Frame>(); var waiting = new Frame(); var late = new Frame();
        slot.Offer(waiting); slot.Dispose(); slot.Dispose();
        Assert.False(slot.Offer(late)); Assert.Equal(1, waiting.Disposals); Assert.Equal(1, late.Disposals); Assert.Null(slot.Take());
    }
    [Fact] public void HiddenSurfaceClearsPendingImage()
    {
        using var slot = new LatestFrameSlot<Frame>(); var frame = new Frame();
        slot.Offer(frame); slot.Clear(); Assert.Equal(1, frame.Disposals); Assert.Null(slot.Take());
    }
    [Fact] public async Task FrameSlotSupportsProducerConsumerRaces()
    {
        using var slot = new LatestFrameSlot<Frame>();
        var frames = Enumerable.Range(0, 5000).Select(_ => new Frame()).ToArray();
        var producer = Task.Run(() => { foreach (var frame in frames) slot.Offer(frame); });
        while (!producer.IsCompleted) { slot.Take()?.Dispose(); await Task.Yield(); }
        await producer; slot.Clear();
        Assert.All(frames, frame => Assert.Equal(1, frame.Disposals));
    }
    [Fact] public void AudioUnderflowReturnsSilenceInsteadOfOldAudio()
    {
        var ring = new LiveAudioBuffer(192000, 4); var output = Enumerable.Repeat((byte)255, 400).ToArray();
        Assert.Equal(400, ring.Read(output, 0, 400)); Assert.All(output, b => Assert.Equal(0, b));
    }
    [Fact] public void AudioBacklogCannotExceed100Milliseconds()
    {
        var ring = new LiveAudioBuffer(192000, 4);
        var input = Enumerable.Range(0, 38400).Select(i => (byte)i).ToArray();
        ring.Write(input, 0, input.Length);
        Assert.Equal(19200, ring.BufferedBytes); Assert.True(ring.DiscardedBytes >= 19200);
        var output = new byte[19200]; ring.Read(output, 0, output.Length);
        Assert.Equal(input.AsSpan(19200).ToArray(), output);
    }
    [Fact] public void AudioClockDriftIsTrimmedOnSampleBoundaries()
    {
        var ring = new LiveAudioBuffer(192000, 8); var input = new byte[18000]; ring.Write(input, 0, input.Length);
        ring.Read(new byte[800], 0, 800);
        Assert.True(ring.DiscardedBytes > 0); Assert.Equal(0, ring.DiscardedBytes % 8);
    }
    [Fact] public void AudioRingWrapPreservesSampleOrder()
    {
        var ring = new LiveAudioBuffer(1000, 2);
        var a = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        ring.Write(a, 0, 40); var first = new byte[30]; ring.Read(first, 0, 30); Assert.Equal(a[..30], first);
        ring.Write(a, 0, 40); var second = new byte[50]; ring.Read(second, 0, 50);
        Assert.Equal(a[30..].Concat(a), second);
    }
    [Fact] public void ProfileRoundTripKeepsCardModeAndIndependentVolume()
    {
        var root = Path.Combine(Path.GetTempPath(), "Seein-CaptureTests-" + Guid.NewGuid().ToString("N"));
        var store = new CaptureProfileStore(Path.Combine(root, "capture.json"));
        var profile = new CaptureProfile { VideoId = "card", AudioId = "card-audio", AudioSelectionSaved = true, ModeKey = High.Key, Volume = 24, Muted = true, WindowLeft = -1000, WindowTop = 20 };
        try { store.Save(profile); Assert.Equal(profile, store.Load()); }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void CorruptProfileReturnsSafeDefaults()
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, "{partial"); Assert.Equal(new CaptureProfile(), new CaptureProfileStore(path).Load()); }
        finally { File.Delete(path); }
    }
    [Fact] public void ProfileClampsInvalidValuesWithoutAutoStarting()
    {
        var actual = CapturePolicy.Normalize(new() { Volume = 999, WindowWidth = double.NaN, WindowHeight = 10 });
        Assert.Equal(100, actual.Volume); Assert.Equal(1100, actual.WindowWidth); Assert.Equal(400, actual.WindowHeight);
    }
    [Fact] public void NullJsonIdentifiersCannotBreakStartOrModeSelection()
    {
        var profile = CapturePolicy.Normalize(new() { VideoId = null!, AudioId = null!, ModeKey = null!, OutputId = null! });
        Assert.Equal("", profile.VideoId); Assert.Equal("", profile.AudioId); Assert.Equal("", profile.ModeKey); Assert.Equal("", profile.OutputId);
    }
}
