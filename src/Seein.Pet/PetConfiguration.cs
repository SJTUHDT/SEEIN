using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seein.Pet;

public sealed record PetProfile(string Id, string Entry, string Name, double Size, string Display,
    JsonObject Settings, JsonObject EntrySettings, string AssetDirectory);

public sealed class PetConfiguration
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private readonly object _sync = new();
    private readonly string _assets;
    public string DirectoryPath { get; }
    public string MainPath => File.Exists(Path.Combine(DirectoryPath, "main-config.jsonc"))
        ? Path.Combine(DirectoryPath, "main-config.jsonc")
        : File.Exists(Path.Combine(DirectoryPath, "main-config.json"))
            ? Path.Combine(DirectoryPath, "main-config.json") : Path.Combine(DirectoryPath, "main-config.jsonc");

    public PetConfiguration(string home, string assets)
    {
        DirectoryPath = Path.Combine(Path.GetFullPath(home), "dsh-pet");
        _assets = Path.GetFullPath(assets);
    }

    public static JsonObject ReadObject(string path) => File.Exists(path)
        ? JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions) as JsonObject
            ?? throw new InvalidDataException("配置根节点必须是对象。")
        : new JsonObject();

    public static void WriteObject(string path, JsonObject document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                document.WriteTo(writer);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public JsonObject ReadEntries()
    {
        lock (_sync)
        {
            var defaults = ReadObject(Path.Combine(_assets, "config.jsonc"));
            if (defaults.Count == 0) throw new InvalidDataException("桌宠默认资源缺失。");
            var main = ReadObject(MainPath);
            var entries = new JsonObject { ["main"] = Overlay(defaults, main) };
            var custom = Path.Combine(DirectoryPath, "pet");
            if (System.IO.Directory.Exists(custom))
            {
                foreach (var path in System.IO.Directory.EnumerateFiles(custom).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(path);
                    var suffix = name.EndsWith("-config.jsonc", StringComparison.OrdinalIgnoreCase) ? "-config.jsonc" : "-config.json";
                    if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    var key = name[..^suffix.Length];
                    ValidateId(key);
                    if (entries.ContainsKey(key)) throw new InvalidDataException("宠物种类名称重复。");
                    var inherited = (JsonObject)defaults.DeepClone();
                    foreach (var field in new[] { "physics", "animationWeights", "font", "confineToScreen", "clickReactionThreshold", "voiceEnabled", "voiceName", "voiceVolume", "voiceWhisperEnabled", "voiceTodoEnabled", "voiceNotifyEnabled", "speechProvider", "whisperImageEnabled", "chatImageEnabled", "chatAnimationEnabled", "chatImageLimit", "chatMemoryRounds", "whisperModel", "chatModel", "eventsRefreshSec" })
                        if (main.TryGetPropertyValue(field, out var value)) inherited[field] = value?.DeepClone();
                    entries[key] = Overlay(inherited, ReadObject(path));
                }
            }
            var template = (defaults["pets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? new JsonObject();
            foreach (var entry in entries)
            {
                if (entry.Value?["pets"] is not JsonArray pets) continue;
                for (var index = 0; index < pets.Count; index++)
                    if (pets[index] is JsonObject pet)
                    {
                        var merged = Overlay(template, pet);
                        if (string.IsNullOrWhiteSpace(pet.Text("name"))) merged["name"] = pet.Text("id");
                        if (template["position"] is JsonObject position && pet["position"] is JsonObject overlay) merged["position"] = Overlay(position, overlay);
                        pets[index] = merged;
                    }
            }
            return entries;
        }
    }

    public IReadOnlyList<PetProfile> ReadPets()
    {
        var result = new List<PetProfile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ReadEntries())
        {
            if (entry.Value is not JsonObject settings || settings["pets"] is not JsonArray pets) continue;
            foreach (var item in pets.OfType<JsonObject>())
            {
                var id = item.Text("id");
                ValidateId(id);
                if (!ids.Add(id)) throw new InvalidDataException("宠物 ID 重复。");
                var display = item.Text("display", "both");
                if (display is not ("none" or "web" or "desktop" or "both")) throw new InvalidDataException("宠物显示方式无效。");
                result.Add(new(id, entry.Key, item.Text("name", id), Math.Clamp(item.Number("size", 462), 32, 4096), display,
                    (JsonObject)item.DeepClone(), (JsonObject)settings.DeepClone(), entry.Key == "main" ? Path.Combine(_assets, "webm") : Path.Combine(DirectoryPath, "pet", entry.Key + "-animation")));
            }
        }
        return result;
    }

    public void SaveMain(JsonObject patch)
    {
        lock (_sync)
        {
            var merged = Overlay(ReadObject(MainPath), patch);
            if (patch.ContainsKey("pets") && patch["pets"] is not JsonArray) throw new InvalidDataException("宠物列表必须是数组。");
            if (merged["pets"] is JsonArray pets)
            {
                if (pets.Count == 0) throw new InvalidDataException("至少保留一只宠物；可将它设为不显示。");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pet in pets)
                {
                    ValidatePet(pet);
                    var id = pet?.Text("id") ?? "";
                    ValidateId(id);
                    if (!ids.Add(id)) throw new InvalidDataException("宠物 ID 重复。");
                }
            }
            WriteObject(MainPath, merged);
        }
    }

    public void SaveEntry(string entry, JsonObject patch)
    {
        ValidateId(entry);
        if (entry == "main") { SaveMain(patch); return; }
        lock (_sync)
        {
            var path = FindEntryPath(entry);
            var raw = Overlay(ReadObject(path), patch);
            if (patch.ContainsKey("pets") && patch["pets"] is not JsonArray) throw new InvalidDataException("宠物列表必须是数组。");
            if (raw["pets"] is JsonArray pets)
            {
                if (pets.Count == 0) throw new InvalidDataException("至少保留一只宠物。");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pet in pets)
                {
                    ValidatePet(pet);
                    var id = pet?.Text("id") ?? "";
                    ValidateId(id);
                    if (!ids.Add(id)) throw new InvalidDataException("宠物 ID 重复。");
                }
            }
            WriteObject(path, raw);
        }
    }

    public void UpdatePet(string id, Action<JsonObject> update)
    {
        lock (_sync)
        {
            var pet = ReadPets().FirstOrDefault(p => p.Id == id) ?? throw new InvalidDataException("找不到宠物。");
            var path = pet.Entry == "main" ? MainPath : FindEntryPath(pet.Entry);
            var raw = ReadObject(path);
            if (raw["pets"] is not JsonArray)
                raw["pets"] = ReadEntries()[pet.Entry]!["pets"]!.DeepClone();
            var item = ((JsonArray)raw["pets"]!).OfType<JsonObject>().First(p => p.Text("id") == id);
            update(item);
            WriteObject(path, raw);
        }
    }

    public void PatchPet(string id, JsonObject patch)
    {
        ValidatePet(patch);
        if (patch.ContainsKey("id") && patch.Text("id") != id) throw new InvalidDataException("不能修改宠物 ID。");
        UpdatePet(id, pet => { foreach (var pair in patch) pet[pair.Key] = pair.Value?.DeepClone(); });
    }

    public void AddPet()
    {
        lock (_sync)
        {
            var pets = ReadEntries()["main"]!["pets"]!.AsArray();
            var pet = (JsonObject)pets[0]!.DeepClone();
            pet["id"] = "pet-" + Guid.NewGuid().ToString("N")[..12];
            pet["name"] = "新桌宠";
            pets.Add(pet);
            SaveMain(new JsonObject { ["pets"] = pets.DeepClone() });
        }
    }

    public void RemovePet(string id)
    {
        lock (_sync)
        {
            var pet = ReadPets().FirstOrDefault(p => p.Id == id) ?? throw new InvalidDataException("找不到宠物。");
            var pets = ReadEntries()[pet.Entry]!["pets"]!.AsArray();
            pets.Remove(pets.OfType<JsonObject>().First(p => p.Text("id") == id));
            SaveEntry(pet.Entry, new JsonObject { ["pets"] = pets.DeepClone() });
        }
    }

    private string FindEntryPath(string entry)
    {
        var root = Path.Combine(DirectoryPath, "pet");
        return new[] { ".jsonc", ".json" }.Select(ext => Path.Combine(root, entry + "-config" + ext)).First(File.Exists);
    }

    public static JsonObject Overlay(JsonObject original, JsonObject overlay)
    {
        var merged = (JsonObject)original.DeepClone();
        foreach (var item in overlay) merged[item.Key] = item.Value?.DeepClone();
        return merged;
    }

    public static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id is "." or ".." || id.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c)))
            throw new InvalidDataException("宠物 ID 无效。");
    }

    private static void ValidatePet(JsonNode? pet)
    {
        if (pet is not JsonObject item) throw new InvalidDataException("宠物条目必须是对象。");
        if (item.ContainsKey("display") && item.Text("display") is not ("none" or "web" or "desktop" or "both")) throw new InvalidDataException("宠物显示方式无效。");
        if (item.ContainsKey("size") && item.Number("size", -1) is <= 0 or > 4096) throw new InvalidDataException("宠物大小应为 1–4096。");
    }
}

public static class PetJson
{
    public static string Text(this JsonNode node, string key, string fallback = "") => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    public static double Number(this JsonNode node, string key, double fallback = 0)
    {
        if (node[key] is not JsonValue value) return fallback;
        if (value.TryGetValue<double>(out var number)) return double.IsFinite(number) ? number : fallback;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<long>(out var longInteger)) return longInteger;
        return fallback;
    }
    public static bool Flag(this JsonNode node, string key, bool fallback = false) => node[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
}
