using System.Security.Cryptography;
using System.Text.Json;

namespace Seein.Core.Themes;

public sealed class ThemeProjectStore
{
    public string DraftRoot { get; }
    public string ProjectRoot { get; }
    public string ExportRoot { get; }
    public ThemeProjectStore(string userDataRoot)
    {
        DraftRoot = Path.Combine(userDataRoot, "ThemeEditor", "drafts");
        var layout = Path.Combine(userDataRoot, "storage-layout.json");
        string? cold = null;
        if (File.Exists(layout)) { using var json = JsonDocument.Parse(File.ReadAllText(layout)); cold = json.RootElement.GetProperty("ExportRoot").GetString(); }
        cold ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Seein");
        ProjectRoot = Path.Combine(cold, "主题库", "工程"); ExportRoot = Path.Combine(cold, "主题库", "导出");
    }
    public ThemeEditorDocument New(ThemeOrientation orientation = ThemeOrientation.PortraitClockwise)
    {
        var id = "custom-" + Guid.NewGuid().ToString("N"); var root = Path.Combine(DraftRoot, id); Directory.CreateDirectory(root);
        return new(new() { SchemaVersion = "1.1", Id = id, Name = "未命名主题", Orientation = orientation,
            Canvas = orientation == ThemeOrientation.Landscape ? new(1920,462) : new(462,1920) }, root);
    }
    public ThemeEditorDocument Open(string directory, bool copy)
    {
        var loaded = ThemePackage.LoadDirectory(directory); var document = New(loaded.Manifest.Orientation);
        CopyTree(directory, document.Root);
        document = new(loaded.Manifest with { Id = copy ? document.Manifest.Id : loaded.Manifest.Id, Name = loaded.Manifest.Name + (copy ? " · 副本" : ""), SchemaVersion = "1.1" }, document.Root);
        if (!copy) document.ProjectPath = directory;
        return document;
    }
    public string ImportAsset(ThemeEditorDocument document, string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".mp4" or ".webm" or ".ttf" or ".otf")) throw new InvalidDataException("不支持此素材格式。");
        if (new FileInfo(file).Length > 100L * 1024 * 1024) throw new InvalidDataException("单个素材不能超过 100 MB。");
        using var stream = File.OpenRead(file); var name = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() + ext;
        var destination = Path.Combine(document.Root, "assets", name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination)) File.Copy(file,destination);
        return "assets/" + name;
    }
    public static void WriteManifest(ThemeEditorDocument document)
    {
        Directory.CreateDirectory(document.Root);
        var path = Path.Combine(document.Root,"manifest.json"); var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(document.Manifest, JsonDefaults.Options)); File.Move(temporary,path,true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Save(ThemeEditorDocument document, byte[] preview)
    {
        WriteManifest(document); File.WriteAllBytes(Path.Combine(document.Root,"preview.png"),preview);
        var destination = document.ProjectPath ?? Path.Combine(ProjectRoot, document.Manifest.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var stage = destination + ".saving-" + Guid.NewGuid().ToString("N"); var previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyTree(document.Root, stage); _ = ThemePackage.LoadDirectory(stage);
            if (Directory.Exists(destination)) Directory.Move(destination,previous);
            Directory.Move(stage,destination); document.ProjectPath = destination; document.MarkSaved();
            if (Directory.Exists(previous)) Directory.Delete(previous,true);
        }
        catch { if (!Directory.Exists(destination) && Directory.Exists(previous)) Directory.Move(previous,destination); throw; }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage,true); }
    }
    public static void Export(ThemeEditorDocument document, string path)
    {
        var full=Path.GetFullPath(path);var source=Path.GetFullPath(document.Root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(full.StartsWith(source,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请将导出主题保存到草稿目录以外的位置。");
        WriteManifest(document); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
        try { ThemePackage.PackDirectory(document.Root,temporary); File.Move(temporary,path,true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public IEnumerable<string> Projects() => Directory.Exists(ProjectRoot) ? Directory.EnumerateDirectories(ProjectRoot).Where(d => File.Exists(Path.Combine(d,"manifest.json")) && !Path.GetFileName(d).Contains(".saving-") && !Path.GetFileName(d).Contains(".previous-")) : [];
    public IEnumerable<string> Drafts() => Directory.Exists(DraftRoot) ? Directory.EnumerateDirectories(DraftRoot).Where(d => File.Exists(Path.Combine(d,"manifest.json"))) : [];
    public void Remember(ThemeEditorDocument document)
    {
        WriteManifest(document);
        var path=Path.Combine(Path.GetDirectoryName(DraftRoot)!,"last-draft.json");var temporary=path+".tmp";
        try{File.WriteAllText(temporary,JsonSerializer.Serialize(new{Id=Path.GetFileName(document.Root),document.ProjectPath}));File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public ThemeEditorDocument? Resume()
    {
        try
        {
            var path=Path.Combine(Path.GetDirectoryName(DraftRoot)!,"last-draft.json");if(!File.Exists(path))return null;
            using var json=JsonDocument.Parse(File.ReadAllText(path));var id=json.RootElement.GetProperty("Id").GetString();
            if(string.IsNullOrEmpty(id)||id.IndexOfAny(new[]{'/', '\\', ':'})>=0||id is "." or "..")return null;
            var root=Path.Combine(DraftRoot,id);var loaded=ThemePackage.LoadDirectory(root);
            var project=json.RootElement.TryGetProperty("ProjectPath",out var item)&&item.ValueKind==JsonValueKind.String?item.GetString():null;
            return new(loaded.Manifest,root){ProjectPath=project};
        }
        catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException){return null;}
    }
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source,"*",new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (Path.GetExtension(file).ToLowerInvariant() is not (".json" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".mp4" or ".webm" or ".ttf" or ".otf")) continue;
            var to = Path.Combine(destination,Path.GetRelativePath(source,file)); Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(file,to,true);
        }
    }
}
