using System.Text.Json.Nodes;

namespace Seein.Pet;

public sealed class PetMemory(string directory)
{
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(directory, "memory.json");

    public JsonArray Read(PetProfile pet)
    {
        lock (_sync)
        {
            var root = PetConfiguration.ReadObject(_path);
            var bucket = pet.Entry;
            return root[bucket]?[pet.Id]?["messages"]?.DeepClone() as JsonArray ?? [];
        }
    }

    public void Append(PetProfile pet, JsonObject user, JsonObject assistant, CancellationToken token = default)
    {
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            var root = PetConfiguration.ReadObject(_path);
            var bucket = pet.Entry;
            if (root[bucket] is not JsonObject) root[bucket] = new JsonObject();
            if (root[bucket]![pet.Id] is not JsonObject) root[bucket]![pet.Id] = new JsonObject();
            var record = (JsonObject)root[bucket]![pet.Id]!;
            if (record["messages"] is not JsonArray) record["messages"] = new JsonArray();
            var messages = (JsonArray)record["messages"]!;
            messages.Add(user.DeepClone());
            messages.Add(assistant.DeepClone());
            PetConfiguration.WriteObject(_path, root);
        }
    }

    public void Clear(PetProfile pet, IReadOnlySet<long>? timestamps = null)
    {
        lock (_sync)
        {
            var root = PetConfiguration.ReadObject(_path);
            var bucket = pet.Entry;
            if (root[bucket]?[pet.Id] is not JsonObject record || record["messages"] is not JsonArray messages) return;
            for (var index = messages.Count - 1; index >= 0; index--)
                if (timestamps is null || timestamps.Contains((long)(messages[index]?.Number("ts") ?? 0))) messages.RemoveAt(index);
            PetConfiguration.WriteObject(_path, root);
        }
    }
}
