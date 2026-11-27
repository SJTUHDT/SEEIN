using System.Diagnostics;
using System.Globalization;

namespace Seein.Core.Sensors;

public sealed class PresentMonFpsProvider : IAsyncDisposable
{
    private Process? _process;
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, (double Sum, int Count)> _samples = new(StringComparer.OrdinalIgnoreCase);
    private int _applicationColumn = -1;
    private int _frameTimeColumn = -1;
    private long _bucketSecond;
    private double? _fps;
    private DateTimeOffset _lastFpsAt;

    public PresentMonFpsProvider(string? explicitPath = null)
    {
        _path = explicitPath ?? FindPresentMon();
    }

    public bool IsAvailable => _path is not null;
    public double? CurrentFps { get { lock (_gate) return DateTimeOffset.Now - _lastFpsAt <= TimeSpan.FromSeconds(2) ? _fps : null; } }

    public void Start()
    {
        if (_path is null || _process is not null) return;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(_path, $"--output_stdout --no_console_stats --exclude_dropped --v1_metrics --exclude dwm.exe --exclude explorer.exe --exclude {ProcessNames.FileName("service")} --exclude {ProcessNames.FileName("main")}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, e) => ParseLine(e.Data);
        try { _process.Start(); _process.BeginOutputReadLine(); } catch { _process.Dispose(); _process = null; }
    }

    private void ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var columns = line.Split(',');
        if (line.StartsWith("Application", StringComparison.OrdinalIgnoreCase))
        {
            _applicationColumn = Array.FindIndex(columns, c => c.Trim('"').Equals("Application", StringComparison.OrdinalIgnoreCase));
            _frameTimeColumn = Array.FindIndex(columns, c => c.Trim('"').Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase));
            return;
        }
        if (_applicationColumn < 0 || _frameTimeColumn < 0 || columns.Length <= Math.Max(_applicationColumn, _frameTimeColumn)) return;
        if (!double.TryParse(columns[_frameTimeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var frameTimeMs) || frameTimeMs is < 1 or > 1000) return;
        var application = Path.GetFileName(columns[_applicationColumn].Trim('"'));
        if (string.IsNullOrWhiteSpace(application)) return;
        var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (_bucketSecond != 0 && second != _bucketSecond)
            {
                var best = _samples.OrderByDescending(p => p.Value.Count).FirstOrDefault();
                if (best.Value.Count > 0)
                {
                    _fps = best.Value.Sum / best.Value.Count;
                    _lastFpsAt = DateTimeOffset.Now;
                }
                _samples.Clear();
            }
            _bucketSecond = second;
            var current = _samples.GetValueOrDefault(application);
            _samples[application] = (current.Sum + 1000d / frameTimeMs, current.Count + 1);
        }
    }

    private static string? FindPresentMon()
    {
        var bundled = ProcessNames.Executable("fps");
        return File.Exists(bundled) ? bundled : null;
    }

    public ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false }) try { _process.Kill(true); } catch { }
        _process?.Dispose();
        return ValueTask.CompletedTask;
    }
}
