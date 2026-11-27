using System.Text.Json.Nodes;

namespace Seein.Core.Tools;

public static class SearchSiteSettings
{
    public static JsonArray Prepare(JsonNode value, string? defaultKeyword = null)
    {
        var sites = value.DeepClone() as JsonArray ?? throw new ArgumentException("搜索站点列表格式无效。");
        if (sites.Count == 0) throw new ArgumentException("至少保留一个搜索站点。");
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sites.Count; i++)
        {
            var row = sites[i] as JsonObject ?? throw new ArgumentException($"第 {i + 1} 个站点格式无效。");
            string Read(string key) => row[key]?.GetValue<string>().Trim() ?? "";
            var name = Read("Name"); var keyword = Read("Keyword"); var url = Read("UrlTemplate");
            if (name.Length == 0) throw new ArgumentException($"请填写第 {i + 1} 个站点的名称。");
            if (keyword.Length == 0 || keyword.Any(char.IsWhiteSpace)) throw new ArgumentException($"「{name}」的关键词不能为空或包含空格。");
            if (!keywords.Add(keyword)) throw new ArgumentException($"关键词「{keyword}」重复，请为每个站点设置不同关键词。");
            url = url.Replace("%s", "{query}", StringComparison.Ordinal).Replace("{q}", "{query}", StringComparison.Ordinal)
                .Replace("%7Bquery%7D", "{query}", StringComparison.OrdinalIgnoreCase);
            if (!url.Contains("{query}", StringComparison.Ordinal))
                throw new ArgumentException($"「{name}」需要搜索地址模板，例如 https://example.com/search?q={{query}}；{{query}} 代表搜索内容。");
            if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
            if (!Uri.TryCreate(url.Replace("{query}", "test", StringComparison.Ordinal), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
                throw new ArgumentException($"「{name}」的搜索地址必须是有效的 HTTP 或 HTTPS 地址。");
            row["Name"] = name; row["Keyword"] = keyword; row["UrlTemplate"] = url;
        }
        if (defaultKeyword is not null && !keywords.Contains(defaultKeyword))
            throw new ArgumentException("列表中需要保留当前默认站点；请先更改默认站点关键词，再移除它。");
        return sites;
    }
}
