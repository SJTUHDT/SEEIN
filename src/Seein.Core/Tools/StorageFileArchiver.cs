using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Seein.Core.Tools;

public sealed record StorageArchiveSource(string Category, string Directory);
public sealed record StorageArchiveResult(int ArchivedFiles, long ArchivedBytes, int SkippedFiles, string[] Packages);

public static class StorageFileArchiver
{
    private sealed record Entry(string Source, string Name, long Length, DateTime ModifiedUtc, string Sha256);

    public static StorageArchiveResult Run(IEnumerable<StorageArchiveSource> sources, string archiveRoot,
        DateTime cutoffUtc, CancellationToken cancellation = default)
    {
        var count = 0; long bytes = 0; var skipped = 0; var packages = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(source.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Directory.Exists(root)) continue;
            var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(path => seen.Add(path) && IsCandidate(path, cutoffUtc)).ToArray();
            // No old files: do not inspect or touch the mechanical disk at all.
            if (files.Length == 0) continue;
            if (source.Category.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("归档分类无效。");
            var destination = Path.Combine(archiveRoot, source.Category);
            Directory.CreateDirectory(destination);
            var package = Path.Combine(destination, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            var pending = package + ".pending";
            var entries = new List<Entry>();
            try
            {
                using (var zip = ZipFile.Open(pending, ZipArchiveMode.Create))
                {
                    foreach (var path in files)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var fullPath = Path.GetFullPath(path);
                        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("归档源超出允许目录。");
                        FileStream input;
                        try { input = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.None); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
                        using (input)
                        {
                            var modified = File.GetLastWriteTimeUtc(fullPath);
                            if (modified >= cutoffUtc) { skipped++; continue; }
                            var hash = HashStream(input, cancellation); input.Position = 0;
                            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                            var name = "files/" + relative;
                            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                            using (var output = entry.Open()) CopyStream(input, output, cancellation);
                            entries.Add(new(fullPath, name, input.Length, modified, hash));
                        }
                    }
                    var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
                    using var manifestStream = manifest.Open();
                    JsonSerializer.Serialize(manifestStream, new { Source = root, CutoffUtc = cutoffUtc, Files = entries });
                }
                if (entries.Count == 0) continue;
                // Read every archived byte back before removing any source file.
                using (var verified = ZipFile.OpenRead(pending))
                    foreach (var entry in entries)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var member = verified.GetEntry(entry.Name) ?? throw new IOException("归档条目缺失。");
                        using var input = member.Open();
                        if (member.Length != entry.Length || HashStream(input, cancellation) != entry.Sha256)
                            throw new IOException("归档校验失败，原文件已保留。");
                    }
                File.Move(pending, package); packages.Add(package);
                foreach (var entry in entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        // Re-lock and re-hash: a file reopened or changed since packaging stays on SSD.
                        using var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Delete);
                        if (input.Length != entry.Length || File.GetLastWriteTimeUtc(entry.Source) != entry.ModifiedUtc ||
                            HashStream(input, cancellation) != entry.Sha256) { skipped++; continue; }
                        File.Delete(entry.Source); count++; bytes += entry.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        return new(count, bytes, skipped, packages.ToArray());
    }

    private static string HashStream(Stream input, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer)) != 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void CopyStream(Stream input, Stream output, CancellationToken cancellation)
    {
        var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer)) != 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
    }

    private static bool IsCandidate(string path, DateTime cutoffUtc)
    {
        var name = Path.GetFileName(path);
        // Database contents and application state are never moved by file-age rules.
        if (name.Contains(".db", StringComparison.OrdinalIgnoreCase) || name.Contains(".sqlite", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) || name.Equals("LOCK", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cookies", StringComparison.OrdinalIgnoreCase) || name.Equals("Login Data", StringComparison.OrdinalIgnoreCase)) return false;
        try { return File.GetLastWriteTimeUtc(path) < cutoffUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
