using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Seein.Pet;

namespace Seein.PetHost;

internal sealed class PetPictureWindow : Window
{
    public PetPictureWindow(string path, PetWindow pet)
    {
        NativeTheme.Configure(this);
        Title = "配图";
        Owner = pet;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 300;
        Height = 330;
        Content = new PetImage(path) { Margin = new Thickness(12) };
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(pet.Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(pet.Top - Height, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    public static IEnumerable<string> Directories(PetEngine engine, PetProfile pet)
    {
        var custom = Path.Combine(engine.Configuration.DirectoryPath, "pet", pet.Entry + "-memes");
        if (pet.Entry != "main" && Directory.Exists(custom)) return [custom];
        return [Path.Combine(engine.Configuration.DirectoryPath, "memes"), Path.Combine(engine.Assets, "memes")];
    }

    public static IEnumerable<string> Names(PetEngine engine, PetProfile pet) => Directories(engine, pet).Where(Directory.Exists)
        .SelectMany(directory => Directory.EnumerateFiles(directory)).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
        .Select(path => Path.GetFileNameWithoutExtension(path)).Distinct(StringComparer.OrdinalIgnoreCase);

    public static string? Find(PetEngine engine, PetProfile pet, string name)
    {
        if (Path.GetFileName(name) != name || name is "." or "..") return null;
        return Directories(engine, pet).Where(Directory.Exists).SelectMany(directory => Directory.EnumerateFiles(directory))
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp");
    }
}
