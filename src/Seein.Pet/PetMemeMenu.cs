using System.Text.Json.Nodes;

namespace Seein.Pet;

public sealed record PetMemeMenuEntry(string Name, string Description);
public sealed record PetMemeMenuGroup(string Label, IReadOnlyList<PetMemeMenuEntry> Entries);

public static class PetMemeMenu
{
    // Mirrors dsh-pet 0.4.9 shared/menu/memes.ts; preserve the configured order and limit semantics.
    public static IReadOnlyList<PetMemeMenuGroup> Build(JsonObject settings, IReadOnlySet<string> available)
    {
        var entries = (settings["memes"] as JsonObject ?? new()).Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                && pair.Value is JsonValue value && value.TryGetValue<string>(out var description) && !string.IsNullOrWhiteSpace(description)
                && available.Contains(pair.Key))
            .Select(pair => new PetMemeMenuEntry(pair.Key, pair.Value!.GetValue<string>())).ToArray();
        var limit = Math.Floor(settings.Number("chatImageLimit", 0));
        var take = !settings.Flag("chatImageEnabled") ? 0 : limit <= 0 ? entries.Length : (int)Math.Min(limit, entries.Length);
        var output = new List<PetMemeMenuGroup>();
        if (take > 0) output.Add(new("纳入对话的配图", entries.Take(take).ToArray()));
        if (take < entries.Length) output.Add(new("其他", entries.Skip(take).ToArray()));
        return output;
    }
}
