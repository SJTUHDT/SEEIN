using System.IO;
using System.Text;
using System.Threading.Channels;

namespace Seein.Core.Ipc;

public sealed class BoundedPipeLineReader(Stream stream, int maxLineBytes = PipeMessageLimits.MaxLineBytes)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _buffer = new byte[4096];
    private int _offset;
    private int _count;

    public async Task<string?> ReadLineAsync(CancellationToken token = default)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await stream.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0) return line.Length == 0 ? null : Decode(line);
            }

            var newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
            var segmentEnd = newline < 0 ? _count : newline;
            var segmentLength = segmentEnd - _offset;
            if (segmentLength > maxLineBytes - line.Length)
                throw new InvalidDataException($"IPC message exceeds {maxLineBytes} bytes.");
            line.Write(_buffer.AsSpan(_offset, segmentLength));
            _offset = newline < 0 ? _count : newline + 1;
            if (newline >= 0) return Decode(line);
        }
    }

    private static string Decode(MemoryStream line)
    {
        var bytes = line.GetBuffer();
        var length = checked((int)line.Length);
        if (length > 0 && bytes[length - 1] == '\r') length--;
        return StrictUtf8.GetString(bytes, 0, length);
    }
}

public static class PipeMessageLimits
{
    public const int MaxLineBytes = 1024 * 1024;
    public const int MaxConcurrentClients = 8;
    public const int MaxPendingRequestsPerClient = 16;

    public static bool IsAuthorizedSid(string? clientSid, string? allowedSid) =>
        !string.IsNullOrWhiteSpace(clientSid) &&
        !string.IsNullOrWhiteSpace(allowedSid) &&
        string.Equals(clientSid, allowedSid, StringComparison.OrdinalIgnoreCase);

    internal static Channel<PipeEnvelope> CreateRequestChannel() =>
        Channel.CreateBounded<PipeEnvelope>(new BoundedChannelOptions(MaxPendingRequestsPerClient)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
}
