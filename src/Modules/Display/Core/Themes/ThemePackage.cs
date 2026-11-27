using System.IO.Compression;
using System.Text.Json;

namespace Seein.Core.Themes;

public static class ThemePackage
{
    private const long MaxArchiveBytes = 250L * 1024 * 1024;
    private const long MaxManifestBytes = 4L * 1024 * 1024;
    private const long MaxEntryBytes = 100L * 1024 * 1024;
    private const int MaxEntries = 512;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".json", ".png", ".jpg", ".jpeg", ".gif", ".mp4", ".webm", ".ttf", ".otf" };

    public static ThemeValidationResult Validate(string packagePath)
    {
        var errors = new List<string>();
        if (!File.Exists(packagePath)) return ThemeValidationResult.Invalid("主题包不存在");
        if (new FileInfo(packagePath).Length > MaxArchiveBytes) return ThemeValidationResult.Invalid("主题包超过 250 MB");

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count > MaxEntries) errors.Add("主题包文件数量超过限制");
            var manifests = archive.Entries.Where(e => Normalize(e.FullName) == "manifest.json").ToArray();
            if (manifests.Length == 0) return ThemeValidationResult.Invalid("缺少 manifest.json");
            if (manifests.Length != 1) return ThemeValidationResult.Invalid("主题包只能包含一个 manifest.json");
            var manifestEntry = manifests[0];
            if (manifestEntry.Length > MaxManifestBytes)
                return ThemeValidationResult.Invalid("manifest.json 超过 4 MB");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                var normalized = Normalize(entry.FullName);
                if (string.IsNullOrEmpty(normalized)) continue;
                if (!IsSafeRelativePath(normalized)) errors.Add($"非法路径：{entry.FullName}");
                if (!names.Add(normalized)) errors.Add($"重复路径：{entry.FullName}");
                if (entry.Length > MaxEntryBytes) errors.Add($"资源超过 100 MB：{entry.FullName}");
                if (!ThemeArchiveLimits.TryAddEntryLength(expandedBytes, entry.Length, out expandedBytes))
                {
                    errors.Add("主题包展开后超过 512 MB");
                    break;
                }
                if (!string.IsNullOrEmpty(Path.GetExtension(normalized)) && !AllowedExtensions.Contains(Path.GetExtension(normalized)))
                    errors.Add($"不允许的文件类型：{entry.FullName}");
            }
            if (errors.Count > 0) return new ThemeValidationResult(false, errors);

            ThemeManifest? manifest;
            using (var stream = manifestEntry.Open())
                manifest = JsonSerializer.Deserialize<ThemeManifest>(stream, JsonDefaults.Options);
            if (manifest is null) return ThemeValidationResult.Invalid("manifest.json 无法解析");
            ValidateManifest(manifest, names, errors);
            return new ThemeValidationResult(errors.Count == 0, errors, manifest);
        }
        catch (InvalidDataException ex) { return ThemeValidationResult.Invalid($"无效 ZIP：{ex.Message}"); }
        catch (JsonException ex) { return ThemeValidationResult.Invalid($"无效 manifest.json：{ex.Message}"); }
        catch (IOException ex) { return ThemeValidationResult.Invalid($"无法读取主题包：{ex.Message}"); }
    }

    public static LoadedTheme Import(string packagePath, string themeStoreRoot)
    {
        Directory.CreateDirectory(themeStoreRoot);
        var stablePackage = Path.Combine(themeStoreRoot, $".import.{Guid.NewGuid():N}.jptheme");
        string? temporary = null;
        string? finalRoot = null;
        string? backup = null;
        try
        {
            using (var source = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length > MaxArchiveBytes) throw new InvalidDataException("主题包超过 250 MB");
                using var copy = new FileStream(stablePackage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(copy);
            }
            var validation = Validate(stablePackage);
            if (!validation.IsValid || validation.Manifest is null)
                throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));

            var id = SanitizeId(validation.Manifest.Id);
            finalRoot = Path.GetFullPath(Path.Combine(themeStoreRoot, id));
            var store = Path.GetFullPath(themeStoreRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!finalRoot.StartsWith(store, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("主题 ID 非法");

            temporary = Path.Combine(themeStoreRoot, $".{id}.{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(temporary);
            using var archive = ZipFile.OpenRead(stablePackage);
            foreach (var entry in archive.Entries)
            {
                var normalized = Normalize(entry.FullName);
                if (string.IsNullOrEmpty(normalized)) continue;
                var destination = Path.GetFullPath(Path.Combine(temporary, normalized.Replace('/', Path.DirectorySeparatorChar)));
                var tempPrefix = Path.GetFullPath(temporary) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP 路径越界");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    Directory.CreateDirectory(destination);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, true);
                }
            }

            if (Directory.Exists(finalRoot))
            {
                backup = finalRoot + $".old.{Guid.NewGuid():N}";
                Directory.Move(finalRoot, backup);
            }
            Directory.Move(temporary, finalRoot);
            if (backup is not null && Directory.Exists(backup))
            {
                try { Directory.Delete(backup, true); } catch { }
            }
            return new LoadedTheme(validation.Manifest, finalRoot);
        }
        catch
        {
            if (temporary is not null && Directory.Exists(temporary))
            {
                try { Directory.Delete(temporary, true); } catch { }
            }
            if (backup is not null && finalRoot is not null && Directory.Exists(backup) && !Directory.Exists(finalRoot))
                Directory.Move(backup, finalRoot);
            throw;
        }
        finally
        {
            try { if (File.Exists(stablePackage)) File.Delete(stablePackage); } catch { }
        }
    }

    public static LoadedTheme LoadDirectory(string root)
    {
        var manifestPath = Path.Combine(root, "manifest.json");
        var manifest = JsonSerializer.Deserialize<ThemeManifest>(File.ReadAllText(manifestPath), JsonDefaults.Options)
            ?? throw new InvalidDataException("manifest.json 无法解析");
        var errors = new List<string>();
        ValidateManifest(manifest, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Normalize(Path.GetRelativePath(root, p))).ToHashSet(StringComparer.OrdinalIgnoreCase), errors);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return new LoadedTheme(manifest, Path.GetFullPath(root));
    }

    public static void PackDirectory(string sourceDirectory, string destinationPackage)
    {
        _ = LoadDirectory(sourceDirectory);
        if (File.Exists(destinationPackage)) File.Delete(destinationPackage);
        ZipFile.CreateFromDirectory(sourceDirectory, destinationPackage, CompressionLevel.Optimal, false);
        var result = Validate(destinationPackage);
        if (!result.IsValid) throw new InvalidDataException(string.Join(Environment.NewLine, result.Errors));
    }

    public static void DeleteImported(string themeStoreRoot, string themeId, string? activeThemeId, string? bundledThemeRoot = null)
    {
        if (string.IsNullOrWhiteSpace(themeId) || !string.Equals(SanitizeId(themeId), themeId, StringComparison.Ordinal))
            throw new InvalidDataException("主题 ID 非法");
        if (!string.IsNullOrWhiteSpace(bundledThemeRoot) && IsBundledThemeId(bundledThemeRoot, themeId))
            throw new InvalidOperationException("内置主题不可删除。");
        if (string.Equals(themeId, activeThemeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前正在使用此主题，请先切换到其他主题。");

        var store = Path.GetFullPath(themeStoreRoot);
        var target = Path.GetFullPath(Path.Combine(store, themeId));
        var prefix = store.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("主题 ID 非法");
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("找不到已导入的主题。");
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不允许删除重解析点目录。");
        var loaded = LoadDirectory(target);
        if (!string.Equals(loaded.Manifest.Id, themeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("主题目录与主题 ID 不匹配。");
        Directory.Delete(target, recursive: true);
    }

    private static bool IsBundledThemeId(string bundledThemeRoot, string themeId)
    {
        if (!Directory.Exists(bundledThemeRoot)) return false;
        foreach (var directory in Directory.EnumerateDirectories(bundledThemeRoot))
        {
            if (string.Equals(Path.GetFileName(directory), themeId, StringComparison.OrdinalIgnoreCase)) return true;
            var manifest = LoadDirectory(directory).Manifest;
            if (string.Equals(manifest.Id, themeId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void ValidateManifest(ThemeManifest manifest, HashSet<string> files, List<string> errors)
    {
        if (manifest.SchemaVersion is not ("1.0" or "1.1")) errors.Add($"不支持 schemaVersion {manifest.SchemaVersion}");
        var expected = manifest.Orientation == ThemeOrientation.Landscape ? new ThemeCanvas(1920, 462) : new ThemeCanvas(462, 1920);
        if (manifest.Canvas is null) errors.Add("画布定义不能为空");
        else if (manifest.Canvas != expected) errors.Add(manifest.Orientation == ThemeOrientation.Landscape ? "横屏画布必须为 1920×462" : "竖屏画布必须为 462×1920");
        if (manifest.FrameRate is < 1 or > 30) errors.Add("帧率必须在 1–30 之间");
        if (manifest.JpegQuality is < 50 or > 100) errors.Add("JPEG 质量必须在 50–100 之间");
        if (string.IsNullOrWhiteSpace(manifest.Id)) errors.Add("主题 ID 不能为空");
        else if (!string.Equals(SanitizeId(manifest.Id), manifest.Id, StringComparison.Ordinal)) errors.Add("主题 ID 只能包含字母、数字、连字符和下划线");
        if (manifest.Layers is null)
        {
            errors.Add("图层列表不能为空");
            return;
        }
        var layerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in manifest.Layers)
        {
            if (layer is null || string.IsNullOrWhiteSpace(layer.Id))
            {
                errors.Add("图层缺少有效 ID");
                continue;
            }
            if (!layerIds.Add(layer.Id)) errors.Add($"图层 ID 重复：{layer.Id}");
            if (!float.IsFinite(layer.Rotation) || !float.IsFinite(layer.Opacity) || layer.Opacity is < 0 or > 1)
                errors.Add($"图层旋转或透明度无效：{layer.Id}");
            if (layer.Style.TryGetValue("fontAsset", out var font) && font.ValueKind == JsonValueKind.String &&
                font.GetString() is { } fontPath && (!IsSafeRelativePath(Normalize(fontPath)) || !files.Contains(Normalize(fontPath))))
                errors.Add($"字体资源不存在或路径非法：{layer.Id}");
            if (!float.IsFinite(layer.X) || !float.IsFinite(layer.Y) || !float.IsFinite(layer.Width) || !float.IsFinite(layer.Height) ||
                layer.X < 0 || layer.Y < 0 || layer.Width < 0 || layer.Height < 0 ||
                manifest.Canvas is not null &&
                ((double)layer.X + layer.Width > manifest.Canvas.Width || (double)layer.Y + layer.Height > manifest.Canvas.Height))
                errors.Add($"图层越出画布：{layer.Id}");
            if (layer.Asset is not null && (!IsSafeRelativePath(Normalize(layer.Asset)) || !files.Contains(Normalize(layer.Asset))))
                errors.Add($"资源不存在或路径非法：{layer.Asset}");
        }
        if (manifest.Background is not null && (!IsSafeRelativePath(Normalize(manifest.Background)) || !files.Contains(Normalize(manifest.Background))))
            errors.Add($"背景资源不存在或路径非法：{manifest.Background}");
    }

    private static string SanitizeId(string id)
    {
        var value = string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
        return string.IsNullOrEmpty(value) ? "theme" : value;
    }

    private static string Normalize(string value) => value.Replace('\\', '/');
    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || Path.IsPathRooted(value) || value.Contains(':')) return false;
        var path = value.TrimEnd('/');
        return path.Length > 0 && path.Split('/').All(segment => segment is not ".." and not "." && segment.Length > 0);
    }
}

