using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Crypto.Net.WalletConnect;

/// <summary>App or wallet metadata shown to the other side.</summary>
public sealed record WalletConnectMetadata(string Name, string Description, string Url, IReadOnlyList<string>? Icons = null)
{
    internal JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["url"] = Url,
        ["icons"] = new JsonArray([.. (Icons ?? []).Select(i => (JsonNode)JsonValue.Create(i)!)]),
    };

    internal static WalletConnectMetadata FromJson(JsonNode? n) => new(
        n?["name"]?.GetValue<string>() ?? "", n?["description"]?.GetValue<string>() ?? "", n?["url"]?.GetValue<string>() ?? "",
        n?["icons"] is JsonArray a ? [.. a.Select(i => i!.GetValue<string>())] : []);
}

/// <summary>
/// A CAIP-25 namespace: chains (<c>eip155:1</c>), methods and events, plus the approved accounts
/// (<c>eip155:1:0x…</c>) when a wallet answers.
/// </summary>
public sealed record WalletConnectNamespace(IReadOnlyList<string> Chains, IReadOnlyList<string> Methods, IReadOnlyList<string> Events, IReadOnlyList<string>? Accounts = null)
{
    internal JsonObject ToJson()
    {
        var o = new JsonObject { ["chains"] = Array(Chains), ["methods"] = Array(Methods), ["events"] = Array(Events) };
        if (Accounts is not null) o["accounts"] = Array(Accounts);
        return o;
    }

    internal static WalletConnectNamespace FromJson(JsonNode? n) => new(List(n?["chains"]), List(n?["methods"]), List(n?["events"]), n?["accounts"] is null ? null : List(n["accounts"]));

    private static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode)JsonValue.Create(i)!)]);
    private static List<string> List(JsonNode? n) => n is JsonArray a ? [.. a.Select(i => i!.GetValue<string>())] : [];
}

/// <summary>Options shared by the app and wallet sides.</summary>
public sealed class WalletConnectOptions
{
    /// <summary>Project id from the Reown (WalletConnect) cloud dashboard.</summary>
    public required string ProjectId { get; init; }
    public required WalletConnectMetadata Metadata { get; init; }
    public string RelayUrl { get; init; } = WalletConnectRelay.DefaultRelayUrl;

    /// <summary>Origin header for the relay, when the project restricts allowed origins.</summary>
    public string? Origin { get; init; }
}

/// <summary>An incoming request in a session (wallet side).</summary>
public sealed record WalletConnectRequest(string Topic, long Id, string Method, JsonNode? Params, string ChainId);

/// <summary>An established session: its topic, the peer, and the approved namespaces.</summary>
public sealed class WalletConnectSession
{
    private readonly WalletConnectEngine _engine;

    internal WalletConnectSession(WalletConnectEngine engine, string topic, WalletConnectMetadata peer, IReadOnlyDictionary<string, WalletConnectNamespace> namespaces, long expiry)
    {
        _engine = engine;
        Topic = topic;
        Peer = peer;
        Namespaces = namespaces;
        Expiry = DateTimeOffset.FromUnixTimeSeconds(expiry);
    }

    public string Topic { get; }
    public WalletConnectMetadata Peer { get; }
    public IReadOnlyDictionary<string, WalletConnectNamespace> Namespaces { get; }
    public DateTimeOffset Expiry { get; }
    public bool IsConnected { get; internal set; } = true;

    /// <summary>All approved CAIP-10 accounts, e.g. <c>eip155:1:0xabc…</c>.</summary>
    public IReadOnlyList<string> Accounts => [.. Namespaces.Values.SelectMany(n => n.Accounts ?? [])];

    /// <summary>Raised for <c>wc_sessionEvent</c> (e.g. <c>accountsChanged</c>, <c>chainChanged</c>): name, data, chain.</summary>
    public event Action<string, JsonNode?, string>? EventReceived;

    /// <summary>Raised when the peer deletes the session.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// Sends <paramref name="method"/> to the wallet for <paramref name="chainId"/> and waits for the user (5 minutes by
    /// default). Rejections throw <see cref="WalletConnectException"/> with <see cref="WalletConnectException.IsUserRejection"/>.
    /// </summary>
    public Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, string chainId, TimeSpan? timeout = null, CancellationToken ct = default) =>
        _engine.RequestAsync(Topic, "wc_sessionRequest", new JsonObject
        {
            ["request"] = new JsonObject { ["method"] = method, ["params"] = parameters },
            ["chainId"] = chainId,
        }, timeout ?? TimeSpan.FromMinutes(5), ct);

    /// <summary>Checks the peer is reachable.</summary>
    public Task PingAsync(CancellationToken ct = default) => _engine.RequestAsync(Topic, "wc_sessionPing", new JsonObject(), TimeSpan.FromSeconds(30), ct);

    /// <summary>Emits a session event to the app (wallet side), e.g. <c>accountsChanged</c>.</summary>
    public Task EmitAsync(string name, JsonNode? data, string chainId, CancellationToken ct = default) =>
        _engine.RequestAsync(Topic, "wc_sessionEvent", new JsonObject { ["event"] = new JsonObject { ["name"] = name, ["data"] = data }, ["chainId"] = chainId }, TimeSpan.FromSeconds(30), ct);

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return;
        IsConnected = false;
        await _engine.RequestAsync(Topic, "wc_sessionDelete", new JsonObject { ["code"] = 6000, ["message"] = "User disconnected." }, TimeSpan.FromSeconds(10), ct, waitForReply: false).ConfigureAwait(false);
    }

    internal void RaiseEvent(string name, JsonNode? data, string chain) => EventReceived?.Invoke(name, data, chain);

    internal void RaiseDisconnected()
    {
        IsConnected = false;
        Disconnected?.Invoke();
    }
}

/// <summary>A pairing waiting for a wallet: show <see cref="Uri"/> as a QR code or deep link.</summary>
public sealed class WalletConnectPairing
{
    internal WalletConnectPairing(string uri, Task<WalletConnectSession> approval)
    {
        Uri = uri;
        Approval = approval;
    }

    public string Uri { get; }

    /// <summary>Completes when the wallet approves (or faults when it rejects or the proposal expires).</summary>
    public Task<WalletConnectSession> Approval { get; }
}

/// <summary>A session proposal received by a wallet.</summary>
public sealed record WalletConnectProposal(long Id, string PairingTopic, byte[] ProposerPublicKey, WalletConnectMetadata Proposer,
    IReadOnlyDictionary<string, WalletConnectNamespace> RequiredNamespaces, IReadOnlyDictionary<string, WalletConnectNamespace> OptionalNamespaces);

/// <summary>
/// WalletConnect v2 for apps (dapps): create a pairing, let the user scan it with their wallet, then send JSON-RPC
/// requests such as <c>personal_sign</c> or <c>eth_sendTransaction</c> to the wallet.
/// </summary>
public sealed class WalletConnectClient : IAsyncDisposable
{
    private readonly WalletConnectEngine _engine;

    private WalletConnectClient(WalletConnectEngine engine) => _engine = engine;

    public static async Task<WalletConnectClient> CreateAsync(WalletConnectOptions options, CancellationToken ct = default) =>
        new(await WalletConnectEngine.CreateAsync(options, ct).ConfigureAwait(false));

    /// <summary>Proposes a session with <paramref name="namespaces"/> (as optional namespaces, like current SDKs do).</summary>
    public Task<WalletConnectPairing> ConnectAsync(IReadOnlyDictionary<string, WalletConnectNamespace> namespaces, CancellationToken ct = default) =>
        _engine.ProposeAsync(namespaces, ct);

    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}

/// <summary>
/// WalletConnect v2 for wallets: pair from a <c>wc:</c> URI, approve or reject proposals, and answer session requests
/// through <see cref="RequestHandler"/>.
/// </summary>
public sealed class WalletConnectWallet : IAsyncDisposable
{
    private readonly WalletConnectEngine _engine;

    private WalletConnectWallet(WalletConnectEngine engine) => _engine = engine;

    public static async Task<WalletConnectWallet> CreateAsync(WalletConnectOptions options, CancellationToken ct = default) =>
        new(await WalletConnectEngine.CreateAsync(options, ct).ConfigureAwait(false));

    /// <summary>Answers session requests: return the JSON result, or throw <see cref="WalletConnectException"/> (4001 for a user rejection).</summary>
    public Func<WalletConnectRequest, Task<JsonNode?>>? RequestHandler
    {
        get => _engine.RequestHandler;
        set => _engine.RequestHandler = value;
    }

    /// <summary>Pairs with an app's URI and waits for its session proposal.</summary>
    public Task<WalletConnectProposal> PairAsync(string uri, CancellationToken ct = default) => _engine.PairAsync(uri, ct);

    public Task<WalletConnectSession> ApproveAsync(WalletConnectProposal proposal, IReadOnlyDictionary<string, WalletConnectNamespace> namespaces, CancellationToken ct = default) =>
        _engine.ApproveAsync(proposal, namespaces, ct);

    public Task RejectAsync(WalletConnectProposal proposal, CancellationToken ct = default) => _engine.RejectAsync(proposal, ct);

    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}

/// <summary>Topics, keys, request correlation and the Sign API methods shared by both sides.</summary>
internal sealed class WalletConnectEngine : IAsyncDisposable
{
    private static readonly Dictionary<string, (int Req, int Res)> Tags = new()
    {
        ["wc_sessionPropose"] = (1100, 1101), ["wc_sessionSettle"] = (1102, 1103), ["wc_sessionUpdate"] = (1104, 1105),
        ["wc_sessionExtend"] = (1106, 1107), ["wc_sessionRequest"] = (1108, 1109), ["wc_sessionEvent"] = (1110, 1111),
        ["wc_sessionDelete"] = (1112, 1113), ["wc_sessionPing"] = (1114, 1115), ["wc_pairingDelete"] = (1000, 1001), ["wc_pairingPing"] = (1002, 1003),
    };

    private readonly WalletConnectRelay _relay;
    private readonly WalletConnectOptions _options;
    private readonly ConcurrentDictionary<string, byte[]> _keys = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, WalletConnectSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (long ProposalId, byte[] PrivateKey, IReadOnlyDictionary<string, WalletConnectNamespace> Requested, TaskCompletionSource<WalletConnectSession> Done)> _pendingSessions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WalletConnectProposal>> _awaitingProposals = new();

    private WalletConnectEngine(WalletConnectRelay relay, WalletConnectOptions options)
    {
        _relay = relay;
        _options = options;
        _relay.MessageReceived += OnMessageAsync;
    }

    public Func<WalletConnectRequest, Task<JsonNode?>>? RequestHandler { get; set; }

    public static async Task<WalletConnectEngine> CreateAsync(WalletConnectOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new WalletConnectEngine(await WalletConnectRelay.ConnectAsync(options.ProjectId, options.RelayUrl, options.Origin, ct).ConfigureAwait(false), options);
    }

    // ---------------------------------------------------------------- app side

    public async Task<WalletConnectPairing> ProposeAsync(IReadOnlyDictionary<string, WalletConnectNamespace> namespaces, CancellationToken ct)
    {
        byte[] symKey = RandomNumberGenerator.GetBytes(32);
        string pairingTopic = WalletConnectCrypto.Topic(symKey);
        _keys[pairingTopic] = symKey;
        await _relay.SubscribeAsync(pairingTopic, ct).ConfigureAwait(false);

        var (priv, pub) = WalletConnectCrypto.GenerateKeyPair();
        long expiry = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        long id = PayloadId();
        var optional = new JsonObject();
        foreach (var (key, ns) in namespaces) optional[key] = ns.ToJson();
        var done = new TaskCompletionSource<WalletConnectSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        var reply = SendRequestAsync(pairingTopic, "wc_sessionPropose", new JsonObject
        {
            ["requiredNamespaces"] = new JsonObject(),
            ["optionalNamespaces"] = optional,
            ["relays"] = new JsonArray(new JsonObject { ["protocol"] = "irn" }),
            ["proposer"] = new JsonObject { ["publicKey"] = Convert.ToHexStringLower(pub), ["metadata"] = _options.Metadata.ToJson() },
            ["expiryTimestamp"] = expiry,
            ["pairingTopic"] = pairingTopic,
            ["id"] = id,
        }, id, ct);

        _ = reply.ContinueWith(async t =>
        {
            try
            {
                var result = await t.ConfigureAwait(false);
                byte[] peer = Convert.FromHexString(result!["responderPublicKey"]!.GetValue<string>());
                byte[] sessionKey = WalletConnectCrypto.DeriveSymKey(priv, peer);
                string sessionTopic = WalletConnectCrypto.Topic(sessionKey);
                _keys[sessionTopic] = sessionKey;
                _pendingSessions[sessionTopic] = (id, priv, namespaces, done);
                await _relay.SubscribeAsync(sessionTopic).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }, TaskScheduler.Default);

        string uri = WalletConnectCrypto.PairingUri(pairingTopic, symKey, expiry);
        return new WalletConnectPairing(uri, done.Task.WaitAsync(TimeSpan.FromMinutes(5), ct));
    }

    // ---------------------------------------------------------------- wallet side

    public async Task<WalletConnectProposal> PairAsync(string uri, CancellationToken ct)
    {
        var (topic, symKey) = WalletConnectCrypto.ParsePairingUri(uri);
        _keys[topic] = symKey;
        var tcs = new TaskCompletionSource<WalletConnectProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        _awaitingProposals[topic] = tcs;
        await _relay.SubscribeAsync(topic, ct).ConfigureAwait(false);
        return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
    }

    public async Task<WalletConnectSession> ApproveAsync(WalletConnectProposal proposal, IReadOnlyDictionary<string, WalletConnectNamespace> namespaces, CancellationToken ct)
    {
        var (priv, pub) = WalletConnectCrypto.GenerateKeyPair();
        byte[] sessionKey = WalletConnectCrypto.DeriveSymKey(priv, proposal.ProposerPublicKey);
        string sessionTopic = WalletConnectCrypto.Topic(sessionKey);
        _keys[sessionTopic] = sessionKey;
        await _relay.SubscribeAsync(sessionTopic, ct).ConfigureAwait(false);

        await SendResultAsync(proposal.PairingTopic, proposal.Id, "wc_sessionPropose",
            new JsonObject { ["relay"] = new JsonObject { ["protocol"] = "irn" }, ["responderPublicKey"] = Convert.ToHexStringLower(pub) }, ct).ConfigureAwait(false);

        long expiry = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
        var ns = new JsonObject();
        foreach (var (key, n) in namespaces) ns[key] = n.ToJson();
        var session = new WalletConnectSession(this, sessionTopic, proposal.Proposer, namespaces, expiry);
        _sessions[sessionTopic] = session;
        await SendRequestAsync(sessionTopic, "wc_sessionSettle", new JsonObject
        {
            ["relay"] = new JsonObject { ["protocol"] = "irn" },
            ["namespaces"] = ns,
            ["controller"] = new JsonObject { ["publicKey"] = Convert.ToHexStringLower(pub), ["metadata"] = _options.Metadata.ToJson() },
            ["expiry"] = expiry,
        }, PayloadId(), ct).WaitAsync(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        return session;
    }

    public Task RejectAsync(WalletConnectProposal proposal, CancellationToken ct) =>
        SendErrorAsync(proposal.PairingTopic, proposal.Id, "wc_sessionPropose", 5000, "User rejected.", ct);

    // ---------------------------------------------------------------- requests

    public async Task<JsonNode?> RequestAsync(string topic, string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct, bool waitForReply = true)
    {
        long id = PayloadId();
        var reply = SendRequestAsync(topic, method, parameters, id, ct);
        if (!waitForReply)
        {
            _pending.TryRemove(id, out _);
            return null;
        }
        return await reply.WaitAsync(timeout, ct).ConfigureAwait(false);
    }

    private async Task<JsonNode?> SendRequestAsync(string topic, string method, JsonObject parameters, long id, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        string json = new JsonObject { ["id"] = id, ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }.ToJsonString();
        var (req, _) = Tags[method];
        await _relay.PublishAsync(topic, WalletConnectCrypto.Encrypt(_keys[topic], json), req, method == "wc_sessionDelete" ? 86400 : 300,
            prompt: method is "wc_sessionPropose" or "wc_sessionRequest" or "wc_sessionEvent", ct: ct).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    private Task SendResultAsync(string topic, long id, string method, JsonNode? result, CancellationToken ct = default)
    {
        string json = new JsonObject { ["id"] = id, ["jsonrpc"] = "2.0", ["result"] = result }.ToJsonString();
        return _relay.PublishAsync(topic, WalletConnectCrypto.Encrypt(_keys[topic], json), Tags.TryGetValue(method, out var t) ? t.Res : 1109, ct: ct);
    }

    private Task SendErrorAsync(string topic, long id, string method, int code, string message, CancellationToken ct = default)
    {
        string json = new JsonObject { ["id"] = id, ["jsonrpc"] = "2.0", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
        return _relay.PublishAsync(topic, WalletConnectCrypto.Encrypt(_keys[topic], json), Tags.TryGetValue(method, out var t) ? t.Res : 1109, ct: ct);
    }

    private async Task OnMessageAsync(RelayMessage m)
    {
        if (!_keys.TryGetValue(m.Topic, out var key)) return;
        var (text, _) = WalletConnectCrypto.Decrypt(key, m.Message);
        if (JsonNode.Parse(text) is not JsonObject o) return;
        long id = o["id"]?.GetValue<long>() ?? 0;

        if (o["method"]?.GetValue<string>() is not { } method)
        {
            if (!_pending.TryRemove(id, out var tcs)) return;
            if (o["error"] is { } err) tcs.TrySetException(new WalletConnectException(err["code"]?.GetValue<int>() ?? -1, err["message"]?.GetValue<string>() ?? "Rejected"));
            else tcs.TrySetResult(o["result"]);
            return;
        }

        var p = o["params"];
        switch (method)
        {
            case "wc_sessionPropose" when _awaitingProposals.TryRemove(m.Topic, out var waiting):
                waiting.TrySetResult(new WalletConnectProposal(id, m.Topic,
                    Convert.FromHexString(p!["proposer"]!["publicKey"]!.GetValue<string>()),
                    WalletConnectMetadata.FromJson(p["proposer"]!["metadata"]),
                    Namespaces(p["requiredNamespaces"]), Namespaces(p["optionalNamespaces"])));
                return;
            case "wc_sessionSettle" when _pendingSessions.TryRemove(m.Topic, out var pendingSession):
            {
                var session = new WalletConnectSession(this, m.Topic, WalletConnectMetadata.FromJson(p!["controller"]?["metadata"]), Namespaces(p["namespaces"]), p["expiry"]?.GetValue<long>() ?? 0);
                _sessions[m.Topic] = session;
                await SendResultAsync(m.Topic, id, method, true).ConfigureAwait(false);
                pendingSession.Done.TrySetResult(session);
                return;
            }
            case "wc_sessionRequest":
            {
                string chain = p?["chainId"]?.GetValue<string>() ?? "";
                var request = new WalletConnectRequest(m.Topic, id, p?["request"]?["method"]?.GetValue<string>() ?? "", p?["request"]?["params"]?.DeepClone(), chain);
                if (RequestHandler is null)
                {
                    await SendErrorAsync(m.Topic, id, method, 10001, "Unsupported method").ConfigureAwait(false);
                    return;
                }
                try
                {
                    await SendResultAsync(m.Topic, id, method, await RequestHandler(request).ConfigureAwait(false)).ConfigureAwait(false);
                }
                catch (WalletConnectException ex)
                {
                    await SendErrorAsync(m.Topic, id, method, ex.Code, ex.Message).ConfigureAwait(false);
                }
                return;
            }
            case "wc_sessionEvent":
                if (_sessions.TryGetValue(m.Topic, out var s1)) s1.RaiseEvent(p?["event"]?["name"]?.GetValue<string>() ?? "", p?["event"]?["data"]?.DeepClone(), p?["chainId"]?.GetValue<string>() ?? "");
                await SendResultAsync(m.Topic, id, method, true).ConfigureAwait(false);
                return;
            case "wc_sessionDelete":
                await SendResultAsync(m.Topic, id, method, true).ConfigureAwait(false);
                if (_sessions.TryRemove(m.Topic, out var s2)) s2.RaiseDisconnected();
                return;
            case "wc_sessionPing" or "wc_pairingPing" or "wc_sessionExtend" or "wc_sessionUpdate" or "wc_pairingDelete":
                await SendResultAsync(m.Topic, id, method, true).ConfigureAwait(false);
                return;
            default:
                await SendErrorAsync(m.Topic, id, method, 10001, $"Unsupported method {method}").ConfigureAwait(false);
                return;
        }
    }

    private static Dictionary<string, WalletConnectNamespace> Namespaces(JsonNode? node) =>
        node is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => WalletConnectNamespace.FromJson(kv.Value)) : [];

    private static long PayloadId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + RandomNumberGenerator.GetInt32(1000);

    public ValueTask DisposeAsync() => _relay.DisposeAsync();
}
