using Seein.Core;
using Seein.Core.Tools;
using System.IO;
using System.Text.Json;

namespace Seein.Tray;

internal static class StartupStorageArchive
{
    private static readonly CancellationTokenSource Shutdown = new();
    private static Task? _running;
    public static Task RunAsync() => _running ??= Task.Run(() =>
    {
        if (AppPaths.InstanceSuffix.Length != 0) return;
        var user = AppPaths.UserDefault.DataRoot;
        var settings = Path.Combine(user, "storage-layout.json");
        if (!File.Exists(settings)) return;
        var status = Path.Combine(user, "storage-archive-status.json");
        try
        {
            using var configuration = JsonDocument.Parse(File.ReadAllText(settings));
            var root = configuration.RootElement;
            if (!root.TryGetProperty("ArchiveOnStartup", out var enabled) || !enabled.GetBoolean()) return;
            var exportRoot = root.GetProperty("ExportRoot").GetString()!;
            if (!Path.IsPathFullyQualified(exportRoot)) throw new IOException("归档根目录必须是完整路径。");
            var sources = new List<StorageArchiveSource>
            {
                new("后台日志", AppPaths.SystemDefault.Logs),
                new("截图录屏日志", Path.Combine(user, "Tools/snow/logs")),
                new("效率工具日志", Path.Combine(user, "Tools/pane/logs")),
                new("桌宠日志", Path.Combine(user, "PetHome/native-logs")),
                new("截图录屏缓存", Path.Combine(user, "Tools/snow/cache"))
            };
            // Only Chromium's rebuildable caches, never its profile/login/storage directories.
            foreach (var webRoot in new[] { "DafeiyuWebView" })
            {
                var directory = Path.Combine(user, webRoot);
                if (!Directory.Exists(directory)) continue;
                foreach (var cache in Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    if (Path.GetFileName(cache) is "Cache_Data" or "GPUCache" or "DawnCache" or "DawnGraphiteCache")
                        sources.Add(new("网页缓存", cache));
            }
            var result = StorageFileArchiver.Run(sources, Path.Combine(exportRoot, "归档", "30天前旧文件"), DateTime.UtcNow.AddDays(-30), Shutdown.Token);
            WriteStatus(new { FinishedUtc = DateTime.UtcNow, Success = true, Result = result });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { WriteStatus(new { FinishedUtc = DateTime.UtcNow, Success = false, Error = ex.Message }); }
        void WriteStatus(object value)
        {
            try { File.WriteAllText(status + ".tmp", JsonSerializer.Serialize(value)); File.Move(status + ".tmp", status, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    });
    public static void Stop()
    {
        Shutdown.Cancel();
        _running?.GetAwaiter().GetResult();
    }
}
