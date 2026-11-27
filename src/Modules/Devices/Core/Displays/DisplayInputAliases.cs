using System.Text.Json;

namespace Seein.Core.Displays;

/// <summary>Names are local UI metadata, keyed by physical monitor identity and unchanged VCP input value.</summary>
public sealed class DisplayInputAliases
{
    public const int MaximumLength = 64;
    private readonly string _path;
    private Dictionary<string, Dictionary<uint, string>> _monitors = new(StringComparer.Ordinal);
    public string? LoadError { get; }

    public DisplayInputAliases(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                _monitors = JsonSerializer.Deserialize<Dictionary<string, Dictionary<uint, string>>>(File.ReadAllText(path))
                    ?? throw new JsonException("昵称配置为空。");
                if (_monitors.Any(m => string.IsNullOrWhiteSpace(m.Key) || m.Value is null ||
                    m.Value.Any(p => p.Key is 0 or > 255 || string.IsNullOrWhiteSpace(p.Value) ||
                        p.Value.Length > MaximumLength || p.Value.Any(char.IsControl))))
                    throw new JsonException("昵称配置格式无效。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _monitors = new(StringComparer.Ordinal);
            LoadError = "本地昵称读取失败：" + ex.Message;
        }
    }

    public string GetAlias(string monitorId, uint input) =>
        _monitors.TryGetValue(monitorId, out var inputs) && inputs.TryGetValue(input, out var alias) ? alias : "";

    public string GetLabel(string monitorId, uint input)
    {
        var port = DisplayFeatures.OptionLabel(DisplayFeature.Input, input);
        var alias = GetAlias(monitorId, input);
        return alias.Length == 0 ? port : $"{alias}（{port}）";
    }

    public void SetAlias(string monitorId, uint input, string nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        if (input is 0 or > 255) throw new ArgumentOutOfRangeException(nameof(input));
        var alias = nickname.Trim();
        if (alias.Length > MaximumLength || alias.Any(char.IsControl))
            throw new ArgumentException($"昵称最多{MaximumLength}个字符，不能包含换行或控制字符。");
        if (LoadError is not null) throw new IOException(LoadError + " 原配置已保留，请先检查该文件。");
        var next = _monitors.ToDictionary(m => m.Key, m => new Dictionary<uint, string>(m.Value), StringComparer.Ordinal);
        if (!next.TryGetValue(monitorId, out var inputs)) next[monitorId] = inputs = [];
        if (alias.Length == 0) inputs.Remove(input); else inputs[input] = alias;
        if (inputs.Count == 0) next.Remove(monitorId);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, _path, true);
        _monitors = next;
    }
}
