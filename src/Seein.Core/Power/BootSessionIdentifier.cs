using System.Diagnostics.Eventing.Reader;

namespace Seein.Core.Power;

/// <summary>Identifies a Windows startup, including a Fast Startup resume.</summary>
public static class BootSessionIdentifier
{
    public static string GetCurrent()
    {
        try
        {
            const string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]";
            var eventQuery = new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true };
            using var reader = new EventLogReader(eventQuery);
            using var record = reader.ReadEvent();
            if (record?.RecordId is long recordId && record.TimeCreated is DateTime created)
                return $"kernel-boot-{recordId}-{created.ToUniversalTime():O}";
        }
        catch (EventLogException) { }
        catch (UnauthorizedAccessException) { }

        // The fallback is stable for the current kernel lifetime, so a service restart never
        // clears the session total. Kernel-Boot is normally available on supported Windows.
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return $"uptime-{boot:O}";
    }
}
