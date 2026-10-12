using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Crypto.Net.WalletConnect;

/// <summary>A message delivered on a subscribed topic (still encrypted).</summary>
public sealed record RelayMessage(string Topic, string Message, long PublishedAt, int Tag);

/// <summary>
/// WebSocket connection to the WalletConnect relay (<c>irn</c> JSON-RPC): authenticates with a <c>did:key</c> JWT,
/// subscribes to topics, publishes envelopes and acknowledges deliveries.
/// </summary>
public sealed class WalletConnectRelay : IAsyncDisposable
{
    public const string DefaultRelayUrl = "wss://relay.walletconnect.org";

    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<RelayMessage> _inbox = Channel.CreateUnbounded<RelayMessage>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _receiveLoop;
    private Task? _dispatchLoop;
    private long _nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    /// <summary>Raised for every message on a subscribed topic.</summary>
    public event Func<RelayMessage, Task>? MessageReceived;

    public static async Task<WalletConnectRelay> ConnectAsync(string projectId, string relayUrl = DefaultRelayUrl, string? origin = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var relay = new WalletConnectRelay();
        byte[] seed = RandomNumberGenerator.GetBytes(32);
        string jwt = WalletConnectCrypto.RelayJwt(seed, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), relayUrl, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 86400);
        CryptographicOperations.ZeroMemory(seed);
        string ua = "wc-2/dotnet-cryptonet-" + typeof(WalletConnectRelay).Assembly.GetName().Version?.ToString(3) + "/" + Environment.OSVersion.Platform;
        string url = $"{relayUrl}?auth={Uri.EscapeDataString(jwt)}&ua={Uri.EscapeDataString(ua)}&projectId={Uri.EscapeDataString(projectId)}";
        if (origin is not null) relay._socket.Options.SetRequestHeader("Origin", origin);
        await relay._socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
        relay._receiveLoop = Task.Run(relay.ReceiveLoopAsync);
        relay._dispatchLoop = Task.Run(relay.DispatchLoopAsync);
        return relay;
    }

    public async Task<string> SubscribeAsync(string topic, CancellationToken ct = default) =>
        (await CallAsync("irn_subscribe", new JsonObject { ["topic"] = topic }, ct).ConfigureAwait(false))!.GetValue<string>();

    public Task UnsubscribeAsync(string topic, string subscriptionId, CancellationToken ct = default) =>
        CallAsync("irn_unsubscribe", new JsonObject { ["topic"] = topic, ["id"] = subscriptionId }, ct);

    public Task PublishAsync(string topic, string message, int tag, int ttlSeconds = 300, bool prompt = false, CancellationToken ct = default) =>
        CallAsync("irn_publish", new JsonObject { ["topic"] = topic, ["message"] = message, ["ttl"] = ttlSeconds, ["prompt"] = prompt, ["tag"] = tag }, ct);

    private async Task<JsonNode?> CallAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await SendAsync(new JsonObject { ["id"] = id, ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, ct).ConfigureAwait(false);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    private async Task SendAsync(JsonNode node, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await _socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var node = JsonNode.Parse(ms.ToArray());
                ms.SetLength(0);
                if (node is JsonObject o) await HandleAsync(o).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        foreach (var p in _pending.Values) p.TrySetException(new WebSocketException("Relay connection closed"));
        _inbox.Writer.TryComplete();
    }

    // Handlers run here, off the receive loop, so they can await relay calls whose replies the loop delivers.
    private async Task DispatchLoopAsync()
    {
        await foreach (var message in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (MessageReceived is not { } handler) continue;
            try
            {
                await handler(message).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or InvalidOperationException or KeyNotFoundException or WalletConnectException or TimeoutException or WebSocketException)
            {
                // A malformed or foreign message on a shared topic must not stop the connection.
            }
        }
    }

    private async Task HandleAsync(JsonObject o)
    {
        if (o["method"]?.GetValue<string>() == "irn_subscription")
        {
            var data = o["params"]!["data"]!;
            await SendAsync(new JsonObject { ["id"] = o["id"]!.DeepClone(), ["jsonrpc"] = "2.0", ["result"] = true }, _cts.Token).ConfigureAwait(false);
            var message = new RelayMessage(data["topic"]!.GetValue<string>(), data["message"]!.GetValue<string>(),
                data["publishedAt"]?.GetValue<long>() ?? 0, data["tag"]?.GetValue<int>() ?? 0);
            _inbox.Writer.TryWrite(message);
            return;
        }
        if (o["id"] is { } idNode && idNode.GetValueKind() == JsonValueKind.Number && _pending.TryRemove(idNode.GetValue<long>(), out var tcs))
        {
            if (o["error"] is { } err) tcs.TrySetException(new WalletConnectException(err["code"]?.GetValue<int>() ?? -1, err["message"]?.GetValue<string>() ?? "Relay error"));
            else tcs.TrySetResult(o["result"]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        if (_receiveLoop is not null) await _receiveLoop.ConfigureAwait(false);
        _inbox.Writer.TryComplete();
        if (_dispatchLoop is not null) await _dispatchLoop.ConfigureAwait(false);
        _socket.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>An error from the relay or from the peer (JSON-RPC error code and message).</summary>
public sealed class WalletConnectException(int code, string message) : Exception($"WalletConnect error {code}: {message}")
{
    public int Code { get; } = code;

    /// <summary>The user rejected the request in the wallet (EIP-1193 code 4001 or WalletConnect 5000).</summary>
    public bool IsUserRejection => Code is 4001 or 5000 or 5002;
}
