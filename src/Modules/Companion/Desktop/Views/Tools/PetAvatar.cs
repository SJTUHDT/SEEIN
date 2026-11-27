using System.IO;
using System.Windows;
using Seein.Pet;
using SkiaSharp;

namespace Seein.Tray;

internal static class PetAvatar
{
    public static string? Find(PetConfiguration configuration, string assets, PetProfile pet)
    {
        var directory = Path.Combine(configuration.DirectoryPath, "pet");
        var custom = pet.Entry == "main" || !Directory.Exists(directory) ? null : Directory.EnumerateFiles(directory, pet.Entry + "-avatar.*")
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        var builtin = Path.Combine(assets, "logo.png");
        return custom ?? (File.Exists(builtin) ? builtin : null);
    }

    public static void Upload(PetConfiguration configuration, PetProfile pet, Window owner)
    {
        if (pet.Entry == "main") throw new InvalidOperationException("内置宠物使用内置头像。");
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif", CheckFileExists = true };
        if (picker.ShowDialog(owner) != true) return;
        if (new FileInfo(picker.FileName).Length > 20 * 1024 * 1024) throw new InvalidOperationException("头像文件不能超过 20 MiB。");
        var bytes = File.ReadAllBytes(picker.FileName);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("请选择有效的图片。");
        var extension = codec.EncodedFormat switch { SKEncodedImageFormat.Png => ".png", SKEncodedImageFormat.Jpeg => ".jpg", SKEncodedImageFormat.Gif => ".gif", SKEncodedImageFormat.Webp => ".webp", _ => throw new InvalidDataException("图片格式不支持。") };
        var directory = Path.Combine(configuration.DirectoryPath, "pet");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, pet.Entry + "-avatar" + extension);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
