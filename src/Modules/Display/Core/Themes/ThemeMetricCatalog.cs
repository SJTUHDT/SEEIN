namespace Seein.Core.Themes;

public sealed record ThemeMetric(string Key, string Name, string Unit, double Maximum = 100, string Format = "0", string Category = "系统")
{
    public override string ToString() => Category + " · " + Name;
}
public static class ThemeMetricCatalog
{
    public static IReadOnlyList<ThemeMetric> All { get; } = new ThemeMetric[]
    {
        new(SensorKeys.CpuTemperature,"CPU 温度","°C",100,"0","CPU"),new(SensorKeys.CpuLoad,"CPU 占用","%",100,"0","CPU"),
        new(SensorKeys.CpuClock,"CPU 频率","MHz",6000,"0","CPU"),new(SensorKeys.CpuVoltage,"CPU 电压","V",2,"0.00","CPU"),new(SensorKeys.CpuPower,"CPU 功率","W",250,"0","CPU"),
        new(SensorKeys.GpuTemperature,"GPU 温度","°C",100,"0","GPU"),new(SensorKeys.GpuLoad,"GPU 占用","%",100,"0","GPU"),new(SensorKeys.GpuClock,"GPU 频率","MHz",4000,"0","GPU"),
        new(SensorKeys.GpuVoltage,"GPU 电压","V",2,"0.00","GPU"),new(SensorKeys.GpuPower,"GPU 功率","W",500,"0","GPU"),new(SensorKeys.GpuMemoryUsed,"显存使用","MB",32768,"0","GPU"),
        new(SensorKeys.MemoryLoad,"内存占用","%"),new(SensorKeys.StorageTemp,"存储温度","°C"),new(SensorKeys.SystemDriveLoad,"系统盘占用","%"),new(SensorKeys.MotherboardTemp,"主板温度","°C"),
        new(SensorKeys.GpuFan,"显卡风扇","RPM",4000,"0","散热"),new(SensorKeys.FanRadiator,"冷排风扇","RPM",3000,"0","散热"),new(SensorKeys.FanPump,"水泵","RPM",5000,"0","散热"),new(SensorKeys.FanCase,"机箱风扇","RPM",3000,"0","散热"),
        new(SensorKeys.NetworkUp,"上传速度","",125000000,"rate","网络"),new(SensorKeys.NetworkDown,"下载速度","",125000000,"rate","网络"),
        new(SensorKeys.DiskRead,"磁盘读取","",2000000000,"rate","存储"),new(SensorKeys.DiskWrite,"磁盘写入","",2000000000,"rate","存储"),
        new(SensorKeys.SystemVolume,"系统音量","%"),new(SensorKeys.Uptime,"开机时长","",86400,"duration"),new(SensorKeys.Fps,"游戏帧率","FPS",240),
        new(SensorKeys.EstimatedSystemPower,"整机估算功率","W",1000,"0","用电"),new(SensorKeys.SessionEnergy,"本次用电","Wh",10000,"0.0","用电"),new(SensorKeys.DailyEnergy,"今日用电","Wh",10000,"0.0","用电"),
        new(SensorKeys.MonthlyEnergy,"本月用电","Wh",100000,"0.0","用电"),new(SensorKeys.TotalEnergy,"累计用电","Wh",1000000,"0.0","用电")
    };
    public static IEnumerable<ThemeMetric> ForSnapshot(SensorSnapshot snapshot) => All.Concat(snapshot.Values.Keys.Except(All.Select(m => m.Key)).Select(k => new ThemeMetric(k,k,k.EndsWith(".rpm") ? "RPM" : "",k.EndsWith(".rpm") ? 5000 : 100,Category:"其他通道")));
    public static SensorSnapshot Demo(DateTimeOffset at) => new(at, All.ToDictionary(m => m.Key, m => (double?)(m.Maximum * (.45 + .15 * Math.Sin(at.ToUnixTimeSeconds() / 8d + m.Key.Length)))));
    public static ThemeLayer Create(string type, ThemeMetric? metric = null)
    {
        metric ??= All[0];
        var style = new Dictionary<string, System.Text.Json.JsonElement>();
        void Put(string k, object v) => style[k] = System.Text.Json.JsonSerializer.SerializeToElement(v);
        Put("color", "#60B4FF"); Put("trackColor", "#303B50"); Put("fontFamily", "Microsoft YaHei UI"); Put("fontSize", 28); Put("align", "center"); Put("radius", 12); Put("min", 0); Put("max", metric.Maximum); Put("unit", metric.Unit); Put("showValue",1);
        var bound = type is "metric" or "bar" or "ring" or "gauge" or "sparkline";
        var round = type is "ring" or "gauge" or "circle";
        return new() { Id = "element-" + Guid.NewGuid().ToString("N"), Name = bound ? metric.Name : type == "text" ? "文字" : type == "clock" ? "日期时间" : type,
            Type = type, X = 24, Y = 24, Width = round ? 180 : 280, Height = round ? 180 : type == "sparkline" ? 120 : type == "line" ? 4 : 60,
            Binding = bound ? metric.Key : null, Format = bound ? metric.Format : type == "text" ? "自定义文字" : type == "clock" ? "HH:mm:ss" : null, Style = style };
    }
}
