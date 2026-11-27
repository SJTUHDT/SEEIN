using Seein.Core.Tools;
using System.IO.Compression;

namespace Seein.Tests;

public sealed class StorageFileArchiverTests
{
    [Fact]
    public void OnlyVerifiedOldClosedFilesLeaveSsd()
    {
        var root = Path.Combine(Path.GetTempPath(), "SeeinArchiveTest-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "ssd"); var destination = Path.Combine(root, "hdd");
        Directory.CreateDirectory(source);
        var now = DateTime.UtcNow;
        var old = Path.Combine(source, "old.log"); var recent = Path.Combine(source, "recent.log");
        var database = Path.Combine(source, "statistics.db"); var locked = Path.Combine(source, "active.log");
        foreach (var path in new[] { old, recent, database, locked }) File.WriteAllText(path, "preserved data " + Path.GetFileName(path));
        foreach (var path in new[] { old, database, locked }) File.SetLastWriteTimeUtc(path, now.AddDays(-31));
        try
        {
            using (var active = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var result = StorageFileArchiver.Run([new("日志", source)], destination, now.AddDays(-30));
                Assert.Equal(1, result.ArchivedFiles); Assert.False(File.Exists(old));
                Assert.True(File.Exists(recent)); Assert.True(File.Exists(database)); Assert.True(File.Exists(locked));
                using var zip = ZipFile.OpenRead(Assert.Single(result.Packages));
                using var text = new StreamReader(zip.GetEntry("files/old.log")!.Open());
                Assert.Equal("preserved data old.log", text.ReadToEnd());
                Assert.NotNull(zip.GetEntry("manifest.json"));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NoOldFilesDoesNotTouchArchiveDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "SeeinArchiveIdle-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "ssd"); var destination = Path.Combine(root, "hdd");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "current.log"), "recent");
        try
        {
            var result = StorageFileArchiver.Run([new("日志", source)], destination, DateTime.UtcNow.AddDays(-30));
            Assert.Equal(0, result.ArchivedFiles); Assert.False(Directory.Exists(destination));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnavailableArchiveDestinationKeepsSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "SeeinArchiveFailure-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "ssd"); var destination = Path.Combine(root, "hdd");
        Directory.CreateDirectory(source); var file = Path.Combine(source, "old.log");
        File.WriteAllText(file, "retain me"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-31));
        File.WriteAllText(destination, "not a directory");
        try
        {
            Assert.ThrowsAny<IOException>(() => StorageFileArchiver.Run([new("日志", source)], destination, DateTime.UtcNow.AddDays(-30)));
            Assert.Equal("retain me", File.ReadAllText(file));
        }
        finally { Directory.Delete(root, true); }
    }
}
