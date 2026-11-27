using Seein.Core.Themes;
using System.IO.Compression;
using System.Text;

namespace Seein.Tests;

public sealed class ThemePackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SeeinTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidPackage_IsAccepted()
    {
        var package = CreatePackage((zip) => AddText(zip, "manifest.json", ValidManifest));
        var result = ThemePackage.Validate(package);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ZipSlip_IsRejected()
    {
        var package = CreatePackage(zip =>
        {
            AddText(zip, "manifest.json", ValidManifest);
            AddText(zip, "../outside.png", "bad");
        });
        var result = ThemePackage.Validate(package);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("非法路径"));
    }

    [Fact]
    public void MissingAsset_IsRejected()
    {
        var manifest = ValidManifest.Replace("\"layers\":[]", "\"layers\":[{\"id\":\"pic\",\"type\":\"image\",\"x\":0,\"y\":0,\"width\":10,\"height\":10,\"asset\":\"assets/missing.png\"}]");
        var package = CreatePackage(zip => AddText(zip, "manifest.json", manifest));
        Assert.False(ThemePackage.Validate(package).IsValid);
    }

    [Fact]
    public void UnknownSchema_IsRejected()
    {
        var package = CreatePackage(zip => AddText(zip, "manifest.json", ValidManifest.Replace("\"1.0\"", "\"9.0\"")));
        Assert.False(ThemePackage.Validate(package).IsValid);
    }

    [Fact]
    public void UnknownSensorBinding_IsSafeAndAccepted()
    {
        var manifest = ValidManifest.Replace("\"layers\":[]", "\"layers\":[{\"id\":\"metric\",\"type\":\"metric\",\"x\":0,\"y\":0,\"width\":100,\"height\":40,\"binding\":\"future.sensor.key\"}]");
        var package = CreatePackage(zip => AddText(zip, "manifest.json", manifest));
        Assert.True(ThemePackage.Validate(package).IsValid);
    }

    [Fact]
    public void DuplicateManifest_IsRejectedWithoutThrowing()
    {
        var package = CreatePackage(zip =>
        {
            AddText(zip, "manifest.json", ValidManifest);
            AddText(zip, "manifest.json", ValidManifest);
        });

        var result = ThemePackage.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("只能包含一个 manifest.json"));
    }

    [Theory]
    [InlineData("/manifest.json")]
    [InlineData("C:/outside.png")]
    public void RootedArchivePaths_AreRejected(string path)
    {
        var package = CreatePackage(zip =>
        {
            AddText(zip, "manifest.json", ValidManifest);
            AddText(zip, path, "bad");
        });

        Assert.False(ThemePackage.Validate(package).IsValid);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("1e30")]
    public void NonFiniteOrOverflowingLayerBounds_AreRejected(string x)
    {
        var manifest = ValidManifest.Replace("\"layers\":[]",
            $"\"layers\":[{{\"id\":\"layer\",\"type\":\"text\",\"x\":{x},\"y\":0,\"width\":100,\"height\":40}}]");
        var package = CreatePackage(zip => AddText(zip, "manifest.json", manifest));

        Assert.False(ThemePackage.Validate(package).IsValid);
    }

    [Fact]
    public void MissingLayers_AreRejected()
    {
        var manifest = ValidManifest.Replace("\"layers\":[]", "\"layers\":null");
        var package = CreatePackage(zip => AddText(zip, "manifest.json", manifest));

        var result = ThemePackage.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("图层列表不能为空"));
    }

    [Fact]
    public void OversizedManifest_IsRejectedBeforeDeserialization()
    {
        var manifest = "{\"payload\":\"" + new string('a', 4 * 1024 * 1024) + "\"}";
        var package = CreatePackage(zip => AddText(zip, "manifest.json", manifest));

        var result = ThemePackage.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("manifest.json 超过 4 MB"));
    }

    [Fact]
    public void DeleteImportedTheme_RejectsActiveThemeAndRemovesInactiveTheme()
    {
        var store = Path.Combine(_root, "themes");
        var package = CreatePackage(zip => AddText(zip, "manifest.json", ValidManifest));
        var imported = ThemePackage.Import(package, store);

        Assert.Throws<InvalidOperationException>(() => ThemePackage.DeleteImported(store, "test", "test"));
        Assert.True(Directory.Exists(imported.RootDirectory));

        ThemePackage.DeleteImported(store, "test", "other");
        Assert.False(Directory.Exists(imported.RootDirectory));
    }

    [Fact]
    public void DeleteImportedTheme_RejectsBundledThemeId()
    {
        var store = Path.Combine(_root, "user-themes");
        var bundled = Path.Combine(_root, "bundled-themes");
        var package = CreatePackage(zip => AddText(zip, "manifest.json", ValidManifest));
        var imported = ThemePackage.Import(package, store);
        ThemePackage.Import(package, bundled);

        Assert.Throws<InvalidOperationException>(() =>
            ThemePackage.DeleteImported(store, "test", "other", bundled));
        Assert.True(Directory.Exists(imported.RootDirectory));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/outside")]
    public void DeleteImportedTheme_RejectsUnsafeIds(string id)
    {
        Assert.Throws<InvalidDataException>(() => ThemePackage.DeleteImported(_root, id, "other"));
    }

    [Fact]
    public void InvalidReplacement_DoesNotRemoveExistingTheme()
    {
        var store = Path.Combine(_root, "themes");
        var existing = ThemePackage.Import(CreatePackage(zip => AddText(zip, "manifest.json", ValidManifest)), store);
        var invalidManifest = ValidManifest
            .Replace("\"layers\":[]", "\"layers\":[{\"id\":\"missing\",\"type\":\"image\",\"x\":0,\"y\":0,\"width\":1,\"height\":1,\"asset\":\"missing.png\"}]");
        var invalidPackage = CreatePackage(zip => AddText(zip, "manifest.json", invalidManifest));

        Assert.Throws<InvalidDataException>(() => ThemePackage.Import(invalidPackage, store));
        Assert.True(Directory.Exists(existing.RootDirectory));
        Assert.Equal("Test", ThemePackage.LoadDirectory(existing.RootDirectory).Manifest.Name);
    }

    [Fact]
    public void UnsafeThemeId_IsRejectedDuringValidation()
    {
        var manifest = ValidManifest.Replace("\"id\":\"test\"", "\"id\":\"../test\"");
        var package = CreatePackage(zip => AddText(zip, "manifest.json", manifest));

        var result = ThemePackage.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("主题 ID"));
    }

    private string CreatePackage(Action<ZipArchive> build)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid() + ".jptheme");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        build(zip);
        return path;
    }

    private static void AddText(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private const string ValidManifest = """
        {"schemaVersion":"1.0","id":"test","name":"Test","author":"Tests","canvas":{"width":1920,"height":462},"orientation":"landscape","frameRate":6,"jpegQuality":88,"backgroundColor":"#10131A","layers":[]}
        """;

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

