using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Seein.Core.Ipc;

public static class PipeNames
{
    // The suffix keeps a development instance from talking to the installed service.
    public static string Service { get; } = "Seein.Service.v1" + AppPaths.InstanceSuffix;
}

public sealed class PanelPipeServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SecurityIdentifier _allowedUserSid;
    private Task? _acceptTask;
    private int _impersonationFallbackLogged;

    public PanelPipeServer(string? allowedUserSid = null)
    {
        allowedUserSid ??= WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(allowedUserSid)) throw new InvalidOperationException("Unable to resolve the authorized pipe user SID.");
        _allowedUserSid = new SecurityIdentifier(allowedUserSid);
    }

    public event Func<PipeEnvelope, Task>? MessageReceived;

    public void Start() => _acceptTask ??= Task.Run(() => AcceptLoopAsync(_shutdown.Token));

    public async Task BroadcastAsync<T>(string type, T message, CancellationToken token = default)
    {
        var json = JsonSerializer.Serialize(PipeEnvelope.Create(type, message), JsonDefaults.Options);
        foreach (var pair in _clients.ToArray())
        {
            try { await pair.Value.SendAsync(json, token).ConfigureAwait(false); }
            catch { Remove(pair.Key); }
        }
    }

    public async Task BroadcastEnvelopeAsync(PipeEnvelope message,CancellationToken token=default)
    {
        var json=JsonSerializer.Serialize(message,JsonDefaults.Options);
        foreach(var pair in _clients.ToArray())
        {try{await pair.Value.SendAsync(json,token).ConfigureAwait(false);}catch{Remove(pair.Key);}}
    }
    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var security = CreatePipeSecurity(_allowedUserSid);
            var pipe = NamedPipeServerStreamAcl.Create(PipeNames.Service, PipeDirection.InOut, PipeMessageLimits.MaxConcurrentClients, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                if (!IsAuthorizedClient(pipe))
                {
                    pipe.Dispose();
                    continue;
                }
                var id = Guid.NewGuid();
                var client = new Client(pipe);
                _clients[id] = client;
                _ = Task.Run(() => ReadClientAsync(id, client, token), token);
            }
            catch
            {
                pipe.Dispose();
                if (token.IsCancellationRequested) break;
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
    }

    internal static PipeSecurity CreatePipeSecurity(SecurityIdentifier allowedUserSid)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(allowedUserSid, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    private bool IsAuthorizedClient(NamedPipeServerStream pipe)
    {
        string? clientSid = null;
        string? error = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                clientSid = identity.User?.Value;
            });
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        if (error is not null)
        {
            // Windows only allows impersonation after data has been read from the pipe. The pipe
            // security descriptor already restricts connections to the tray SID, SYSTEM and
            // administrators, so an unimpersonable (but connected) client is still trusted.
            if (Interlocked.Exchange(ref _impersonationFallbackLogged, 1) == 0)
            {
                try
                {
                    var log = Path.Combine(AppPaths.SystemDefault.Logs, "pipe-auth.log");
                    File.AppendAllText(log, $"{DateTimeOffset.Now:O} impersonation-fallback allowed={_allowedUserSid.Value} error={error}{Environment.NewLine}");
                }
                catch { }
            }
            return true;
        }

        var allowed = PipeMessageLimits.IsAuthorizedSid(clientSid, _allowedUserSid.Value);
        if (!allowed)
        {
            try
            {
                var log = Path.Combine(AppPaths.SystemDefault.Logs, "pipe-auth.log");
                File.AppendAllText(log, $"{DateTimeOffset.Now:O} denied client={clientSid ?? "<null>"} allowed={_allowedUserSid.Value}{Environment.NewLine}");
            }
            catch { }
        }
        return allowed;
    }

    private async Task ReadClientAsync(Guid id, Client client, CancellationToken token)
    {
        var dispatcher = DispatchClientAsync(client, token);
        try
        {
            while (!token.IsCancellationRequested && client.Reader is not null)
            {
                var line = await client.Reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                var envelope = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonDefaults.Options);
                if (envelope is not null) await client.EnqueueAsync(envelope, token).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            client.Complete();
            try { await dispatcher.ConfigureAwait(false); } catch { }
            Remove(id);
        }
    }

    private async Task DispatchClientAsync(Client client, CancellationToken token)
    {
        await foreach (var envelope in client.ReadRequestsAsync(token).ConfigureAwait(false))
            if (MessageReceived is not null) await MessageReceived.Invoke(envelope).ConfigureAwait(false);
    }

    private void Remove(Guid id)
    {
        if (_clients.TryRemove(id, out var client)) client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var id in _clients.Keys) Remove(id);
        if (_acceptTask is not null) try { await _acceptTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }

    private sealed class Client : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Channel<PipeEnvelope> _requests = PipeMessageLimits.CreateRequestChannel();
        public BoundedPipeLineReader Reader { get; }
        private StreamWriter Writer { get; }

        public Client(NamedPipeServerStream pipe)
        {
            _pipe = pipe;
            Reader = new BoundedPipeLineReader(pipe);
            Writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        }

        public async Task SendAsync(string json, CancellationToken token)
        {
            if (Encoding.UTF8.GetByteCount(json) > PipeMessageLimits.MaxLineBytes)
                throw new InvalidDataException("IPC message exceeds the 1 MiB limit.");
            await _writeLock.WaitAsync(token).ConfigureAwait(false);
            try { await Writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }

        public ValueTask EnqueueAsync(PipeEnvelope envelope, CancellationToken token) => _requests.Writer.WriteAsync(envelope, token);
        public IAsyncEnumerable<PipeEnvelope> ReadRequestsAsync(CancellationToken token) => _requests.Reader.ReadAllAsync(token);
        public void Complete() => _requests.Writer.TryComplete();
        public void Dispose() { Complete(); Writer.Dispose(); _pipe.Dispose(); _writeLock.Dispose(); }
    }
}

public sealed class PanelPipeClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private BoundedPipeLineReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken token = default)
    {
        await DisposePipeAsync().ConfigureAwait(false);
        _pipe = new NamedPipeClientStream(".", PipeNames.Service, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        await _pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        _reader = new BoundedPipeLineReader(_pipe);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    }

    public async Task SendAsync<T>(string type, T payload, CancellationToken token = default)
    {
        if (_writer is null) throw new InvalidOperationException("未连接到服务");
        var json = JsonSerializer.Serialize(PipeEnvelope.Create(type, payload), JsonDefaults.Options);
        if (Encoding.UTF8.GetByteCount(json) > PipeMessageLimits.MaxLineBytes)
            throw new InvalidDataException("IPC message exceeds the 1 MiB limit.");
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try { await _writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public async IAsyncEnumerable<PipeEnvelope> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        if (_reader is null) throw new InvalidOperationException("未连接到服务");
        while (!token.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) yield break;
            var message = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonDefaults.Options);
            if (message is not null) yield return message;
        }
    }

    private async ValueTask DisposePipeAsync()
    {
        if (_writer is not null) await _writer.DisposeAsync().ConfigureAwait(false);
        _pipe?.Dispose();
        _writer = null; _reader = null; _pipe = null;
    }

    public async ValueTask DisposeAsync() { await DisposePipeAsync().ConfigureAwait(false); _writeLock.Dispose(); }
}


