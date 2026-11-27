namespace Seein.Core.Sensors;

/// <summary>Unavailable hardware readings are null, never JSON-breaking NaN or infinity.</summary>
public static class SensorSnapshotSanitizer
{
    public static SensorSnapshot Normalize(SensorSnapshot snapshot, out string[] invalidKeys)
    {
        invalidKeys = snapshot.Values.Where(pair => pair.Value is double value && !double.IsFinite(value)).Select(pair => pair.Key).ToArray();
        if (invalidKeys.Length == 0) return snapshot;
        var values = new Dictionary<string, double?>(snapshot.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var key in invalidKeys) values[key] = null;
        return snapshot with { Values = values };
    }
}
