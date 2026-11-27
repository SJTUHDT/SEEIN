using Seein.Core.Themes;

if (args.Length < 2)
{
    Console.WriteLine("Seein.ThemeTool");
    Console.WriteLine("  validate <theme.jptheme>");
    Console.WriteLine("  pack <theme-directory> [output.jptheme]");
    return 2;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "validate":
            var result = ThemePackage.Validate(Path.GetFullPath(args[1]));
            if (!result.IsValid)
            {
                foreach (var error in result.Errors) Console.Error.WriteLine(error);
                return 1;
            }
            Console.WriteLine($"有效主题：{result.Manifest!.Name} ({result.Manifest.Id})");
            return 0;
        case "pack":
            var source = Path.GetFullPath(args[1]);
            var destination = args.Length >= 3 ? Path.GetFullPath(args[2]) : Path.Combine(Directory.GetParent(source)!.FullName, Path.GetFileName(source) + ".jptheme");
            ThemePackage.PackDirectory(source, destination);
            Console.WriteLine(destination);
            return 0;
        default:
            Console.Error.WriteLine("未知命令");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
