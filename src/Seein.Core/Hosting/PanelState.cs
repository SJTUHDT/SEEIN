using Seein.Core;

namespace Seein.Service;

/// <summary>
/// Latest sensor / power readings and settings, shared between the panel loop and the application
/// usage collector so the latter can attribute the measured CPU and GPU power to processes.
/// </summary>
public sealed class PanelState
{
    private readonly object _gate = new();
    private SensorSnapshot _sensors = SensorSnapshot.Empty;
    private PowerSnapshot? _power;
    private PanelSettings _settings = new();

    public void Publish(SensorSnapshot sensors, PowerSnapshot? power)
    {
        lock (_gate)
        {
            _sensors = sensors;
            _power = power;
        }
    }

    public void PublishSettings(PanelSettings settings)
    {
        lock (_gate) _settings = settings;
    }

    public (SensorSnapshot Sensors, PowerSnapshot? Power, PanelSettings Settings) Snapshot()
    {
        lock (_gate) return (_sensors, _power, _settings);
    }
}
