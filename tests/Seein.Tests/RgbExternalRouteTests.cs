using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Seein.Core;
using Seein.Core.Lighting;
using Seein.Service;
using static Seein.Core.Lighting.OpenRgbProtocol;

namespace Seein.Tests;

public sealed class RgbExternalRouteTests
{
    [Theory]
    [InlineData(68)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SavedZoneLengthRestoresNonzeroDefaultsAndDoesNotWriteAgainWhenEqual(int savedCount)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));var token=deadline.Token;
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var board=new SimulatedGpu("External Control",true,standInForBoard:true,resizable:true);
        var server=board.ServeAsync(listener,token);
        await using var provider=new OpenRgbProvider(((IPEndPoint)listener.LocalEndpoint).Port,5);
        using var service=new RgbService(NullLogger<RgbService>.Instance,new PanelState(),new RgbEngineHost(NullLogger<RgbEngineHost>.Instance),provider);
        var device=Assert.Single(await provider.RefreshAsync(token:token)).Info;
        service.Initialize(RgbEffects.DefaultSettings() with{Enabled=false,AutoStartEngine=false,ZoneSizes=[new(device.DeviceKey,0,"JARGB 2",savedCount)]});
        try
        {
            var restored=await service.RefreshAsync(token);Assert.Equal(savedCount,Assert.Single(restored.Devices).Info.Zones[0].LedCount);
            Assert.Equal(savedCount==1?0:1,board.ResizeWrites.Count);
            await service.RefreshAsync(token);Assert.Equal(savedCount==1?0:1,board.ResizeWrites.Count);
        }
        finally{await provider.DisposeAsync();await server;}
    }

    [Theory]
    [InlineData("External Control", "mode", false)]
    [InlineData("External Source", "solid", true)]
    public async Task GroupCommandsWriteOnlyExternalModeToTheGpu(string externalName, string preview, bool initiallyExternal)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var gpu = new SimulatedGpu(externalName, initiallyExternal);
        var server = gpu.ServeAsync(listener, token);
        await using var provider = new OpenRgbProvider(((IPEndPoint)listener.LocalEndpoint).Port, 5);
        using var service = new RgbService(NullLogger<RgbService>.Instance, new PanelState(),
            new RgbEngineHost(NullLogger<RgbEngineHost>.Instance), provider);
        try
        {
            var device = Assert.Single(await provider.RefreshAsync(token: token)).Info;
            var input = device.Modes.Single(mode => mode.Name == externalName).Index;
            var runway = device.Modes.Single(mode => mode.Name == "Runway").Index;
            var nativeRainbow = device.Modes.Single(mode => mode.Name == "Rainbow Wave").Index;
            var wave = RgbEffects.DefaultsFor(RgbEffects.Wave);
            service.Initialize(RgbEffects.DefaultSettings() with
            {
                Enabled = true, AutoStartEngine = false, GroupEffect = wave,
                ExternalControlPreferences = new Dictionary<string, bool> { [device.DeviceKey] = false }
            });
            await service.StartAsync(token);
            // Startup must write even when the controller reports External already. A stale native readback
            // needs a retry on the next enumeration, without flooding the GPU at animation-frame rate.
            while (gpu.ModeWrites.IsEmpty) await Task.Delay(10, token);
            await Task.Delay(150, token);
            Assert.Equal(new[] { input }, gpu.ModeWrites.ToArray());
            await WaitForModeAsync(externalName);
            Assert.Equal(initiallyExternal ? 1 : 3, gpu.ModeWrites.Count);
            Assert.All(gpu.ModeWrites, written => Assert.Equal(input, written));
            Assert.Equal(0, gpu.LedWrites);

            var previewResult = await SendAsync(new(preview, DeviceAddress: device.Address, ModeValue: runway,
                Colors: [new RgbColor(0x42, 0xAB, 0x8C)]));
            Assert.Equal(preview == "solid" ? "Static" : "Runway", Assert.Single(previewResult.Status!.Devices).Info.ActiveMode);
            await AssertExternalCommandAsync(new("groupEffect", GroupEffect: wave));
            // The next two actions start with a cached successful external readback.
            await AssertExternalCommandAsync(new("groupEffect", GroupEffect: wave));
            await SendAsync(new("saveGroupProfile", GroupEffect: wave, ProfileName: "Four colors"));
            var preset = Assert.Single(service.Settings.Profiles!, profile => profile.WholeSystem);
            await AssertExternalCommandAsync(new("loadProfile", ProfileId: preset.Id));
            Assert.Equal(wave.Colors, service.Settings.GroupEffect!.Colors);
            await AssertExternalCommandAsync(new("loadGroupProfile", ProfileId: RgbEffects.Rainbow));
            Assert.Equal(RgbEffects.Rainbow, service.Settings.GroupEffect!.EffectId);
            await SendAsync(new("saveProfile", DeviceAddress: device.Address, ProfileId: "gpu-runway",
                EffectId: "Runway preview", ModeName: "Runway"));
            var rejected = await service.HandleCommandAsync(new("loadGroupProfile", ProfileId: "gpu-runway"), token);
            Assert.False(rejected.Success);
            Assert.Equal(RgbEffects.Rainbow, service.Settings.GroupEffect!.EffectId);

            await SendAsync(new("bootMode", DeviceAddress: device.Address, ModeValue: runway));
            await WaitForModeAsync("Runway");
            await AssertExternalCommandAsync(new("groupEffect", GroupEffect: RgbEffects.DefaultsFor(RgbEffects.Rainbow)));
            await SendAsync(new("mode", DeviceAddress: device.Address, ModeValue: nativeRainbow));
            await WaitForModeAsync("Rainbow Wave");
            await AssertExternalCommandAsync(new("solidAll", Colors: [new RgbColor(30, 206, 171)]));

            async Task AssertExternalCommandAsync(RgbCommandRequest command)
            {
                var start = gpu.ModeWrites.Count;
                var ledWrites = gpu.LedWrites;
                await SendAsync(command);
                while (gpu.ModeWrites.Count == start) await Task.Delay(10, token);
                await WaitForModeAsync(externalName);
                await Task.Delay(100, token);
                Assert.Equal(new[] { input }, gpu.ModeWrites.Skip(start).ToArray());
                Assert.Equal(ledWrites, gpu.LedWrites);
            }

            async Task WaitForModeAsync(string expected)
            {
                while (Assert.Single((await SendAsync(new("refresh"))).Status!.Devices).Info.ActiveMode != expected)
                    await Task.Delay(25, token);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await server;
        }

        async Task<RgbCommandResult> SendAsync(RgbCommandRequest command)
        {
            var result = await service.HandleCommandAsync(command, token);
            Assert.True(result.Success, result.Error);
            return result;
        }
    }

    [Fact]
    public async Task GroupSolidReportsMissingExternalInputWithoutWritingToTheNitro()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var gpu = new SimulatedGpu("Unknown mode", initiallyExternal: false);
        var server = gpu.ServeAsync(listener, token);
        await using var provider = new OpenRgbProvider(((IPEndPoint)listener.LocalEndpoint).Port, 5);
        using var service = new RgbService(NullLogger<RgbService>.Instance, new PanelState(),
            new RgbEngineHost(NullLogger<RgbEngineHost>.Instance), provider);
        try
        {
            var device = Assert.Single(await provider.RefreshAsync(token: token));
            Assert.Null(RgbEffects.ExternalInput(device.Info));
            service.Initialize(RgbEffects.DefaultSettings() with { AutoStartEngine = false });
            Assert.True((await service.HandleCommandAsync(new("refresh"), token)).Success);
            var result = await service.HandleCommandAsync(new("solidAll", Colors: [new RgbColor(12, 50, 140)]), token);
            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Empty(gpu.ModeWrites);
            Assert.Equal(0, gpu.LedWrites);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await server;
        }
    }

    [Fact]
    public async Task NativeBoardPlaybackExcludesPixelFramesAndRestoresDirectForPreviews()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var sdk = new SimulatedGpu("External Control", initiallyExternal: true, standInForBoard: true);
        var server = sdk.ServeAsync(listener, token);
        await using var provider = new OpenRgbProvider(((IPEndPoint)listener.LocalEndpoint).Port, 5);
        var board = new SimulatedBoard();
        using var service = new RgbService(NullLogger<RgbService>.Instance, new PanelState(),
            new RgbEngineHost(NullLogger<RgbEngineHost>.Instance), provider, board);
        try
        {
            var info = Assert.Single(await provider.RefreshAsync(token: token)).Info;
            var wave = RgbEffects.DefaultsFor(RgbEffects.Wave);
            var calibration = new RgbColorCalibration(White: new RgbColor(60, 60, 255));
            service.Initialize(RgbEffects.DefaultSettings() with
            {
                Enabled = true, GroupEffect = wave, AutoStartEngine = false,
                ZoneCalibrations = [new(info.DeviceKey, 0, "JARGB 2", calibration)]
            });
            await service.StartAsync(token);
            while (service.GetStatus().GroupPlayback is null) await Task.Delay(10, token);
            var start = Assert.Single(board.Applications);
            Assert.Equal(wave.Colors, start.Effect.Colors);
            Assert.Equal(wave.Colors.Select(calibration.Correct), start.Palette);
            Assert.Equal("原生水波 · 显卡跟随校正色，机箱使用原色", service.GetStatus().GroupPlayback);
            await Task.Delay(600, token);
            Assert.Single(board.Applications);
            Assert.Equal(0, sdk.LedWrites);

            Assert.True((await service.HandleCommandAsync(new("colors", DeviceAddress: info.Address,
                Colors: [new RgbColor(100, 50, 25)]), token)).Success);
            Assert.Equal(1, board.DirectSelections);
            Assert.Equal(1, sdk.LedWrites);
            Assert.Null(service.GetStatus().GroupPlayback);
            await Task.Delay(550, token);
            Assert.Single(board.Applications);
            Assert.True((await service.HandleCommandAsync(new("groupEffect", GroupEffect: wave), token)).Success);
            Assert.Equal(2, board.Applications.Count);
            Assert.Equal(1, sdk.LedWrites);

            var follow = await service.HandleCommandAsync(new("groupEffect", GroupEffect: wave with { UseZoneCalibration = false }), token);
            Assert.True(follow.Success);
            Assert.Equal("原生水波 · 显卡与机箱跟随整机", follow.Status!.GroupPlayback);
            Assert.Null(board.Applications.Last().Palette);
            Assert.Equal(wave.Colors, board.Applications.Last().Effect.Colors);

            board.FailNative = true;
            var failed = await service.HandleCommandAsync(new("groupEffect", GroupEffect: wave), token);
            Assert.False(failed.Success);
            Assert.Null(failed.Status!.GroupPlayback);
            await Task.Delay(550, token);
            Assert.Equal(4, board.Applications.Count);
            Assert.Equal(1, sdk.LedWrites);

            board.FailNative = false;
            foreach (var id in new[] { RgbEffects.Breathing, RgbEffects.Temperature, RgbEffects.ColorCycle })
            {
                var before = board.Applications.Count;
                var moving = await service.HandleCommandAsync(new("groupEffect", GroupEffect: RgbEffects.DefaultsFor(id)), token);
                Assert.True(moving.Success, moving.Error);
                Assert.Contains(RgbEffects.NameOf(id), moving.Status!.GroupPlayback);
                await Task.Delay(650, token);
                var frames = board.Applications.Skip(before).ToArray();
                if (id == RgbEffects.ColorCycle)
                    Assert.InRange(frames.Length, 9, 22); // 25 fps, including transition from the 125 ms timer.
                else
                    Assert.InRange(frames.Length, id == RgbEffects.Temperature ? 1 : 2, 7);
                Assert.All(frames, application =>
                {
                    Assert.Equal(RgbEffects.Static, application.Effect.EffectId);
                    Assert.Single(application.Effect.Colors);
                });
                if (id == RgbEffects.Temperature) Assert.Single(frames);
                Assert.Equal(1, sdk.LedWrites);
            }
            var boardDefault = await service.HandleCommandAsync(new("groupEffect", GroupEffect: RgbEffects.DefaultsFor(RgbEffects.BoardDefault)), token);
            Assert.True(boardDefault.Success, boardDefault.Error);
            Assert.Equal("主板默认 · 显卡与机箱跟随整机", boardDefault.Status!.GroupPlayback);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await server;
        }
    }

    [Fact]
    public async Task ZoneCalibrationValidatesBeforeSavingAndKeepsMeasurementsForLegacyClients()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var sdk = new SimulatedGpu("External Control", initiallyExternal: true, standInForBoard: true);
        var server = sdk.ServeAsync(listener, token);
        await using var provider = new OpenRgbProvider(((IPEndPoint)listener.LocalEndpoint).Port, 5);
        var board = new SimulatedBoard();
        using var service = new RgbService(NullLogger<RgbService>.Instance, new PanelState(),
            new RgbEngineHost(NullLogger<RgbEngineHost>.Instance), provider, board);
        try
        {
            var device = Assert.Single(await provider.RefreshAsync(token: token)).Info;
            var sample = new RgbCalibrationSample(RgbColor.FromHex("#FF3366"), RgbColor.FromHex("#A01092"));
            var calibration = new RgbColorCalibration(White: RgbColor.FromHex("#3C3CFF"), Samples: [sample]);
            var invalid = calibration with { Samples = [sample, sample] };
            service.Initialize(RgbEffects.DefaultSettings() with
            {
                AutoStartEngine = false,
                ZoneCalibrations = [new(device.DeviceKey, 0, "JARGB 2", invalid)]
            });
            Assert.True((await service.HandleCommandAsync(new("refresh"), token)).Success);
            Assert.Contains("额外校色样点未加载", service.GetStatus().LastError);
            Assert.Same(invalid, Assert.Single(service.Settings.ZoneCalibrations!).Colors);

            var saves = 0;
            service.SettingsChanged += _ => saves++;
            Assert.True((await Set(calibration)).Success);
            Assert.Null(service.GetStatus().LastError);
            var original = service.Settings;
            foreach (var rejected in new[]
            {
                invalid,
                calibration with { Samples = [new(null, sample.Output)] },
                calibration with { Samples = [new(sample.Target, null)] },
                calibration with { Samples = Enumerable.Repeat(sample, 33).ToArray() },
                calibration with { Samples = [new(new RgbColor(255, 255, 255), RgbColor.FromHex("#FFFFFF"))] }
            })
            {
                var response = await Set(rejected);
                Assert.False(response.Success);
                Assert.False(string.IsNullOrWhiteSpace(response.Error));
                Assert.Same(original, service.Settings);
            }
            Assert.Equal(1, saves);

            var stored = JsonSerializer.Deserialize<RgbSettings>(JsonSerializer.Serialize(service.Settings, JsonDefaults.Options), JsonDefaults.Options);
            Assert.Equal(sample, Assert.Single(Assert.Single(stored!.ZoneCalibrations!).Colors.Samples!));
            var legacy = JsonSerializer.Deserialize<RgbColorCalibration>("""{"white":{"r":61,"g":61,"b":255}}""", JsonDefaults.Options)!;
            Assert.True((await Set(legacy)).Success);
            var merged = Assert.Single(service.Settings.ZoneCalibrations!).Colors;
            Assert.Equal(legacy.White, merged.White);
            Assert.Equal(sample, Assert.Single(merged.Samples!));
            Assert.True((await Set(legacy with { Samples = [] })).Success);
            Assert.Empty(Assert.Single(service.Settings.ZoneCalibrations!).Colors.Samples!);
            Assert.True((await Set(null)).Success);
            Assert.Null(service.Settings.ZoneCalibrations);
            Assert.Equal(4, saves);

            Task<RgbCommandResult> Set(RgbColorCalibration? value) => service.HandleCommandAsync(
                new("setZoneCalibration", DeviceAddress: device.Address, ZoneIndex: 0, Calibration: value), token);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await server;
        }
    }

    private sealed class SimulatedBoard : IMsiMysticLightBoard
    {
        public ConcurrentQueue<(RgbEffectSettings Effect, IReadOnlyList<RgbColor>? Palette)> Applications { get; } = new();
        public int DirectSelections { get; private set; }
        public bool FailNative { get; set; }
        public bool Matches(RgbDeviceInfo info) => info.Type == "主板";

        public RgbWriteResult Apply(RgbDeviceInfo info, RgbEffectSettings effect, IReadOnlyList<RgbColor>? jargb2Colors = null,int? ringZone=null)
        {
            Applications.Enqueue((effect, jargb2Colors));
            return FailNative ? RgbWriteResult.Reject("模拟主板回读失败") : RgbWriteResult.ReadBack;
        }

        public RgbWriteResult EnterDirect(RgbDeviceInfo info)
        {
            DirectSelections++;
            return RgbWriteResult.ReadBack;
        }

        public RgbWriteResult UpdateAudio(byte master, byte left, byte right) => RgbWriteResult.Sent;
        public RgbWriteResult UpdateRing(RgbDeviceInfo info,int zone,IReadOnlyList<RgbColor> frame,bool verify)=>
            throw new InvalidOperationException("This fixture has no ring zone");
    }

    // The same redacted SDK capture can stand in for a single-LED board in transport arbitration tests.
    private sealed class SimulatedGpu
    {
        private readonly byte[] _controller;
        private readonly int _activeModeOffset;
        private readonly int _externalMode;
        private readonly int _ignoredExternalWrites;
        private int _externalWrites;
        private int _ledWrites;
        public ConcurrentQueue<int> ModeWrites { get; } = new();
        public ConcurrentQueue<int> ResizeWrites { get; } = new();
        public int LedWrites => Volatile.Read(ref _ledWrites);

        public SimulatedGpu(string externalName, bool initiallyExternal, bool standInForBoard = false, bool resizable = false)
        {
            var original = Convert.FromBase64String(OpenRgbProtocolTests.SapphireV5);
            if (standInForBoard)
            {
                BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(sizeof(int)), 0);
                original = Rename(original, "Static", "Direct");
                original = Rename(original, "GPU Zone", "JARGB 2");
                var zone = BuildClientName("JARGB 2");
                var typeOffset = original.AsSpan().IndexOf(zone) + zone.Length;
                BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(typeOffset), (int)RgbZoneType.Linear);
                if(resizable)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(typeOffset+4),0);
                    BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(typeOffset+8),240);
                    BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(typeOffset+20),3);
                }
            }
            _controller = Rename(original, RgbEffects.ExternalControl, externalName);
            BinaryPrimitives.WriteInt32LittleEndian(_controller, _controller.Length);

            // SDK v5 puts six length-prefixed identity strings between device type and mode count.
            var offset = 2 * sizeof(int);
            for (var field = 0; field < 6; field++)
                offset += sizeof(ushort) + BinaryPrimitives.ReadUInt16LittleEndian(_controller.AsSpan(offset));
            _activeModeOffset = offset + sizeof(ushort);
            var modes = ParseController(0, _controller).Modes;
            _externalMode = modes.Single(mode => mode.Name == externalName).Index;
            _ignoredExternalWrites = initiallyExternal ? 0 : 2;
            SetActiveMode(initiallyExternal ? _externalMode : modes.Single(mode => mode.Name == "Rainbow Wave").Index);
        }

        private static byte[] Rename(byte[] original, string oldValue, string newValue)
        {
            var oldName = BuildClientName(oldValue);
            var newName = BuildClientName(newValue);
            var start = original.AsSpan().IndexOf(oldName);
            byte[] renamed = [.. original.AsSpan(0, start), .. newName, .. original.AsSpan(start + oldName.Length)];
            BinaryPrimitives.WriteUInt16LittleEndian(renamed.AsSpan(start - sizeof(ushort)), (ushort)newName.Length);
            return renamed;
        }

        private void SetActiveMode(int mode) =>
            BinaryPrimitives.WriteInt32LittleEndian(_controller.AsSpan(_activeModeOffset), mode);

        public async Task ServeAsync(TcpListener listener, CancellationToken token)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            client.NoDelay = true;
            using var stream = client.GetStream();
            var header = new byte[HeaderSize];
            while (await stream.ReadAsync(header.AsMemory(0, 1), token) != 0)
            {
                await stream.ReadExactlyAsync(header.AsMemory(1), token);
                var (device, packet, length) = ParseHeader(header);
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, token);
                switch (packet)
                {
                    case RequestProtocol: await ReplyAsync(Int32Payload(5)); break;
                    case SetClientName: break;
                    case SetCustomMode: break;
                    case ControllerCount: await ReplyAsync(Int32Payload(1)); break;
                    case ControllerData: await ReplyAsync(_controller); break;
                    case ResizeZone:
                        var requested=BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));ResizeWrites.Enqueue(requested);
                        var zoneStart=_controller.AsSpan().IndexOf(BuildClientName("JARGB 2"))+BuildClientName("JARGB 2").Length;
                        BinaryPrimitives.WriteInt32LittleEndian(_controller.AsSpan(zoneStart+12),requested);
                        break;
                    case UpdateMode:
                        var mode = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(sizeof(int)));
                        ModeWrites.Enqueue(mode);
                        if (mode == _externalMode && ++_externalWrites <= _ignoredExternalWrites) break;
                        SetActiveMode(mode);
                        break;
                    case UpdateLeds:
                        Interlocked.Increment(ref _ledWrites);
                        // One RGBA LED followed by the empty alias count and controller flags.
                        body.AsSpan(sizeof(int) + sizeof(ushort), 4).CopyTo(_controller.AsSpan(_controller.Length - 10));
                        break;
                    default: throw new InvalidDataException($"Unexpected SDK packet {packet}");
                }

                async Task ReplyAsync(byte[] payload)
                {
                    await stream.WriteAsync(BuildHeader(device, packet, payload.Length), token);
                    await stream.WriteAsync(payload, token);
                }
            }
        }
    }
}
