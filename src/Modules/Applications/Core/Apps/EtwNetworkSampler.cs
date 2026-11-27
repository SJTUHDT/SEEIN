using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Seein.Core.Apps;

/// <summary>
/// Per-process network counters from the Windows kernel network provider
/// (<c>Microsoft-Windows-Kernel-Network</c>).  Unlike the per-connection ESTATS API this does not
/// need each TCP connection to be enabled, so it also sees UDP traffic and connections that were
/// opened before the sampler started.  Requires an elevated process (the service runs as LocalSystem).
/// </summary>
public sealed class EtwNetworkSampler : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (long Up, long Down)> _pending = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _isRunning;
    private volatile bool _stopping;
    private string? _lastError;
    private int _generation;

    public bool IsRunning => _isRunning;

    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>Events observed since start (diagnostic).</summary>
    public long EventCount;

    public bool Start(string instanceSuffix)
    {
        // A fresh GUID name avoids collisions with sessions left behind by earlier runs,
        // which is what made the kernel session fail with ERROR_NO_SYSTEM_RESOURCES before.
        for (var attempt = 1; attempt <= 3 && !IsRunning; attempt++)
        {
            _stopping = false;
            var generation = Interlocked.Increment(ref _generation);
            Volatile.Write(ref _lastError, null);
            var name = $"SeeinNet-{instanceSuffix}-{Guid.NewGuid():N}";
            try
            {
                var session = new TraceEventSession(name) { StopOnDispose = true };
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                var kernel = session.Source.Kernel;
                kernel.TcpIpSend += data => Record(data.ProcessID, data.size, data.saddr, data.daddr, upload: true);
                kernel.TcpIpRecv += data => Record(data.ProcessID, data.size, data.saddr, data.daddr, upload: false);
                kernel.UdpIpSend += data => Record(data.ProcessID, data.size, data.saddr, data.daddr, upload: true);
                kernel.UdpIpRecv += data => Record(data.ProcessID, data.size, data.saddr, data.daddr, upload: false);
                kernel.TcpIpSendIPV6 += data => Record(data.ProcessID, data.size, data.saddr, data.daddr, upload: true);
                kernel.TcpIpRecvIPV6 += data => Record(data.ProcessID, data.size, data.saddr, data.daddr, upload: false);
                kernel.UdpIpSendIPV6 += data => Record(data.ProcessID, data.size, data.saddr, data.daddr, upload: true);
                kernel.UdpIpRecvIPV6 += data => Record(data.ProcessID, data.size, data.saddr, data.daddr, upload: false);

                _session = session;
                _thread = new Thread(() =>
                {
                    if (_stopping || generation != Volatile.Read(ref _generation)) return;
                    _isRunning = true;
                    try
                    {
                        session.Source.Process();
                        if (!_stopping && generation == Volatile.Read(ref _generation))
                            Volatile.Write(ref _lastError, "ETW event processing stopped unexpectedly.");
                    }
                    catch (Exception ex)
                    {
                        if (!_stopping && generation == Volatile.Read(ref _generation))
                            Volatile.Write(ref _lastError, $"{ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        if (generation == Volatile.Read(ref _generation)) _isRunning = false;
                    }
                })
                {
                    IsBackground = true,
                    Name = "SeeinNetEtw"
                };
                _thread.Start();
                Thread.Sleep(600);   // let the consumer attach before we report success
                if (IsRunning) return true;
                Volatile.Write(ref _lastError, LastError ?? "ETW event consumer did not remain active.");
                _stopping = true;
                Interlocked.Increment(ref _generation);
                _isRunning = false;
                try { _session?.Dispose(); } catch { }
                _session = null;
                _thread = null;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, $"{ex.GetType().Name}: {ex.Message}");
                _stopping = true;
                Interlocked.Increment(ref _generation);
                _isRunning = false;
                try { _session?.Dispose(); } catch { }
                _session = null;
                _thread = null;
                Thread.Sleep(400 * attempt);
            }
        }
        _isRunning = false;
        return false;
    }

    private void Record(int pid, int size, IPAddress source, IPAddress destination, bool upload)
    {
        if (pid <= 0 || size <= 0 || NetworkSamplerPolicy.IsLoopbackTraffic(source, destination)) return;
        lock (_gate)
        {
            _pending.TryGetValue(pid, out var total);
            _pending[pid] = upload ? (total.Up + size, total.Down) : (total.Up, total.Down + size);
        }
        Interlocked.Increment(ref EventCount);
    }

    /// <summary>Bytes per process since the previous drain.</summary>
    public Dictionary<int, (long Up, long Down)> Drain()
    {
        lock (_gate)
        {
            var copy = new Dictionary<int, (long Up, long Down)>(_pending);
            _pending.Clear();
            return copy;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        Interlocked.Increment(ref _generation);
        _isRunning = false;
        try { _session?.Dispose(); } catch { }
        _session = null;
    }
}
