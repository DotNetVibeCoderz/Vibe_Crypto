using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Crypto.Net.Core;

/// <summary>
/// JSON-RPC 2.0 over a WebSocket with subscriptions (<c>eth_subscribe</c>, Solana <c>*Subscribe</c>, Substrate
/// <c>chain_subscribe*</c> / <c>author_submitAndWatchExtrinsic</c>). Calls are multiplexed on one connection, which is
/// opened on first use. When the connection drops, pending calls fail and open subscriptions end with the error;
/// callers resubscribe (or fall back to polling with <see cref="ChainClientExtensions"/>).
/// </summary>
public sealed class WebSocketRpcClient : IAsyncDisposable, IDisposable
{
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    private readonly Func<CancellationToken, ValueTask<WebSocket>> _connect;
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, Channel<JsonElement>> _subscriptions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private WebSocket? _socket;
    private Task? _receiveLoop;
    private long _id;
    private int _disposed;

    /// <summary>Connects to a <c>ws://</c> or <c>wss://</c> endpoint; <c>http(s)://</c> URLs are converted.</summary>
    public WebSocketRpcClient(string url, IReadOnlyDictionary<string, string>? headers = null)
        : this(new RpcEndpoint(url, headers))
    {
    }

    /// <summary>Connects to <paramref name="endpoint"/>, sending its headers (e.g. a dRPC key) on the handshake.</summary>
    public WebSocketRpcClient(RpcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        string url = ToWebSocketUrl(endpoint.Url);
        DisplayName = endpoint.DisplayName;
        _connect = async ct =>
        {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            if (endpoint.Headers is not null)
                foreach (var (name, value) in endpoint.Headers) ws.Options.SetRequestHeader(name, value);
            await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
            return ws;
        };
    }

    /// <summary>Uses a caller-supplied connection factory (custom transports, proxies, tests).</summary>
    public WebSocketRpcClient(Func<CancellationToken, ValueTask<WebSocket>> connect)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        DisplayName = "custom transport";
    }

    /// <summary>Endpoint description safe for logs (API keys redacted when the endpoint names a provider).</summary>
    public string DisplayName { get; }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary><c>https://host/path</c> → <c>wss://host/path</c>, <c>http</c> → <c>ws</c>; other URLs unchanged.</summary>
    public static string ToWebSocketUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + url[8..];
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + url[7..];
        return url;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (IsConnected) return;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            if (_socket is not null) throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The WebSocket connection was closed");
            _socket = await _connect(ct).ConfigureAwait(false);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_socket, _lifetime.Token));
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Calls <paramref name="method"/> and returns the raw <c>result</c> element.</summary>
    public Task<JsonElement> CallAsync(string method, object?[]? parameters = null, CancellationToken ct = default) =>
        SendRequestAsync(method, parameters, subscribe: false, ct);

    /// <summary>
    /// Opens a subscription and streams its notifications (the <c>params.result</c> of each). Disposing the enumerator
    /// (e.g. leaving <c>await foreach</c>) calls <paramref name="unsubscribeMethod"/>.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> SubscribeAsync(string method, object?[]? parameters, string? unsubscribeMethod,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = await SendRequestAsync(method, parameters, subscribe: true, ct).ConfigureAwait(false);
        string key = Key(id);
        if (!_subscriptions.TryGetValue(key, out var channel)) yield break;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            _subscriptions.TryRemove(key, out _);
            if (unsubscribeMethod is not null && IsConnected)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await CallAsync(unsubscribeMethod, [id], timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is RpcException or WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Best effort: the server drops the subscription with the connection anyway.
                }
            }
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object?[]? parameters, bool subscribe, CancellationToken ct)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        long id = Interlocked.Increment(ref _id);
        var pending = new Pending(method, subscribe);
        _pending[id] = pending;
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? [],
        });
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _socket!.SendAsync(body, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
            using (ct.Register(() => pending.Completion.TrySetCanceled(ct)))
                return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        Exception? failure = null;
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                buffer.ResetWrittenCount();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.GetMemory(8 * 1024), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        failure = new WebSocketException(WebSocketError.ConnectionClosedPrematurely, $"Server closed the connection ({socket.CloseStatus} {socket.CloseStatusDescription})");
                        return;
                    }
                    buffer.Advance(result.Count);
                    if (buffer.WrittenCount > MaxMessageBytes) throw new WebSocketException(WebSocketError.Faulted, "Message too large");
                }
                while (!result.EndOfMessage);
                Dispatch(buffer.WrittenSpan);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            failure = ex;
        }
        finally
        {
            var error = failure ?? new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The WebSocket connection was closed");
            foreach (var p in _pending.Values) p.Completion.TrySetException(error);
            foreach (var c in _subscriptions.Values) c.Writer.TryComplete(ct.IsCancellationRequested ? null : error);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> message)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(message.ToArray());
        }
        catch (JsonException)
        {
            return; // ignore non-JSON frames
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray()) DispatchOne(item);
                return;
            }
            DispatchOne(root);
        }
    }

    private void DispatchOne(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object) return;
        if (msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out long id))
        {
            if (!_pending.TryGetValue(id, out var pending)) return;
            if (msg.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            {
                long code = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var c) && c.TryGetInt64(out long cv) ? cv : -1;
                string text = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.ToString() : err.ToString();
                string? data = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("data", out var d) ? d.ToString() : null;
                pending.Completion.TrySetException(new RpcException(code, text, data, pending.Method));
                return;
            }
            var result = msg.TryGetProperty("result", out var r) ? r.Clone() : default;
            // Register the subscription before completing the call, so notifications that follow immediately are kept.
            if (pending.Subscribe)
                _subscriptions.TryAdd(Key(result), Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }));
            pending.Completion.TrySetResult(result);
            return;
        }
        if (msg.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("subscription", out var sub) && _subscriptions.TryGetValue(Key(sub), out var channel))
        {
            channel.Writer.TryWrite(p.TryGetProperty("result", out var value) ? value.Clone() : default);
        }
    }

    private static string Key(JsonElement id) => id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
        }
        socket?.Dispose();
        _lifetime.Dispose();
        _sendLock.Dispose();
        _connectLock.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Pending(string method, bool subscribe)
    {
        public string Method { get; } = method;
        public bool Subscribe { get; } = subscribe;
        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
