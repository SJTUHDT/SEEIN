using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using static Seein.Core.Lighting.OpenRgbProtocol;

namespace Seein.Core.Lighting;

public sealed class OpenRgbProvider(int port = OpenRgbProvider.DefaultPort, int maximumVersion = ProtocolVersion) : IAsyncDisposable
{
    public const int DefaultPort = 6743;
    private const int MaxDevices = 4096;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RgbDeviceLayout> _layouts = new(StringComparer.Ordinal);
    private readonly HashSet<int> _direct = [];
    private Connection? _connection;
    private int _generation;
    private int _loadedTopology = -1;
    private int _version;

    public RgbProviderState State { get; private set; } = RgbProviderState.NotConfigured;
    public string? Message { get; private set; }
    public string? LastError { get; private set; }
    public bool IsAvailable => State == RgbProviderState.Ready && _connection is { IsClosed: false };
    public bool NeedsRefresh => _connection is { } connection && connection.Topology != _loadedTopology;

    public async Task<IReadOnlyList<RgbDeviceState>> RefreshAsync(bool rescan = false, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var deadline = Deadline(token, rescan ? 30 : 12);
            var ct = deadline.Token;
            if (!IsAvailable) await ConnectAsync(ct).ConfigureAwait(false);
            var connection = _connection!;
            if (rescan)
            {
                var topology = connection.Topology;
                var scans = connection.CompletedScans;
                State = RgbProviderState.Connecting;
                Message = "正在重新扫描灯效设备…";
                var result = await SendCommandAsync(0, Rescan, [], ct).ConfigureAwait(false);
                if (!result.Success) throw new IOException(result.Error);
                while ((_version == 6 ? connection.CompletedScans == scans : connection.Topology == topology) ||
                       connection.Scanning || (_version == 5 && !connection.TopologySettled))
                {
                    if (connection.IsClosed) throw new IOException("OpenRGB 扫描时断开连接");
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
            while (connection.Scanning || (_version == 5 && NeedsRefresh && !connection.TopologySettled))
                await Task.Delay(100, ct).ConfigureAwait(false);

            var snapshot = connection.Topology;
            var data = await ExchangeAsync(0, ControllerCount, [], ct).ConfigureAwait(false);
            if (data.Length < 4) throw new InvalidDataException("OpenRGB 设备计数包缺失");
            var count = BinaryPrimitives.ReadInt32LittleEndian(data);
            if (count is < 0 or > MaxDevices || data.Length != 4 + (_version == 6 ? 4 * count : 0))
                throw new InvalidDataException("OpenRGB 设备计数包格式无效");

            var layouts = new List<RgbDeviceLayout>(count);
            for (var i = 0; i < count; i++)
            {
                if (snapshot != connection.Topology) break;
                var id = _version == 6 ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4 + i * 4)) : i;
                layouts.Add(await ReadLayoutAsync(id, ct).ConfigureAwait(false));
            }
            if (connection.Topology != snapshot)
            {
                _layouts.Clear();
                State = RgbProviderState.Ready;
                Message = "设备列表正在变化，等待重新读取";
                return [];
            }
            if (_loadedTopology != snapshot)
            {
                _generation++;
                _direct.Clear();
            }
            _loadedTopology = snapshot;
            _layouts.Clear();
            foreach (var layout in layouts) _layouts.Add(Address(layout.Index), layout);
            State = RgbProviderState.Ready;
            Message = layouts.Count == 0 ? "灯效引擎已连接，未识别到可控制的设备" : null;
            LastError = null;
            return layouts.Select(ToState).ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException or ChannelClosedException)
        {
            Fail(ex);
            token.ThrowIfCancellationRequested();
            return [];
        }
        finally { _gate.Release(); }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        Close();
        State = RgbProviderState.Connecting;
        Message = "正在连接 SEEIN 灯效引擎…";
        using var deadline = Deadline(token, 3);
        _connection = await Connection.OpenAsync(port, deadline.Token).ConfigureAwait(false);
        var data = await ExchangeAsync(0, RequestProtocol, Int32Payload(maximumVersion), deadline.Token).ConfigureAwait(false);
        if (data.Length != 4) throw new InvalidDataException("OpenRGB SDK 版本响应格式无效");
        _version = Math.Min(maximumVersion, BinaryPrimitives.ReadInt32LittleEndian(data));
        if (_version < MinimumVersion)
            throw new InvalidDataException($"OpenRGB SDK v{_version} 不兼容，需要 v5 或 v6");
        await _connection.SendAsync(0, SetClientName, BuildClientName("SEEIN"), deadline.Token).ConfigureAwait(false);
        if (_version == 6)
            {
            var result = await SendCommandAsync(0, SetClientFlags, Int32Payload(1), deadline.Token).ConfigureAwait(false);
            if (!result.Success) throw new InvalidDataException(result.Error);
        }
    }

    public Task<RgbWriteResult> ApplyAsync(string address, IReadOnlyList<RgbColor> colors, CancellationToken token)
        => WriteAsync(address, async (layout, ct) =>
        {
            if (colors.Count == 0 || layout.LedCount == 0) return RgbWriteResult.Reject("设备没有可写灯珠或缺少颜色");
            if (!await EnsureDirectAsync(layout, ct).ConfigureAwait(false)) return RgbWriteResult.Reject("设备不支持直接上色");
            var frame = FitColors(colors, layout.LedCount);
            return await SendCommandAsync(layout.Index, UpdateLeds, BuildUpdateLeds(frame), ct).ConfigureAwait(false);
        }, token);

    public Task<RgbWriteResult> ApplyZoneAsync(string address, int zone, IReadOnlyList<RgbColor> colors, CancellationToken token)
        => WriteAsync(address, async (layout, ct) =>
        {
            var info = layout.Zones.ElementAtOrDefault(zone);
            if (info is null || info.LedCount == 0 || colors.Count == 0) return RgbWriteResult.Reject("分区没有可写灯珠或缺少颜色");
            if (!await EnsureDirectAsync(layout, ct).ConfigureAwait(false)) return RgbWriteResult.Reject("设备不支持直接上色");
            return await SendCommandAsync(layout.Index, UpdateZoneLeds, BuildUpdateZoneLeds(zone, FitColors(colors, info.LedCount)), ct).ConfigureAwait(false);
        }, token);

    public Task<RgbWriteResult> ApplySingleLedAsync(string address, int led, RgbColor color, CancellationToken token)
        => WriteAsync(address, async (layout, ct) =>
        {
            if ((uint)led >= layout.LedCount) return RgbWriteResult.Reject("灯珠索引无效");
            if (!await EnsureDirectAsync(layout, ct).ConfigureAwait(false)) return RgbWriteResult.Reject("设备不支持直接上色");
            return await SendCommandAsync(layout.Index, UpdateSingleLed, BuildUpdateSingleLed(led, color), ct).ConfigureAwait(false);
        }, token);

    public Task<RgbWriteResult> SetModeAsync(string address, int index, int? speed, int? brightness, CancellationToken token,
        RgbColor? color = null)
        => WriteAsync(address, async (layout, ct) =>
        {
            var mode = layout.Modes.ElementAtOrDefault(index);
            if (mode is null) return RgbWriteResult.Reject("硬件模式无效");
            if (speed is >= 0 && mode.HasSpeed)
                mode = mode with { Speed = Math.Clamp((uint)speed.Value,
                    Math.Min(mode.SpeedMin, mode.SpeedMax), Math.Max(mode.SpeedMin, mode.SpeedMax)) };
            if (brightness is >= 0 && mode.HasBrightness && mode.BrightnessMin <= mode.BrightnessMax)
                mode = mode with { Brightness = Math.Clamp((uint)brightness.Value, mode.BrightnessMin, mode.BrightnessMax) };
            if (color is { } selectedColor && mode.HasModeColors && mode.ColorMode == 2)
            {
                var count = Math.Max(1, mode.Colors.Count);
                if ((uint)count >= mode.ColorsMin && (uint)count <= mode.ColorsMax)
                    mode = mode with { Colors = FitColors([selectedColor], count) };
            }
            _direct.Remove(layout.Index);
            var result = await SendCommandAsync(layout.Index, UpdateMode, BuildUpdateMode(mode, _version), ct).ConfigureAwait(false);
            if (!result.Success) return result;
            RgbColor[]? frame = null;
            if (color is { } ledColor && mode is { IsDirect: false, PerLedColor: true, ColorMode: 1 } && layout.LedCount > 0)
            {
                // Devices such as the Nitro+ expose a single software-settable color in a hardware static mode.
                frame = FitColors([ledColor], layout.LedCount);
                result = await SendCommandAsync(layout.Index, UpdateLeds, BuildUpdateLeds(frame), ct).ConfigureAwait(false);
                if (!result.Success) return result;
            }
            var refreshed = await ReadLayoutAsync(layout.Index, ct).ConfigureAwait(false);
            _layouts[address] = refreshed;
            var readMode = refreshed.ActiveMode;
            return readMode?.Index == index && readMode.Speed == mode.Speed && readMode.Brightness == mode.Brightness &&
                readMode.Colors.SequenceEqual(mode.Colors) && (frame is null || refreshed.Colors.SequenceEqual(frame))
                ? RgbWriteResult.ReadBack : result;
        }, token);

    public Task<RgbWriteResult> ResizeZoneAsync(string address, int zone, int size, CancellationToken token)
        => WriteAsync(address, async (layout, ct) =>
        {
            var info = layout.Zones.ElementAtOrDefault(zone);
            if (info is null || (info.Flags & 3) == 0 || size < info.LedsMin || size > info.LedsMax || size > ushort.MaxValue)
                return RgbWriteResult.Reject("分区尺寸不可调或超出范围");
            var result = await SendCommandAsync(layout.Index, ResizeZone, BuildResizeZone(zone, size), ct).ConfigureAwait(false);
            if (!result.Success) return result;
            var refreshed = await ReadLayoutAsync(layout.Index, ct).ConfigureAwait(false);
            _layouts[address] = refreshed;
            if (refreshed.Zones.ElementAtOrDefault(zone)?.LedCount != size) return result;
            if (info.LedCount != size)
            {
                _connection!.InvalidateTopology();
                _direct.Clear();
            }
            return RgbWriteResult.ReadBack;
        }, token);

    private static RgbColor[] FitColors(IReadOnlyList<RgbColor> colors, int count)
    {
        var frame = new RgbColor[count];
        for (var i = 0; i < count; i++) frame[i] = colors[Math.Min(i, colors.Count - 1)];
        return frame;
    }

    private async Task<bool> EnsureDirectAsync(RgbDeviceLayout layout, CancellationToken token)
    {
        if (_direct.Contains(layout.Index)) return true;
        if (!layout.SupportsDirect) return false;
        var result = await SendCommandAsync(layout.Index, SetCustomMode, [], token).ConfigureAwait(false);
        if (result.Success) _direct.Add(layout.Index);
        return result.Success;
    }

    private async Task<RgbWriteResult> WriteAsync(string address, Func<RgbDeviceLayout, CancellationToken, Task<RgbWriteResult>> operation,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsAvailable) return RgbWriteResult.Reject("灯效引擎未就绪");
            if (NeedsRefresh || _connection!.Scanning || !_layouts.TryGetValue(address, out var layout))
                return RgbWriteResult.Reject("设备已经变化，请刷新后重试");
            using var deadline = Deadline(token, 3);
            return await operation(layout, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException or ChannelClosedException)
        {
            Fail(ex);
            token.ThrowIfCancellationRequested();
            return RgbWriteResult.Reject(LastError!);
        }
        finally { _gate.Release(); }
    }

    private string Address(int id) => $"{_generation}:{(uint)id}";

    private RgbDeviceState ToState(RgbDeviceLayout layout)
    {
        var name = string.IsNullOrWhiteSpace(layout.DisplayName) ? layout.Name : layout.DisplayName;
        var identity = string.Join("\0", layout.Vendor, layout.Name, layout.Type,
            layout.Serial, string.IsNullOrWhiteSpace(layout.Serial) ? layout.Location : "");
        var topology = string.Join("\0", layout.LedCount, string.Join("|", layout.Zones.Select(zone =>
            $"{zone.Index}:{zone.Name}:{zone.Type}:{zone.LedCount}:{zone.MatrixWidth}x{zone.MatrixHeight}:{string.Join(',', zone.Matrix ?? [])}")));
        return new RgbDeviceState(new RgbDeviceInfo(layout.Index, name, TypeName(layout.Type), layout.Vendor,
            layout.Zones, layout.Modes, layout.ActiveMode?.Name, layout.SupportsDirect, layout.LedCount,
            Address(layout.Index), Hash(identity), Hash(topology), layout.ActiveModeIndex), layout.Colors);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string TypeName(int type) => type switch
    {
        0 => "主板", 1 => "内存", 2 => "显卡", 3 => "散热器", 4 => "灯带",
        5 => "键盘", 6 => "鼠标", 7 => "鼠标垫", 8 => "耳机", _ => "设备"
    };

    private async Task<RgbDeviceLayout> ReadLayoutAsync(int id, CancellationToken token)
    {
        var data = await ExchangeAsync(id, ControllerData, Int32Payload(_version), token).ConfigureAwait(false);
        try { return ParseController(id, data, _version); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"SDK v{_version} 包 {ControllerData}，设备 {(uint)id}：{ex.Message}", ex);
        }
    }

    private async Task<byte[]> ExchangeAsync(int device, int packet, byte[] data, CancellationToken token)
    {
        var connection = _connection!;
        await connection.SendAsync(device, packet, data, token).ConfigureAwait(false);
        while (true)
        {
            var response = await connection.Replies.Reader.ReadAsync(token).ConfigureAwait(false);
            if (response.Device == device && response.PacketId == packet) return response.Data;
            if (GetAck(response, device, packet) is > 0 and var error)
                throw new InvalidDataException($"OpenRGB 拒绝请求 {packet}（状态 {error}）");
        }
    }

    private async Task<RgbWriteResult> SendCommandAsync(int device, int packet, byte[] data, CancellationToken token)
    {
        var connection = _connection!;
        if (packet >= ResizeZone && (NeedsRefresh || connection.Scanning))
            return RgbWriteResult.Reject("设备列表已变化，请重新选择");
        await connection.SendAsync(device, packet, data, token).ConfigureAwait(false);
        if (_version == 5) return RgbWriteResult.Sent;
        while (true)
        {
            var response = await connection.Replies.Reader.ReadAsync(token).ConfigureAwait(false);
            if (GetAck(response, device, packet) is not int status) continue;
            return status == 0 ? RgbWriteResult.Confirmed : RgbWriteResult.Reject($"引擎拒绝命令 {packet}（状态 {status}）");
        }
    }

    private static int? GetAck(Packet response, int device, int packet)
        => response.PacketId == Ack && response.Device == device && response.Data.Length == 8 &&
           BinaryPrimitives.ReadInt32LittleEndian(response.Data) == packet
            ? BinaryPrimitives.ReadInt32LittleEndian(response.Data.AsSpan(4)) : null;

    private static CancellationTokenSource Deadline(CancellationToken token, int seconds)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(TimeSpan.FromSeconds(seconds));
        return source;
    }

    private void Fail(Exception ex)
    {
        Close();
        State = ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
            ? RgbProviderState.NotConfigured : RgbProviderState.Error;
        Message = State == RgbProviderState.Error ? "灯效引擎通信失败" : "灯效引擎未监听";
        LastError = ex switch
        {
            OperationCanceledException => $"OpenRGB SDK v{_version} 请求超时",
            ChannelClosedException => ex.InnerException?.Message ?? "OpenRGB 已断开",
            _ => ex.Message
        };
    }

    private void Close()
    {
        _connection?.Dispose();
        _connection = null;
        _loadedTopology = -1;
        _layouts.Clear();
        _direct.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Close(); }
        finally { _gate.Release(); }
    }

    private sealed record Packet(int Device, int PacketId, byte[] Data);

    private sealed class Connection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private int _topology;
        private volatile bool _scanning;
        private int _completedScans;
        private long _lastTopologyChange;

        private Connection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            _ = ReceiveAsync();
        }

        public Channel<Packet> Replies { get; } = Channel.CreateBounded<Packet>(
            new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true });
        public int Topology => Volatile.Read(ref _topology);
        public bool Scanning => _scanning;
        public int CompletedScans => Volatile.Read(ref _completedScans);
        public bool TopologySettled => Stopwatch.GetElapsedTime(Volatile.Read(ref _lastTopologyChange)) >= TimeSpan.FromSeconds(3);
        public bool IsClosed => Replies.Reader.Completion.IsCompleted || Volatile.Read(ref _closed);
        private bool _closed;

        public void InvalidateTopology()
        {
            Volatile.Write(ref _lastTopologyChange, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _topology);
        }

        public static async Task<Connection> OpenAsync(int port, CancellationToken token)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, token).ConfigureAwait(false);
                return new Connection(client);
            }
            catch { client.Dispose(); throw; }
        }

        public async Task SendAsync(int device, int packet, byte[] bytes, CancellationToken token)
        {
            await _stream.WriteAsync(BuildHeader(device, packet, bytes.Length), token).ConfigureAwait(false);
            if (bytes.Length > 0) await _stream.WriteAsync(bytes, token).ConfigureAwait(false);
        }

        private async Task ReceiveAsync()
        {
            Exception? error = null;
            try
            {
                var header = new byte[HeaderSize];
                while (true)
                {
                    await _stream.ReadExactlyAsync(header).ConfigureAwait(false);
                    var (device, packet, length) = ParseHeader(header);
                    var data = new byte[length];
                    await _stream.ReadExactlyAsync(data).ConfigureAwait(false);
                    switch (packet)
                    {
                        case ScanStarted: _scanning = true; InvalidateTopology(); break;
                        case ScanComplete:
                            InvalidateTopology();
                            _scanning = false;
                            Interlocked.Increment(ref _completedScans);
                            break;
                        case DeviceListUpdated: InvalidateTopology(); break;
                        case RequestProtocol or Ack or ControllerCount or ControllerData:
                            if (!Replies.Writer.TryWrite(new Packet(device, packet, data)))
                                throw new InvalidDataException("OpenRGB 响应队列已满");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { error = ex; }
            finally
            {
                Volatile.Write(ref _closed, true);
                Replies.Writer.TryComplete(error);
            }
        }

        public void Dispose() => _client.Dispose();
    }
}
