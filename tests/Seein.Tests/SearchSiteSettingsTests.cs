using System.Text.Json.Nodes;
using Seein.Core.Tools;

namespace Seein.Tests;

public sealed class SearchSiteSettingsTests
{
    private static JsonObject Site(string keyword, string url = "https://example.com/?q={query}") =>
        new() { ["Name"] = "自定义站点", ["Keyword"] = keyword, ["UrlTemplate"] = url };
    [Fact]
    public void PreparingANewSiteNormalizesASeparateCopy()
    {
        var draft = new JsonArray(Site(" ex ", "example.com/search?q=%s"));
        var result = SearchSiteSettings.Prepare(draft);
        Assert.Equal("ex", result[0]!["Keyword"]!.GetValue<string>());
        Assert.Equal("https://example.com/search?q={query}", result[0]!["UrlTemplate"]!.GetValue<string>());
        Assert.Equal(" ex ", draft[0]!["Keyword"]!.GetValue<string>());
    }
    [Fact]
    public void AHomepageWithoutASearchPlaceholderHasAnActionableError() =>
        Assert.Contains("{query}", Assert.Throws<ArgumentException>(() =>
            SearchSiteSettings.Prepare(new JsonArray(Site("ex", "https://example.com/")))).Message);
    [Fact]
    public void KeywordsAreUniqueIgnoringCase() =>
        Assert.Contains("重复", Assert.Throws<ArgumentException>(() =>
            SearchSiteSettings.Prepare(new JsonArray(Site("ex"), Site("EX")))).Message);
    [Fact]
    public void RemovingTheDefaultSiteExplainsHowToProceed() =>
        Assert.Contains("默认", Assert.Throws<ArgumentException>(() =>
            SearchSiteSettings.Prepare(new JsonArray(Site("ex")), "bing")).Message);
}
