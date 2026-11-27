using System.Text.Json;

namespace Seein.Core.Themes;

public sealed class ThemeEditorDocument
{
    private readonly List<ThemeManifest> _undo = [], _redo = [];
    private ThemeManifest? _gesture;
    public ThemeManifest Manifest { get; private set; }
    public string Root { get; }
    public string? ProjectPath { get; set; }
    public bool Dirty { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action? Changed;
    public ThemeEditorDocument(ThemeManifest manifest, string root) { Manifest = Clone(manifest); Root = root; }
    public static ThemeManifest Clone(ThemeManifest value) => JsonSerializer.Deserialize<ThemeManifest>(JsonSerializer.Serialize(value, JsonDefaults.Options), JsonDefaults.Options)!;
    public void Update(Func<ThemeManifest, ThemeManifest> change, bool history = true)
    {
        var next = change(Clone(Manifest));
        if (JsonSerializer.Serialize(next, JsonDefaults.Options) == JsonSerializer.Serialize(Manifest, JsonDefaults.Options)) return;
        if (history && _gesture is null) { _undo.Add(Clone(Manifest)); if (_undo.Count > 100) _undo.RemoveAt(0); _redo.Clear(); }
        Manifest = next with { SchemaVersion = "1.1" }; Dirty = true; Changed?.Invoke();
    }
    public void BeginGesture() => _gesture ??= Clone(Manifest);
    public void EndGesture(bool cancel = false)
    {
        if (_gesture is null) return;
        var previous = _gesture; _gesture = null;
        if (cancel) Manifest = previous;
        else if (JsonSerializer.Serialize(previous, JsonDefaults.Options) != JsonSerializer.Serialize(Manifest, JsonDefaults.Options))
        { _undo.Add(previous); if (_undo.Count > 100) _undo.RemoveAt(0); _redo.Clear(); }
        Changed?.Invoke();
    }
    public void Undo() { if (!CanUndo) return; _redo.Add(Clone(Manifest)); Manifest = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); Dirty = true; Changed?.Invoke(); }
    public void Redo() { if (!CanRedo) return; _undo.Add(Clone(Manifest)); Manifest = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); Dirty = true; Changed?.Invoke(); }
    public void MarkSaved() { Dirty = false; }
    public void Edit(IEnumerable<string> ids, Func<ThemeLayer, ThemeLayer> change)
    { var selected = ids.ToHashSet(); Update(m => m with { Layers = m.Layers.Select(l => selected.Contains(l.Id) ? change(l) : l).ToList() }); }
    public static ThemeLayer Fit(ThemeLayer layer, ThemeCanvas canvas) => layer with
    {
        Width = Math.Clamp(layer.Width, 1, canvas.Width), Height = Math.Clamp(layer.Height, 1, canvas.Height),
        X = Math.Clamp(layer.X, 0, Math.Max(0, canvas.Width - Math.Clamp(layer.Width, 1, canvas.Width))),
        Y = Math.Clamp(layer.Y, 0, Math.Max(0, canvas.Height - Math.Clamp(layer.Height, 1, canvas.Height)))
    };
    public void Transform(IReadOnlyDictionary<string, ThemeLayer> originals, float dx, float dy, float scale = 1, float rotation = 0)
    {
        if (originals.Count == 0) return;
        var left = originals.Values.Min(l => l.X); var top = originals.Values.Min(l => l.Y);
        var right = originals.Values.Max(l => l.X + l.Width); var bottom = originals.Values.Max(l => l.Y + l.Height);
        scale = Math.Clamp(scale, .01f, Math.Min((Manifest.Canvas.Width - left) / Math.Max(1, right - left), (Manifest.Canvas.Height - top) / Math.Max(1, bottom - top)));
        dx = Math.Clamp(dx, -left, Manifest.Canvas.Width - right); dy = Math.Clamp(dy, -top, Manifest.Canvas.Height - bottom);
        var cx = (left + right) / 2; var cy = (top + bottom) / 2;
        var cos = MathF.Cos(rotation * MathF.PI / 180); var sin = MathF.Sin(rotation * MathF.PI / 180);
        Update(m => m with { Layers = m.Layers.Select(l =>
        {
            if (!originals.TryGetValue(l.Id, out var p)) return l;
            var x = p.X + p.Width / 2 - cx; var y = p.Y + p.Height / 2 - cy;
            return Fit(p with { X = rotation == 0 ? left + (p.X - left) * scale + dx : cx + x * cos - y * sin - p.Width / 2,
                Y = rotation == 0 ? top + (p.Y - top) * scale + dy : cy + x * sin + y * cos - p.Height / 2,
                Width = p.Width * scale, Height = p.Height * scale, Rotation = (p.Rotation + rotation) % 360 }, m.Canvas);
        }).ToList() }, false);
    }
    public void Align(IEnumerable<string> ids, string operation)
    {
        var set = ids.ToHashSet(); var layers = Manifest.Layers.Where(l => set.Contains(l.Id) && !l.Locked).ToArray(); if (layers.Length == 0) return;
        var left = layers.Min(l => l.X); var top = layers.Min(l => l.Y); var right = layers.Max(l => l.X + l.Width); var bottom = layers.Max(l => l.Y + l.Height);
        if (layers.Length == 1) { left = top = 0; right = Manifest.Canvas.Width; bottom = Manifest.Canvas.Height; }
        var horizontal = layers.OrderBy(l => l.X).ToArray(); var vertical = layers.OrderBy(l => l.Y).ToArray();
        Edit(layers.Select(l => l.Id), l => operation switch
        {
            "left" => l with { X = left }, "center" => l with { X = (left + right - l.Width) / 2 }, "right" => l with { X = right - l.Width },
            "top" => l with { Y = top }, "middle" => l with { Y = (top + bottom - l.Height) / 2 }, "bottom" => l with { Y = bottom - l.Height },
            "distributeX" when layers.Length > 2 => l with { X = left + horizontal.Take(Array.FindIndex(horizontal,p=>p.Id==l.Id)).Sum(p => p.Width) + Array.FindIndex(horizontal,p=>p.Id==l.Id) * (right - left - layers.Sum(p => p.Width)) / (layers.Length - 1) },
            "distributeY" when layers.Length > 2 => l with { Y = top + vertical.Take(Array.FindIndex(vertical,p=>p.Id==l.Id)).Sum(p => p.Height) + Array.FindIndex(vertical,p=>p.Id==l.Id) * (bottom - top - layers.Sum(p => p.Height)) / (layers.Length - 1) }, _ => l
        });
    }
    public static Dictionary<string, JsonElement> Style(ThemeLayer layer, string key, object value)
        => new(layer.Style, StringComparer.OrdinalIgnoreCase) { [key] = JsonSerializer.SerializeToElement(value) };
}
