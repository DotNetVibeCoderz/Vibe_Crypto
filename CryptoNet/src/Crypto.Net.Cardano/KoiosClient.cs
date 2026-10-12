using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Cardano;

/// <summary>Chain tip: block height, absolute slot and epoch.</summary>
public sealed record CardanoTip(ulong BlockHeight, ulong Slot, uint Epoch);

/// <summary>
/// Cardano client over Koios, the community REST API (no key required; an optional bearer token raises the limits).
/// Reads tips, UTxOs and protocol parameters, builds transfers with UTxO selection and exact fees, and submits.
/// </summary>
public sealed class KoiosClient : IChainClient
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;
    private readonly string? _apiToken;

    public KoiosClient(CardanoChain network, string? baseUrl = null, HttpClient? httpClient = null, string? apiToken = null)
    {
        Network = network ?? throw new ArgumentNullException(nameof(network));
        _baseUrl = (baseUrl ?? network.DefaultApiUrl).TrimEnd('/');
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _apiToken = apiToken;
    }

    public CardanoChain Network { get; }
    public IChain Chain => Network;

    public async ValueTask<CardanoTip> GetTipAsync(CancellationToken ct = default)
    {
        var t = (await SendAsync(HttpMethod.Get, "/tip", null, ct).ConfigureAwait(false))[0];
        return new CardanoTip(t.GetProperty("block_no").GetUInt64(), t.GetProperty("abs_slot").GetUInt64(), t.GetProperty("epoch_no").GetUInt32());
    }

    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default) => (await GetTipAsync(ct).ConfigureAwait(false)).BlockHeight;

    public async ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "/address_info", new Dictionary<string, object> { ["_addresses"] = new[] { address.Value } }, ct).ConfigureAwait(false);
        ulong balance = r.GetArrayLength() == 0 ? 0 : ParseU64(r[0].GetProperty("balance"));
        return new Amount(balance, 6);
    }

    /// <summary>Unspent outputs at <paramref name="address"/>, with their native assets.</summary>
    public async ValueTask<IReadOnlyList<CardanoUtxo>> GetUtxosAsync(string address, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "/address_info", new Dictionary<string, object> { ["_addresses"] = new[] { address } }, ct).ConfigureAwait(false);
        if (r.GetArrayLength() == 0) return [];
        return r[0].GetProperty("utxo_set").EnumerateArray().Select(u => new CardanoUtxo(
            new CardanoInput(u.GetProperty("tx_hash").GetString()!, u.GetProperty("tx_index").GetUInt32()),
            ParseU64(u.GetProperty("value")),
            u.TryGetProperty("asset_list", out var assets) && assets.ValueKind == JsonValueKind.Array
                ? assets.EnumerateArray().Select(a => new CardanoAsset(a.GetProperty("policy_id").GetString()!, a.GetProperty("asset_name").GetString() ?? "", ParseU64(a.GetProperty("quantity")))).ToList()
                : [])).ToList();
    }

    public async ValueTask<CardanoProtocolParameters> GetProtocolParametersAsync(CancellationToken ct = default)
    {
        var p = (await SendAsync(HttpMethod.Get, "/epoch_params?limit=1&order=epoch_no.desc&select=epoch_no,min_fee_a,min_fee_b,coins_per_utxo_size,max_tx_size", null, ct).ConfigureAwait(false))[0];
        return new CardanoProtocolParameters(ParseU64(p.GetProperty("min_fee_a")), ParseU64(p.GetProperty("min_fee_b")), ParseU64(p.GetProperty("coins_per_utxo_size")), p.GetProperty("max_tx_size").GetUInt32());
    }

    /// <summary>
    /// Builds a transfer from <paramref name="account"/>: selects UTxOs (largest first), returns change and any native
    /// assets to the sender, and sets the exact minimum fee for the signed size. The result is unsigned.
    /// </summary>
    public async ValueTask<CardanoTransaction> BuildTransferAsync(CardanoAccount account, IReadOnlyList<CardanoOutput> outputs, string? message = null, ulong ttlSlots = 7200, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var utxos = await GetUtxosAsync(account.Address.Value, ct).ConfigureAwait(false);
        var pp = await GetProtocolParametersAsync(ct).ConfigureAwait(false);
        var tip = await GetTipAsync(ct).ConfigureAwait(false);
        return BuildTransfer(account, utxos, outputs, pp, tip.Slot + ttlSlots, message);
    }

    /// <summary>Offline version of <see cref="BuildTransferAsync"/> over known UTxOs and parameters.</summary>
    public static CardanoTransaction BuildTransfer(CardanoAccount account, IReadOnlyList<CardanoUtxo> utxos, IReadOnlyList<CardanoOutput> outputs, CardanoProtocolParameters pp, ulong? ttl, string? message = null)
    {
        foreach (var o in outputs)
        {
            ulong min = o.MinLovelace(pp.CoinsPerUtxoByte);
            if (o.Lovelace < min) throw new InvalidOperationException($"Output to {o.Address} needs at least {min} lovelace");
        }
        ulong sendLovelace = outputs.Aggregate(0UL, (s, o) => checked(s + o.Lovelace));
        var sendAssets = outputs.SelectMany(o => o.Assets ?? []).GroupBy(a => a.Unit).ToDictionary(g => g.Key, g => g.Aggregate(0UL, (s, a) => checked(s + a.Quantity)));

        // Inputs carrying the assets being sent first, then the largest ADA amounts.
        var ordered = utxos.OrderByDescending(u => u.Assets.Any(a => sendAssets.ContainsKey(a.Unit))).ThenByDescending(u => u.Lovelace).ToList();
        var selected = new List<CardanoUtxo>();
        byte[]? aux = message is null ? null : CardanoMetadata.Message(message);
        foreach (var utxo in ordered)
        {
            selected.Add(utxo);
            var tx = TryComplete(account, selected, outputs, sendLovelace, sendAssets, pp, ttl, aux);
            if (tx is not null) return tx;
        }
        throw new InvalidOperationException("Insufficient funds: the UTxOs cannot cover the outputs, the fee and the change");
    }

    private static CardanoTransaction? TryComplete(CardanoAccount account, List<CardanoUtxo> selected, IReadOnlyList<CardanoOutput> outputs, ulong sendLovelace,
        Dictionary<string, ulong> sendAssets, CardanoProtocolParameters pp, ulong? ttl, byte[]? aux)
    {
        ulong inLovelace = selected.Aggregate(0UL, (s, u) => checked(s + u.Lovelace));
        var change = selected.SelectMany(u => u.Assets).GroupBy(a => a.Unit).ToDictionary(g => g.Key, g => (g.First(), Total: g.Aggregate(0UL, (s, a) => checked(s + a.Quantity))));
        foreach (var (unit, qty) in sendAssets)
        {
            if (!change.TryGetValue(unit, out var have) || have.Total < qty) return null;
            change[unit] = (have.Item1, have.Total - qty);
        }
        var changeAssets = change.Values.Where(v => v.Total > 0).Select(v => v.Item1 with { Quantity = v.Total }).ToList();
        if (inLovelace <= sendLovelace) return null;

        var tx = new CardanoTransaction { Inputs = selected.Select(u => u.Input).ToList(), Ttl = ttl, AuxiliaryData = aux };
        ulong fee = pp.MinFee(0);
        for (int round = 0; round < 4; round++)
        {
            if (inLovelace < sendLovelace + fee) return null;
            ulong rest = inLovelace - sendLovelace - fee;
            var changeOutput = new CardanoOutput(account.Address.Value, rest, changeAssets);
            bool withChange = rest > 0 || changeAssets.Count > 0;
            if (withChange && rest < changeOutput.MinLovelace(pp.CoinsPerUtxoByte))
            {
                if (changeAssets.Count > 0) return null;
                withChange = false;   // dust: leave it to the fee
            }
            tx.Outputs.Clear();
            tx.Outputs.AddRange(outputs);
            if (withChange) tx.Outputs.Add(changeOutput);
            tx.Fee = withChange ? fee : inLovelace - sendLovelace;
            int size = tx.Sign(account).Length;
            if (size > pp.MaxTxSize) throw new InvalidOperationException($"Transaction size {size} exceeds the limit of {pp.MaxTxSize} bytes");
            ulong needed = pp.MinFee(size);
            if (tx.Fee >= needed && (!withChange || tx.Fee == needed)) return tx;
            fee = needed;
        }
        return null;
    }

    /// <summary>Builds, signs and submits a transfer of <paramref name="amount"/> ADA (optionally with a CIP-20 message).</summary>
    public async ValueTask<TxHash> TransferAsync(CardanoAccount account, string to, Amount amount, string? message = null, CancellationToken ct = default)
    {
        var tx = await BuildTransferAsync(account, [new CardanoOutput(to, (ulong)amount.WithDecimals(6).BaseUnits)], message, ct: ct).ConfigureAwait(false);
        return await SendRawTransactionAsync(tx.Sign(account), ct).ConfigureAwait(false);
    }

    /// <summary>Submits a signed transaction (CBOR); returns its id.</summary>
    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/submittx") { Content = new ByteArrayContent(signedTxBytes.ToArray()) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/cbor");
        if (_apiToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RpcException((int)response.StatusCode, text.Length > 500 ? text[..500] : text, method: "submittx");
        return new TxHash(CardanoTransaction.IdOf(signedTxBytes.Span));
    }

    /// <summary>Receipt once the transaction is in a block; null while it is pending or unknown.</summary>
    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "/tx_info", new Dictionary<string, object> { ["_tx_hashes"] = new[] { hash.Value }, ["_inputs"] = false, ["_metadata"] = false, ["_assets"] = false, ["_withdrawals"] = false, ["_certs"] = false, ["_scripts"] = false, ["_bytecode"] = false }, ct).ConfigureAwait(false);
        if (r.GetArrayLength() == 0) return null;
        var t = r[0];
        if (!t.TryGetProperty("block_height", out var bh) || bh.ValueKind != JsonValueKind.Number) return null;
        bool valid = !t.TryGetProperty("valid_contract", out var vc) || vc.ValueKind != JsonValueKind.False;
        return new TxReceipt(hash, bh.GetUInt64(), valid, ParseU64(t.GetProperty("fee")), valid ? null : "Script validation failed (collateral taken)");
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        if (_apiToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType()));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
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
        if (!response.IsSuccessStatusCode)
        {
            string msg = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var m) ? m.GetString() ?? text : text;
            throw new RpcException((int)response.StatusCode, msg, method: path);
        }
        return root;
    }

    private static ulong ParseU64(JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? ulong.Parse(v.GetString()!, CultureInfo.InvariantCulture) : v.GetUInt64();

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
