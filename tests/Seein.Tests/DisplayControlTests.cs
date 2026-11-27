using Seein.Core.Displays;

namespace Seein.Tests;

public sealed class DisplayControlTests
{
    [Fact]
    public async Task RejectsMismatchedResponsesAndRecoversWithANewHost()
    {
        var flag = Path.Combine(Path.GetTempPath(), "Seein-display-test-" + Guid.NewGuid().ToString("N"));
        var script = "$r=[Console]::ReadLine()|ConvertFrom-Json; if(Test-Path -LiteralPath '" + flag + "'){$id=$r.requestId}else{[IO.File]::WriteAllText('" + flag + "','1');$id='stale'}; @{requestId=$id;success=$true}|ConvertTo-Json -Compress";
        using var client = TestClient(script, TimeSpan.FromSeconds(10));
        try
        {
            var first = await client.SendAsync(new("list"));
            Assert.False(first.Success);
            Assert.Contains("不匹配", first.Error);
            Assert.True((await client.SendAsync(new("list"))).Success);
        }
        finally { if (File.Exists(flag)) File.Delete(flag); }
    }

    [Fact]
    public async Task HungHostTimesOutAndCancellationNeverLeaksAReply()
    {
        using var client = TestClient("$null=[Console]::ReadLine(); Start-Sleep -Seconds 30", TimeSpan.FromMilliseconds(500));
        var response = await client.SendAsync(new("list"));
        Assert.False(response.Success);
        Assert.Contains("超时", response.Error);
        using var cancellation = new CancellationTokenSource(150);
        response = await client.SendAsync(new("list"), cancellation.Token);
        Assert.False(response.Success);
        Assert.Contains("取消", response.Error);
    }

    private static DisplayControlClient TestClient(string script, TimeSpan timeout)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand " + encoded, timeout);
    }

    [Fact]
    public void ParsesActualKtcCapabilitiesWithoutTreatingCommandsAsFeatures()
    {
        var caps = DisplayCapabilities.Parse("(cmds(01 02 03 07 0C)vcp(10 12 14(05 08 0B 0C) 60(11 12 0F 10) D6(01 04 05) DC(00 02 03 05))mccs_ver(2.1))");
        Assert.Equal(new uint[] { 0x11, 0x12, 0x0F, 0x10 }, caps[0x60]);
        Assert.Equal(new uint[] { 1, 4, 5 }, caps[0xD6]);
        Assert.Empty(caps[0x10]);
        Assert.False(caps.ContainsKey(0x01));
        Assert.False(caps.ContainsKey(0x62));
    }

    [Theory]
    [InlineData("(vcp(60(11 12")]
    [InlineData("(vcp(60(11 Z1)))")]
    [InlineData("(cmds(60(11 12)))")]
    public void DoesNotExposeUntrustedEnumValues(string text) => Assert.False(DisplayCapabilities.Parse(text).ContainsKey(0x60));

    [Fact]
    public void RejectsUnsupportedFeaturesAndOutOfRangeValues()
    {
        Assert.True(DisplayFeatures.IsValidValue(new(DisplayFeature.Brightness, "", true, Maximum: 100), 100));
        Assert.False(DisplayFeatures.IsValidValue(new(DisplayFeature.Brightness, "", true, Maximum: 100), 101));
        Assert.False(DisplayFeatures.IsValidValue(new(DisplayFeature.Brightness, "", false), 10));
        var input = new DisplaySetting(DisplayFeature.Input, "", true, Options: new Dictionary<uint, string> { [0x0F] = "DP 1" });
        Assert.True(DisplayFeatures.IsValidValue(input, 0x0F));
        Assert.False(DisplayFeatures.IsValidValue(input, 0x11));
        Assert.False(DisplayFeatures.IsValidValue(input with { Options = null }, 0x0F));
    }

    [Fact]
    public void InputAndPowerAlwaysRequireExplicitAction()
    {
        Assert.True(DisplayFeatures.RequiresExplicitAction(DisplayFeature.Input));
        Assert.True(DisplayFeatures.RequiresExplicitAction(DisplayFeature.Power));
        Assert.False(DisplayFeatures.RequiresExplicitAction(DisplayFeature.Brightness));
    }
}
