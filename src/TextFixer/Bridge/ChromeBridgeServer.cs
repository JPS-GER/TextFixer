using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TextFixer.Bridge;

public sealed class BridgeMessageEventArgs(string sessionId, string origin, JsonElement message) : EventArgs
{
    public string SessionId { get; } = sessionId;

    /// <summary>Extension origin validated by the native host, e.g. chrome-extension://&lt;id&gt;/.</summary>
    public string Origin { get; } = origin;

    /// <summary>Validated JSON object. The schema (kind, captureId, ...) is checked by the consumer.</summary>
    public JsonElement Message { get; } = message;
}

public sealed class BridgeSessionEventArgs(string sessionId, string origin) : EventArgs
{
    public string SessionId { get; } = sessionId;
    public string Origin { get; } = origin;
}

/// <summary>
/// Tray-side end of the Chrome bridge: a current-user-only named pipe. Each connected native host
/// (one per extension port) is a session. Events fire on background threads.
/// Only connections from this same executable are accepted.
/// </summary>
public sealed class ChromeBridgeServer : IDisposable
{
    private const int MaxSessions = 16;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _pipeName;
    private readonly bool _verifyClientExecutable;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private CancellationTokenSource? _stop;
    private Task? _acceptLoop;

    public ChromeBridgeServer()
        : this(BridgeProtocol.DefaultPipeName(), verifyClientExecutable: true)
    {
    }

    internal ChromeBridgeServer(string pipeName, bool verifyClientExecutable)
    {
        _pipeName = pipeName;
        _verifyClientExecutable = verifyClientExecutable;
    }

    public event EventHandler<BridgeSessionEventArgs>? SessionConnected;
    public event EventHandler<BridgeMessageEventArgs>? MessageReceived;
    public event EventHandler<BridgeSessionEventArgs>? SessionDisconnected;

    public IReadOnlyCollection<string> SessionIds => _sessions.Keys.ToArray();

    /// <summary>
    /// Creates the first pipe instance synchronously, so a second server or a squatting pipe fails here
    /// (UnauthorizedAccessException or IOException).
    /// </summary>
    public void Start()
    {
        if (_stop is not null)
        {
            throw new InvalidOperationException("The bridge server is already started.");
        }

        var first = CreatePipe(first: true);
        var stop = new CancellationTokenSource();
        _stop = stop;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(first, stop.Token));
    }

    /// <summary>Sends a JSON object to one session.</summary>
    /// <returns>False if the session no longer exists or the write failed (the session is then closed).</returns>
    public async Task<bool> SendAsync(string sessionId, object message, CancellationToken cancellationToken = default)
    {
        var payload = message is JsonElement element
            ? Encoding.UTF8.GetBytes(element.GetRawText())
            : JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), JsonOptions);
        BridgeFraming.ParseObject(payload);
        if (payload.Length > BridgeProtocol.MaxMessageBytes)
        {
            throw new BridgeProtocolException("Message is larger than 1 MB.");
        }

        return _sessions.TryGetValue(sessionId, out var session)
            && await session.SendAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public void Stop()
    {
        var stop = Interlocked.Exchange(ref _stop, null);
        if (stop is null)
        {
            return;
        }

        stop.Cancel();
        foreach (var session in _sessions.Values)
        {
            session.Close();
        }

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        stop.Dispose();
    }

    public void Dispose() => Stop();

    private NamedPipeServerStream CreatePipe(bool first) =>
        new(_pipeName, PipeDirection.InOut, MaxSessions + 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    private async Task AcceptLoopAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = CreatePipe(first: false);
                continue;
            }

            var connected = pipe;
            pipe = CreatePipe(first: false);
            _ = Task.Run(() => RunSessionAsync(connected, cancellationToken), CancellationToken.None);
        }

        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunSessionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        Session? session = null;
        try
        {
            if (_sessions.Count >= MaxSessions || (_verifyClientExecutable && !IsSameExecutable(pipe)))
            {
                return;
            }

            string origin;
            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                helloTimeout.CancelAfter(HelloTimeout);
                var hello = await BridgeFraming.ReadMessageAsync(pipe, helloTimeout.Token).ConfigureAwait(false);
                if (hello is not { } frame
                    || BridgeFraming.Kind(frame.Message) != BridgeProtocol.HelloKind
                    || !frame.Message.TryGetProperty("protocol", out var protocol)
                    || protocol.ValueKind != JsonValueKind.Number
                    || !protocol.TryGetInt32(out var version)
                    || version != BridgeProtocol.Version
                    || !frame.Message.TryGetProperty("origin", out var originElement)
                    || !BridgeProtocol.IsExtensionOrigin(originElement.ValueKind == JsonValueKind.String ? originElement.GetString() : null))
                {
                    return;
                }

                origin = originElement.GetString()!;
            }

            session = new Session(Guid.NewGuid().ToString("N"), origin, pipe);
            _sessions[session.Id] = session;
            if (!await session.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { kind = BridgeProtocol.ReadyKind, protocol = BridgeProtocol.Version }), cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            Raise(SessionConnected, new BridgeSessionEventArgs(session.Id, origin));

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await BridgeFraming.ReadMessageAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }

                var kind = BridgeFraming.Kind(frame.Value.Message);
                if (kind is BridgeProtocol.HelloKind or BridgeProtocol.ReadyKind or BridgeProtocol.ErrorKind)
                {
                    continue;
                }

                Raise(MessageReceived, new BridgeMessageEventArgs(session.Id, origin, frame.Value.Message));
            }
        }
        catch (Exception ex) when (ex is IOException or BridgeProtocolException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (session is not null && _sessions.TryRemove(session.Id, out _))
            {
                session.Close();
                Raise(SessionDisconnected, new BridgeSessionEventArgs(session.Id, session.Origin));
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception)
        {
            // A failing consumer must not stop the transport.
        }
    }

    private static bool IsSameExecutable(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        {
            return false;
        }

        var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0)
        {
            return false;
        }

        try
        {
            var buffer = new StringBuilder(32768);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size)
                && Environment.ProcessPath is { } own
                && string.Equals(Path.GetFullPath(buffer.ToString(0, size)), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    private sealed class Session(string id, string origin, NamedPipeServerStream pipe)
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _closed;

        public string Id { get; } = id;
        public string Origin { get; } = origin;

        public async Task<bool> SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            try
            {
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            try
            {
                await BridgeFraming.WriteFrameAsync(pipe, payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Close();
                return false;
            }
            finally
            {
                try
                {
                    _writeGate.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                pipe.Dispose();
            }
        }
    }
}
