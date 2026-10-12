using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Ton;

/// <summary>Balance and contract state of an account (<c>active</c>, <c>uninitialized</c> or <c>frozen</c>).</summary>
public sealed record TonAccountState(BigInteger Balance, string State)
{
    public bool IsActive => State == "active";
}

/// <summary>Fees a message would pay (nanotons), from an emulated run.</summary>
public sealed record TonFees(long InForwardFee, long StorageFee, long GasFee, long ForwardFee)
{
    public long Total => InForwardFee + StorageFee + GasFee + ForwardFee;
}

/// <summary>
/// TON Center client (HTTP API v2 and v3). Without an API key TON Center allows about one request per second; the
/// client retries rate-limited requests.
/// </summary>
public sealed class TonCenterClient : IChainClient
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    /// <param name="baseUrl">The API root that contains <c>/v2</c> and <c>/v3</c> (e.g. <c>https://toncenter.com/api</c>).</param>
    /// <param name="apiKey">Optional TON Center API key (sent as <c>X-API-Key</c>) for higher rate limits.</param>
    public TonCenterClient(TonChain network, string? baseUrl = null, HttpClient? httpClient = null, string? apiKey = null)
    {
        Network = network ?? throw new ArgumentNullException(nameof(network));
        _baseUrl = (baseUrl ?? network.DefaultApiUrl).TrimEnd('/');
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _apiKey = apiKey;
    }

    public TonChain Network { get; }
    public IChain Chain => Network;

    /// <summary>Latest masterchain block seqno.</summary>
    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default)
    {
        var r = await V2Async(HttpMethod.Get, "getMasterchainInfo", null, ct).ConfigureAwait(false);
        return r.GetProperty("last").GetProperty("seqno").GetUInt64();
    }

    public async ValueTask<TonAccountState> GetAccountStateAsync(string address, CancellationToken ct = default)
    {
        var r = await V2Async(HttpMethod.Get, $"getAddressInformation?address={Uri.EscapeDataString(address)}", null, ct).ConfigureAwait(false);
        return new TonAccountState(BigInteger.Parse(r.GetProperty("balance").GetString()!, CultureInfo.InvariantCulture), r.GetProperty("state").GetString()!);
    }

    public async ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default) =>
        new((await GetAccountStateAsync(address.Value, ct).ConfigureAwait(false)).Balance, 9);

    /// <summary>The wallet's current seqno, or 0 when the wallet contract is not deployed yet.</summary>
    public async ValueTask<uint> GetSeqnoAsync(string walletAddress, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/v3/walletInformation?address={Uri.EscapeDataString(walletAddress)}&use_v2=false", null, ct).ConfigureAwait(false);
        return r.TryGetProperty("seqno", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetUInt32() : 0;
    }

    /// <summary>Runs a get-method with no arguments and returns the stack entries as TON Center reports them.</summary>
    public async ValueTask<JsonElement> RunGetMethodAsync(string address, string method, CancellationToken ct = default)
    {
        var r = await V2Async(HttpMethod.Post, "runGetMethod", new { address, method, stack = Array.Empty<object>() }, ct).ConfigureAwait(false);
        int exit = r.GetProperty("exit_code").GetInt32();
        if (exit != 0) throw new RpcException(exit, $"Get-method {method} exited with code {exit}", method: "runGetMethod");
        return r.GetProperty("stack");
    }

    /// <summary>Emulates the wallet receiving <paramref name="body"/> (with its state init when undeployed) and returns the fees.</summary>
    public async ValueTask<TonFees> EstimateFeeAsync(TonAccount account, TonCell body, bool includeStateInit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var init = includeStateInit ? account.StateInit.BeginParse() : null;
        TonCell? code = null, data = null;
        if (init is not null)
        {
            init.LoadBit();
            init.LoadBit();
            code = init.LoadMaybeRef();
            data = init.LoadMaybeRef();
        }
        var r = await V2Async(HttpMethod.Post, "estimateFee", new
        {
            address = account.Address.Value,
            body = body.ToBocBase64(),
            init_code = code?.ToBocBase64() ?? "",
            init_data = data?.ToBocBase64() ?? "",
            ignore_chksig = false,
        }, ct).ConfigureAwait(false);
        var f = r.GetProperty("source_fees");
        return new TonFees(f.GetProperty("in_fwd_fee").GetInt64(), f.GetProperty("storage_fee").GetInt64(), f.GetProperty("gas_fee").GetInt64(), f.GetProperty("fwd_fee").GetInt64());
    }

    /// <summary>Broadcasts a serialized external message (BoC); returns its normalized hash (hex).</summary>
    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        var message = TonCell.FromBoc(signedTxBytes.Span);
        await V2Async(HttpMethod.Post, "sendBoc", new { boc = System.Convert.ToBase64String(signedTxBytes.Span) }, ct).ConfigureAwait(false);
        return new TxHash(System.Convert.ToHexStringLower(TonMessage.NormalizedHash(message)));
    }

    /// <summary>Signs and sends <paramref name="messages"/> from <paramref name="account"/>, deploying the wallet on its first transfer.</summary>
    public async ValueTask<TxHash> SendAsync(TonAccount account, IReadOnlyList<TonMessage> messages, byte sendMode = TonSendMode.Default, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        uint seqno = await GetSeqnoAsync(account.Address.Value, ct).ConfigureAwait(false);
        var external = account.CreateTransfer(seqno, messages, sendMode: sendMode);
        return await SendRawTransactionAsync(external.ToBoc(), ct).ConfigureAwait(false);
    }

    /// <summary>Sends TON, optionally with a comment.</summary>
    public ValueTask<TxHash> TransferAsync(TonAccount account, string to, Amount amount, string? comment = null, CancellationToken ct = default)
    {
        var (address, bounceable, _) = TonAddress.ParseWithFlags(to);
        var value = amount.WithDecimals(9).BaseUnits;
        bool bounce = bounceable ?? false;
        var message = comment is null ? new TonMessage(address, value, bounce) : TonMessage.WithComment(address, value, comment, bounce);
        return SendAsync(account, [message], ct: ct);
    }

    /// <summary>The jetton wallet that holds <paramref name="jettonMaster"/> tokens for <paramref name="owner"/>, or null if none exists yet.</summary>
    public async ValueTask<TonAddress?> GetJettonWalletAsync(string owner, string jettonMaster, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/v3/jetton/wallets?owner_address={Uri.EscapeDataString(owner)}&jetton_address={Uri.EscapeDataString(jettonMaster)}&limit=1", null, ct).ConfigureAwait(false);
        var wallets = r.GetProperty("jetton_wallets");
        return wallets.GetArrayLength() == 0 ? null : TonAddress.Parse(wallets[0].GetProperty("address").GetString()!);
    }

    /// <summary>
    /// Receipt of the transaction triggered by an external message, looked up by the message's normalized hash (hex or
    /// base64). Null until TON Center has indexed it.
    /// </summary>
    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default)
    {
        string key = hash.Value.Length == 64 && hash.Value.All(Uri.IsHexDigit) ? System.Convert.ToBase64String(System.Convert.FromHexString(hash.Value)) : hash.Value;
        var r = await SendAsync(HttpMethod.Get, $"/v3/transactionsByMessage?msg_hash={Uri.EscapeDataString(key)}&direction=in&limit=1", null, ct).ConfigureAwait(false);
        var txs = r.GetProperty("transactions");
        if (txs.GetArrayLength() == 0) return null;
        var t = txs[0];
        var d = t.GetProperty("description");
        bool computeOk = d.TryGetProperty("compute_ph", out var cp) && cp.TryGetProperty("success", out var s) && s.GetBoolean();
        bool actionOk = !d.TryGetProperty("action", out var a) || !a.TryGetProperty("success", out var asx) || asx.GetBoolean();
        bool ok = !d.GetProperty("aborted").GetBoolean() && computeOk && actionOk;
        ulong block = t.TryGetProperty("mc_block_seqno", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetUInt64() : 0;
        ulong fees = ulong.Parse(t.GetProperty("total_fees").GetString()!, CultureInfo.InvariantCulture);
        string? error = ok ? null : cp.ValueKind == JsonValueKind.Object && cp.TryGetProperty("exit_code", out var ec) ? $"exit code {ec.GetInt32()}" : "aborted";
        return new TxReceipt(hash, block, ok, fees, error);
    }

    private async Task<JsonElement> V2Async(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var r = await SendAsync(method, "/v2/" + path, body, ct).ConfigureAwait(false);
        if (!r.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new RpcException(r.TryGetProperty("code", out var c) ? c.GetInt32() : -1, r.TryGetProperty("error", out var e) ? e.GetString() ?? "error" : r.GetRawText(), method: path);
        return r.GetProperty("result");
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, _baseUrl + path);
            if (_apiKey is not null) request.Headers.Add("X-API-Key", _apiKey);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType()));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1100 * (attempt + 1)), ct).ConfigureAwait(false);
                continue;
            }
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(text);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new RpcException((int)response.StatusCode, text.Length > 300 ? text[..300] : text, method: path);
            }
            if (!response.IsSuccessStatusCode && !(root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out _)))
            {
                string msg = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) ? e.GetString() ?? text : text;
                throw new RpcException((int)response.StatusCode, msg, method: path);
            }
            return root;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
