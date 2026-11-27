using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Seein.Core.Tools;

public static class ToolPipeClient
{
    public static async Task<ToolResponse> SendAsync(string pipeName, ToolRequest request,
        CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ToolCatalog.Json).AsMemory(), timeout.Token).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null || line.Length > 4 * 1024 * 1024) throw new IOException("工具响应为空或超过大小限制。");
            var result = JsonSerializer.Deserialize<ToolResponse>(line, ToolCatalog.Json);
            if (result is null || result.Id != request.Id || result.Version != 1) throw new IOException("工具协议响应不匹配。");
            return result;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return new(request.Id, false, "工具未及时响应，请重试。"); }
    }
}
