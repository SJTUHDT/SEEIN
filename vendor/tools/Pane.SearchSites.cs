using Pane.Contracts;
using Pane.Localization;
using Pane.Settings;

namespace Pane.Services.Launcher
{
    internal static class SearchSites
    {
        internal static SearchEngine? Parse(LauncherSettings settings, string raw, out string text)
        {
            var value = raw.TrimStart();
            var boundary = Array.FindIndex(value.ToCharArray(), char.IsWhiteSpace);
            var key = boundary < 0 ? value : value[..boundary];
            text = boundary < 0 ? "" : value[(boundary + 1)..].Trim();
            return settings.SearchEngines.FirstOrDefault(e => e.Keyword.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
        internal static SearchEngine[] Matches(LauncherSettings settings, string raw)
        {
            var query = raw.Trim();
            if (query.Length == 0 || query.Any(char.IsWhiteSpace)) return [];
            var exact = settings.SearchEngines.FirstOrDefault(e => e.Keyword.Equals(query, StringComparison.OrdinalIgnoreCase));
            return exact is not null ? [exact] : settings.SearchEngines.Where(e =>
                e.Keyword.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(6).ToArray();
        }
    }
}

namespace Pane.Views
{
    public sealed partial class LauncherView
    {
        static LauncherItem SeeinSiteChoice(SearchEngine engine) => new("site:" + engine.Keyword,
            LauncherItemKind.Command, LauncherGroups.Web, engine.Name,
            Strings.FormatName(Strings.Launcher_WebHint, engine.Name), new("globe"), Hotkey: "Enter");
        internal System.Text.Json.Nodes.JsonObject SeeinSiteState() => new()
        {
            ["query"] = Input.Text,
            ["engine"] = Pane.Services.Launcher.SearchSites.Parse(Options, Input.Text, out _)?.Keyword,
            ["sites"] = new System.Text.Json.Nodes.JsonArray(rows.Where(r => r.Item.Id.StartsWith("site:", StringComparison.Ordinal))
                .Select(r => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(r.Item.Title)).ToArray())
        };
        internal async Task SeeinChooseSite()
        {
            var index = rows.FindIndex(r => r.Item.Id.StartsWith("site:", StringComparison.Ordinal));
            if (index >= 0) await Execute(index, false, false);
        }
    }
}
