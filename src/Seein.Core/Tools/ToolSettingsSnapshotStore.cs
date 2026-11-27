using System.Text.Json;

namespace Seein.Core.Tools;

/// <summary>Disposable UI snapshots on the SSD; the tool remains the source of truth.</summary>
public sealed class ToolSettingsSnapshotStore(string root)
{
    private readonly object _gate = new();
    private sealed record Snapshot(int Version, string Engine, ToolField[] Fields);
    private string FileFor(string module) => module is "snow" or "pane"
        ? Path.Combine(root, module, "settings-ui-cache.json") : throw new ArgumentException("Unknown tool.");
    public IReadOnlyList<ToolField>? Load(string module, string engine)
    {
        lock (_gate)
        {
            try
            {
                var file = FileFor(module);
                if (!File.Exists(file) || new FileInfo(file).Length > 2 * 1024 * 1024) return null;
                var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file), ToolCatalog.Json);
                return snapshot is { Version: 1 } && snapshot.Engine == engine ? snapshot.Fields : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }
    public void Save(string module, string engine, IReadOnlyList<ToolField> fields)
    {
        lock (_gate)
        {
            var file = FileFor(module);
            var snapshot = new Snapshot(1, engine, fields.Select(f => f with { Value = f.Kind == "secret" ? null : f.Value?.DeepClone() }).ToArray());
            var json = JsonSerializer.Serialize(snapshot, ToolCatalog.Json);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file) && File.ReadAllText(file) == json) return;
            File.WriteAllText(file + ".tmp", json);
            File.Move(file + ".tmp", file, true);
        }
    }
}
