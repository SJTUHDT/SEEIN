using System.Text.Json.Nodes;
using Seein.Pet;

namespace Seein.Tests;

public sealed class PetMenuTests
{
    [Fact]
    public void PicturePreviewsKeepConfiguredOrderAndTheOriginalTwoGroups()
    {
        var settings = JsonNode.Parse("""{"chatImageEnabled":true,"chatImageLimit":2,"memes":{"第三张":"描述3","第一张":"描述1","第二张":"描述2"}}""")!.AsObject();
        var groups = PetMemeMenu.Build(settings, new HashSet<string> { "第一张", "第二张", "第三张" });
        Assert.Equal(new[] { "纳入对话的配图", "其他" }, groups.Select(g => g.Label));
        Assert.Equal(new[] { "第三张", "第一张" }, groups[0].Entries.Select(e => e.Name));
        Assert.Equal("第二张", Assert.Single(groups[1].Entries).Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(20)]
    public void UnlimitedOrLargeLimitsDoNotCreateAnEmptyOtherGroup(int limit)
    {
        var settings = new JsonObject { ["chatImageEnabled"] = true, ["chatImageLimit"] = limit, ["memes"] = new JsonObject { ["图片"] = "描述" } };
        Assert.Equal("纳入对话的配图", Assert.Single(PetMemeMenu.Build(settings, new HashSet<string> { "图片" })).Label);
    }

    [Fact]
    public void DisabledChatPicturesRemainAvailableWithPreviewsUnderOther()
    {
        var settings = JsonNode.Parse("""{"chatImageEnabled":false,"memes":{"可用":"描述","空描述":" ","未找到文件":"描述"}}""")!.AsObject();
        var group = Assert.Single(PetMemeMenu.Build(settings, new HashSet<string> { "可用", "空描述" }));
        Assert.Equal("其他", group.Label);
        Assert.Equal("可用", Assert.Single(group.Entries).Name);
    }

    [Fact]
    public void CascadingMenuFlipsAtTheScreenEdgeWithoutCoveringItsParent()
    {
        var parent = new PetMenuBox(560, 100, 220, 320);
        var child = PetMenuLayout.Place(220, 320, 120, parent, [parent], new(0, 0, 900, 520));
        Assert.Equal(336, child.X);
        Assert.False(child.Overlaps(parent));
    }

    [Fact]
    public void ThirdMenuColumnAvoidsBothAlreadyOpenColumns()
    {
        PetMenuBox first = new(0, 100, 220, 320), second = new(224, 120, 220, 320);
        var third = PetMenuLayout.Place(220, 320, 140, second, [first, second], new(0, 0, 900, 520));
        Assert.Equal(448, third.X);
        Assert.False(third.Overlaps(first));
        Assert.False(third.Overlaps(second));
    }

    [Fact]
    public void SmallScreensKeepTheMenuInsideTheVisibleArea()
    {
        var child = PetMenuLayout.Place(220, 120, 160, new(0, 0, 220, 180), [new(0, 0, 220, 180)], new(0, 0, 260, 200));
        Assert.InRange(child.X, 4, 36);
        Assert.InRange(child.Y, 4, 76);
    }
}
