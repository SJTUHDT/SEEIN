using System.Text.Json.Nodes;
using Seein.Pet;

namespace Seein.Tests;

public sealed class NativePetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SeeinTests", "native-pet-" + Guid.NewGuid().ToString("N"));
    private string Assets => Path.Combine(_root, "assets");
    private PetConfiguration Config => new(_root, Assets);

    public NativePetTests()
    {
        Directory.CreateDirectory(Assets);
        File.WriteAllText(Path.Combine(Assets, "config.jsonc"), """
            { // Legacy JSONC defaults
              "physics": {"gravity":1400,"restitution":0.78},
              "voiceName":"edge/zh-CN-XiaoyiNeural",
              "pets":[{"id":"main","name":"宠物","size":462,"display":"both","whisperEnabled":true,
                "position":{"corner":"bottom-right","marginX":20,"marginY":0}}]
            }
            """);
    }

    [Fact]
    public void LegacyPartialPetsInheritDefaultsAndKeepUnknownFields()
    {
        Config.SaveMain(JsonNode.Parse("""{"pets":[{"id":"custom","position":{"marginX":70},"customField":42}],"futureSetting":{"keep":true}}""")!.AsObject());
        var pet = Assert.Single(Config.ReadPets());
        Assert.True(pet.Settings.Flag("whisperEnabled"));
        Assert.Equal("bottom-right", pet.Settings["position"]!.Text("corner"));
        Assert.Equal(70, pet.Settings["position"]!.Number("marginX"));
        Config.UpdatePet("custom", p => p["display"] = "none");
        var saved = PetConfiguration.ReadObject(Config.MainPath);
        Assert.True(saved["futureSetting"]!.Flag("keep"));
        Assert.Equal(42, saved["pets"]![0]!.Number("customField"));
    }

    [Fact]
    public void ExplicitPhysicsBlockReplacesWholeDefaultBlock()
    {
        Config.SaveMain(JsonNode.Parse("""{"physics":{"gravity":50}}""")!.AsObject());
        var physics = Config.ReadEntries()["main"]!["physics"]!.AsObject();
        Assert.Equal(50, physics.Number("gravity"));
        Assert.False(physics.ContainsKey("restitution"));
    }

    [Fact]
    public void EmbeddedSettingsPatchKeepsTheLatestPositionAndUnknownFields()
    {
        Config.SaveMain(JsonNode.Parse("""{"pets":[{"id":"main","futureFlag":42,"position":{"marginX":150}}]}""")!.AsObject());
        Config.UpdatePet("main", pet => pet["position"] = new JsonObject { ["marginX"] = 250 });
        Config.PatchPet("main", new JsonObject { ["name"] = "新名字", ["size"] = 450, ["display"] = "desktop" });
        var pet = Assert.Single(Config.ReadPets());
        Assert.Equal("新名字", pet.Name);
        Assert.Equal(250, pet.Settings["position"]!.Number("marginX"));
        Assert.Equal(42, pet.Settings.Number("futureFlag"));
    }

    [Theory]
    [InlineData("""{"id":"renamed"}""")]
    [InlineData("""{"display":"invalid"}""")]
    [InlineData("""{"size":-1}""")]
    public void InvalidEmbeddedSettingsNeverReplaceSavedConfiguration(string patch)
    {
        Config.SaveMain(new JsonObject { ["futureFlag"] = 42 });
        var before = File.ReadAllText(Config.MainPath);
        Assert.Throws<InvalidDataException>(() => Config.PatchPet("main", JsonNode.Parse(patch)!.AsObject()));
        Assert.Equal(before, File.ReadAllText(Config.MainPath));
    }

    [Fact]
    public void EmbeddedAddAndRemoveKeepTheOriginalAndRejectRemovingTheLastPet()
    {
        Config.SaveMain(new JsonObject { ["futureFlag"] = 42 });
        Config.AddPet();
        var added = Assert.Single(Config.ReadPets(), p => p.Id != "main");
        Assert.Equal("新桌宠", added.Name);
        Config.RemovePet(added.Id);
        Assert.Equal("main", Assert.Single(Config.ReadPets()).Id);
        var before = File.ReadAllText(Config.MainPath);
        Assert.Throws<InvalidDataException>(() => Config.RemovePet("main"));
        Assert.Equal(before, File.ReadAllText(Config.MainPath));
        Assert.Equal(42, PetConfiguration.ReadObject(Config.MainPath).Number("futureFlag"));
    }

    [Fact]
    public void InvalidExistingJsonIsNeverReplaced()
    {
        Directory.CreateDirectory(Config.DirectoryPath);
        File.WriteAllText(Config.MainPath, "{invalid");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Config.SaveMain(new JsonObject { ["name"] = "new" }));
        Assert.Equal("{invalid", File.ReadAllText(Config.MainPath));
    }

    [Theory]
    [InlineData("../pet")]
    [InlineData("..")]
    [InlineData("bad\\id")]
    [InlineData("")]
    public void PetPathsCannotEscapeTheirDirectory(string id) => Assert.Throws<InvalidDataException>(() => PetConfiguration.ValidateId(id));

    [Fact]
    public void FilePetsInheritGlobalPreferencesAndKeepSeparateAssets()
    {
        Config.SaveMain(new JsonObject { ["voiceName"] = "deepseek/stella" });
        var folder = Path.Combine(Config.DirectoryPath, "pet");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fish-config.json"), """{"pets":[{"id":"fish-1"}]}""");
        var pet = Config.ReadPets().Single(p => p.Id == "fish-1");
        Assert.Equal("deepseek/stella", pet.EntrySettings.Text("voiceName"));
        Assert.Equal(Path.Combine(folder, "fish-animation"), pet.AssetDirectory);
    }

    [Fact]
    public void MemoryPreservesLegacyBucketsUnknownFieldsAndOtherPets()
    {
        var pet = Config.ReadPets()[0];
        Directory.CreateDirectory(Config.DirectoryPath);
        var path = Path.Combine(Config.DirectoryPath, "memory.json");
        File.WriteAllText(path, """{"main":{"main":{"custom":"keep","messages":[{"role":"user","content":"以前","ts":1}]}},"other":{"pet":{"messages":[]}}}""");
        var memory = new PetMemory(Config.DirectoryPath);
        memory.Append(pet, new JsonObject { ["role"] = "user", ["content"] = "新消息", ["ts"] = 2 }, new JsonObject { ["role"] = "assistant", ["content"] = "回复", ["ts"] = 2 });
        Assert.Equal(3, memory.Read(pet).Count);
        memory.Clear(pet, new HashSet<long> { 2 });
        var root = PetConfiguration.ReadObject(path);
        Assert.Single(memory.Read(pet));
        Assert.Equal("keep", root["main"]!["main"]!.Text("custom"));
        Assert.NotNull(root["other"]);
    }

    [Fact]
    public void RestingPetStaysOnGroundWithoutContinuousBouncing()
    {
        var motion = new PetMotion { X = 50, Y = 100 };
        for (var index = 0; index < 600; index++) motion.Step(1.0 / 30, 0, 0, 200, 100, 1400, .78, 2.5, true);
        Assert.Equal(100, motion.Y);
        Assert.Equal(0, motion.VelocityY);
    }

    [Fact]
    public void MainEntryMemoryUsesTheLegacyEntryBucketForEveryInstance()
    {
        var pet = Config.ReadPets()[0] with { Id = "second" };
        Directory.CreateDirectory(Config.DirectoryPath);
        File.WriteAllText(Path.Combine(Config.DirectoryPath, "memory.json"), """{"main":{"second":{"messages":[{"role":"user","content":"保留的记忆","ts":1}]}}}""");
        var memory = new PetMemory(Config.DirectoryPath);
        Assert.Single(memory.Read(pet));
        memory.Append(pet, new JsonObject { ["ts"] = 2 }, new JsonObject { ["ts"] = 2 });
        Assert.Equal(3, memory.Read(pet).Count);
        Assert.False(PetConfiguration.ReadObject(Path.Combine(Config.DirectoryPath, "memory.json")).ContainsKey("second"));
    }

    [Fact]
    public async Task ClearingMemoryCancelsAnActiveReplyBeforeItCanWriteBack()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new FakeTransport(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        });
        Directory.CreateDirectory(Path.Combine(_root, "bridge", "private"));
        File.WriteAllText(Path.Combine(_root, "bridge", "private", "pet-bridge-token.txt"), "test-key");
        using var ai = new PetBridgeClient(Config.DirectoryPath, Path.Combine(_root, "bridge"), null, transport);
        var pet = Config.ReadPets()[0];
        var pending = ai.ChatAsync(pet, "测试取消", null, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ai.ClearMemory(pet);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(ai.Memory.Read(pet));
    }

    private sealed class FakeTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Theory]
    [InlineData("https://example.com/v1/")]
    [InlineData("https://127.0.0.1/v1/")]
    [InlineData("http://user:password@localhost/v1/")]
    public void PetBridgeRejectsIndependentRemoteModelEndpoints(string endpoint) =>
        Assert.Throws<InvalidDataException>(() => new PetBridgeClient(Config.DirectoryPath, _root, new Uri(endpoint)));

    [Fact]
    public async Task PetUsesOnlyTheDafeiyuBridgeAndLeavesOldModelSettingsUntouched()
    {
        var directory = Path.Combine(_root, "bridge", "private");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "pet-bridge-token.txt"), "bridge-only-key");
        Directory.CreateDirectory(Config.DirectoryPath);
        var legacy = Path.Combine(Config.DirectoryPath, "standalone-ai.json");
        const string old = """{"baseUrl":"https://example.com/v1","model":"unused","apiKey":"unused-key","keep":42}""";
        File.WriteAllText(legacy, old);
        using var transport = new FakeTransport(async (request, token) =>
        {
            Assert.True(request.RequestUri!.IsLoopback);
            Assert.Equal("bridge-only-key", request.Headers.Authorization!.Parameter);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal("dafeiyu", body.Text("model"));
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"桥接正常"}}]}""") };
        });
        using var client = new PetBridgeClient(Config.DirectoryPath, Path.Combine(_root, "bridge"), null, transport);
        var reply = await client.ChatAsync(Config.ReadPets()[0], "测试", null, default);
        Assert.Equal("桥接正常", reply.Text);
        Assert.Equal(2, client.Memory.Read(Config.ReadPets()[0]).Count);
        Assert.Equal(old, File.ReadAllText(legacy));
    }

    [Fact]
    public void InvisibleVideoBackgroundDoesNotRemainClickable()
    {
        byte[] pixels = [180, 90, 60, 0, 180, 90, 60, 16, 180, 90, 60, 17, 180, 90, 60, 255];
        PetPixels.PrepareForLayeredWindow(pixels);
        Assert.All(pixels.Take(8), channel => Assert.Equal((byte)0, channel));
        Assert.Equal(17, pixels[11]);
        Assert.Equal(new byte[] { 180, 90, 60, 255 }, pixels.Skip(12));
    }

    [Fact]
    public void GentlyPlacedPetStaysAboveTheFloorWithoutGravity()
    {
        var motion = new PetMotion { X = 200, Y = 120 };
        Assert.False(motion.Release([new(0, 0, 0), new(50, 10, 5), new(100, 20, 10)], 100));
        for (var index = 0; index < 3600; index++) motion.Step(1.0 / 60, 0, 0, 1000, 800, 1400, .78, 2.5, true);
        Assert.False(motion.IsFlying);
        Assert.Equal(200, motion.X);
        Assert.Equal(120, motion.Y);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(500, false)]
    [InlineData(600, true)]
    [InlineData(2000, true)]
    public void ReleaseUsesTheLegacySoftSpeedAndFiveHundredDeadZone(double pointerSpeed, bool flying)
    {
        var motion = new PetMotion();
        Assert.Equal(flying, motion.Release([new(0, 0, 0), new(50, pointerSpeed * .05, 0), new(100, pointerSpeed * .1, 0)], 100));
        Assert.Equal(flying, motion.IsFlying);
        if (flying) Assert.Equal(3600 * (1 - Math.Exp(-pointerSpeed / 3600)), motion.VelocityX, 8);
    }

    [Fact]
    public void HoldingBeforeReleaseDiscardsTheEarlierFastMovement()
    {
        var motion = new PetMotion { X = 250, Y = 150 };
        Assert.False(motion.Release([new(0, 0, 0), new(50, 100, 0), new(100, 200, 0)], 251));
        motion.Step(.033, 0, 0, 1000, 800, 1400, .78, 2.5, true);
        Assert.Equal(150, motion.Y);
    }

    [Fact]
    public void CatchingAFlightAndPlacingItDoesNotRetainMomentum()
    {
        var motion = new PetMotion { X = 100, Y = 100 };
        motion.Launch(1200, -600);
        motion.Step(.02, 0, 0, 1000, 800, 1400, .78, 2.5, true);
        var x = motion.X;
        var y = motion.Y;
        motion.Place();
        motion.Step(.05, 0, 0, 1000, 800, 1400, .78, 2.5, true);
        Assert.Equal(x, motion.X);
        Assert.Equal(y, motion.Y);
        Assert.Equal(0, motion.VelocityX);
        Assert.Equal(0, motion.VelocityY);
    }

    [Fact]
    public void FastReleaseFallsBouncesAndEventuallyStops()
    {
        var motion = new PetMotion { X = 100, Y = 100 };
        Assert.True(motion.Release([new(0, 0, 0), new(50, 100, -50), new(100, 200, -100)], 100));
        for (var index = 0; index < 6000 && motion.IsFlying; index++) motion.Step(1.0 / 60, 0, 0, 1000, 800, 1400, .78, 2.5, true);
        Assert.False(motion.IsFlying);
        Assert.Equal(800, motion.Y);
    }

    [Fact]
    public void TwoStationaryPetsDoNotPushEachOtherDownFromTheirPlacedPositions()
    {
        var first = new PetMotion { X = 100, Y = 100 };
        var second = new PetMotion { X = 110, Y = 100 };
        PetMotion.Collide(first, 320, second, 320, .995);
        Assert.Equal(100, first.X);
        Assert.Equal(110, second.X);
        Assert.False(first.IsFlying);
        Assert.False(second.IsFlying);
    }

    [Fact]
    public void SlingshotUsesPullDistanceAndReversesDirectionRegardlessOfReleaseSpeed()
    {
        var velocity = PetDragRelease.Slingshot(192, -96, 320);
        Assert.Equal(-1200, velocity.X);
        Assert.Equal(600, velocity.Y);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
