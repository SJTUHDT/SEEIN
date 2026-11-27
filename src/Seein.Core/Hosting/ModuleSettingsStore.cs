using System.Text.Json;
namespace Seein.Core.Hosting;
public sealed class ModuleSettingsStore
{
    private readonly object _gate=new();
    private readonly string _path;
    private PanelSettings _current;
    public ModuleSettingsStore(string path)
    {
        _path=path;
        _current=File.Exists(path)?JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(path),JsonDefaults.Options)??new():new();
    }
    public PanelSettings Current=>Volatile.Read(ref _current);
    public event Action<PanelSettings>? Changed;
    public void Update(Func<PanelSettings,PanelSettings> update)
    {
        lock(_gate)
        {
            var candidate=update(_current);Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {File.WriteAllBytes(temporary,JsonSerializer.SerializeToUtf8Bytes(candidate,JsonDefaults.Options));File.Move(temporary,_path,true);Volatile.Write(ref _current,candidate);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            Changed?.Invoke(candidate);
        }
    }
}
