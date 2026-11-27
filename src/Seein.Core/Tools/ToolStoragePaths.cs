using System.Text.Json;

namespace Seein.Core.Tools;

public sealed record ToolStoragePaths(string Cache, string Temporary, string? Pdf)
{
    public static ToolStoragePaths Resolve(string userDataRoot)
    {
        var file = Path.Combine(userDataRoot, "storage-layout.json");
        string? root = null;
        if (File.Exists(file))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            root = document.RootElement.GetProperty("ExportRoot").GetString();
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new InvalidOperationException("保存根目录必须是完整路径。");
        }
        // ExportRoot may be a mechanical disk. Keep frequent and in-progress writes on the user-data SSD.
        var cache = Path.Combine(userDataRoot, "Tools", "snow", "cache");
        return new(cache, Path.Combine(cache, "临时"), root is null ? null : Path.Combine(root, "导出文件", "PDF"));
    }
}
