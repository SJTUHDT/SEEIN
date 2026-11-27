using System.Runtime.InteropServices;

namespace Seein.Core.Apps;

/// <summary>
/// Reads per-process GPU engine utilization from the PDH wildcard counter
/// <c>\GPU Engine(*)\Utilization Percentage</c>.  A persistent query keeps a sample at roughly
/// 0.6 ms even with 550 counter instances, which is why Get-Counter style ad hoc queries are avoided.
/// When a discrete adapter can be identified its engines are preferred; if the driver reports
/// different LUIDs every engine is summed instead of losing the sample.
/// </summary>
public sealed class GpuEngineSampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private IntPtr _query;
    private IntPtr _counter;
    private bool _ready;

    [StructLayout(LayoutKind.Sequential)]
    private struct FmtValueItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    public GpuEngineSampler()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
        if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0) return;
        PdhCollectQueryData(_query);
        Thread.Sleep(300);
        _ready = true;
    }

    public bool IsAvailable => _ready;

    /// <summary>Last sampling diagnostic (instance counts and sums).</summary>
    public string LastDiagnostic { get; private set; } = string.Empty;

    /// <summary>LUID of the discrete adapter used for filtering, null when only one GPU exists.</summary>
    public long? DiscreteAdapterLuid => DiscreteGpu.Luid;

    /// <summary>Returns pid to the sum of its engine utilizations in percent.</summary>
    public Dictionary<int, double> Sample()
    {
        var result = new Dictionary<int, double>();
        if (!_ready) return result;
        if (PdhCollectQueryData(_query) != 0) return result;

        uint size = 0, count = 0;
        PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, out count, IntPtr.Zero);
        if (size == 0) return result;

        var rawPositive = 0;
        var rawSum = 0d;
        var matched = 0;
        var matchedSum = 0d;
        string? firstInstance = null;
        var buffer = Marshal.AllocHGlobal((int)size);
        var fallback = false;
        try
        {
            if (PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, out count, buffer) != 0) return result;
            var itemSize = Marshal.SizeOf<FmtValueItem>();
            var luid = DiscreteGpu.Luid;
            var staged = new List<(int Pid, double Value, bool Match)>((int)count);
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<FmtValueItem>(buffer + index * itemSize);
                if (item.Name == IntPtr.Zero) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                if (name is null) continue;
                if (firstInstance is null) firstInstance = name;
                if (item.Value > 0)
                {
                    rawPositive++;
                    rawSum += item.Value;
                }

                var pidStart = name.IndexOf("pid_", StringComparison.Ordinal);
                if (pidStart < 0) continue;
                var pidEnd = name.IndexOf('_', pidStart + 4);
                if (pidEnd < 0) continue;
                if (!int.TryParse(name.AsSpan(pidStart + 4, pidEnd - pidStart - 4), out var pid)) continue;

                var isMatch = luid is not long expected || MatchesLuid(name, expected);
                if (isMatch && item.Value > 0)
                {
                    matched++;
                    matchedSum += item.Value;
                }
                staged.Add((pid, item.Value, isMatch));
            }

            fallback = luid is long && matched == 0 && rawPositive > 0;
            foreach (var entry in staged)
            {
                if (!fallback && !entry.Match) continue;
                if (entry.Value <= 0) continue;
                result[entry.Pid] = result.TryGetValue(entry.Pid, out var current) ? current + entry.Value : entry.Value;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        LastDiagnostic =
            $"count={count} positive={rawPositive} sumAll={rawSum:F1} matched={matched} sumMatched={matchedSum:F1} " +
            $"luid={(DiscreteGpu.Luid is long value ? value.ToString("X16") : "none")} fallback={fallback} sample={firstInstance}";
        return result;
    }

    /// <summary>PDH instance names embed the adapter LUID as luid_0xHIGH_0xLOW.</summary>
    private static bool MatchesLuid(string instance, long luid)
    {
        var high = (uint)(luid >> 32);
        var low = (uint)(luid & 0xFFFFFFFF);
        var expected = $"luid_0x{high:X8}_0x{low:X8}";
        return instance.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_counter != IntPtr.Zero) PdhRemoveCounter(_counter);
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _ready = false;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhRemoveCounter(IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
