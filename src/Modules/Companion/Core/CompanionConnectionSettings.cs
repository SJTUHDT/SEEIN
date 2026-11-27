using System.Text.Json;

namespace Seein.Core;

public sealed record CompanionConnectionSettings(string WorkbenchRoot, string AstrBotAddress, string QqAddress)
{
    private static string FilePath => Path.Combine(AppPaths.UserDefault.DataRoot, "Companion", "connections.json");
    public static CompanionConnectionSettings Defaults => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dafeiyu"), "http://127.0.0.1:6185/", "http://127.0.0.1:5099/");
    public static CompanionConnectionSettings Load()
    {
        try { var value = JsonSerializer.Deserialize<CompanionConnectionSettings>(File.ReadAllText(FilePath)); if (value is not null) { value.Validate(); return value; } }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { }
        return Defaults;
    }
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(WorkbenchRoot)) throw new ArgumentException("工作台目录必须是完整路径。");
        foreach (var address in new[] { AstrBotAddress, QqAddress })
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host is not ("127.0.0.1" or "localhost") || uri.UserInfo.Length != 0)
                throw new ArgumentException("后台地址须为本机 HTTP 地址，不包含账号密码。");
        if (new Uri(AstrBotAddress).Port == new Uri(QqAddress).Port) throw new ArgumentException("两个后台端口不能相同。");
    }
    public void Save()
    {
        Validate(); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
