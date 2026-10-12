using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Sui;

/// <summary>A coin object and its balance.</summary>
public sealed record SuiCoin(SuiObjectRef Ref, string CoinType, ulong Balance);

/// <summary>Outcome of a simulated or executed transaction.</summary>
public sealed record SuiEffects(string Digest, bool Success, string? Error, ulong ComputationCost, ulong StorageCost, ulong StorageRebate, ulong? Checkpoint)
{
    /// <summary>Net gas charged (computation + storage − rebate), never below zero.</summary>
    public ulong NetGas => ComputationCost + StorageCost > StorageRebate ? ComputationCost + StorageCost - StorageRebate : 0;
}

/// <summary>
/// Sui client over the GraphQL API (Sui's public fullnodes no longer serve JSON-RPC). Reads balances, coins and
/// objects, simulates and executes transactions.
/// </summary>
public sealed class SuiGraphQlClient : IChainClient
{
    public const string SuiCoinType = "0x2::sui::SUI";
    private const string EffectsFields = "digest status executionError { message } checkpoint { sequenceNumber } gasEffects { gasSummary { computationCost storageCost storageRebate } }";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _url;

    public SuiGraphQlClient(SuiChain network, string? url = null, HttpClient? httpClient = null)
    {
        Network = network ?? throw new ArgumentNullException(nameof(network));
        _url = url ?? network.DefaultGraphQlUrl;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public SuiChain Network { get; }
    public IChain Chain => Network;

    /// <summary>Runs a GraphQL query and returns its <c>data</c>; GraphQL errors become <see cref="RpcException"/>.</summary>
    public async Task<JsonElement> QueryAsync(string query, object? variables = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { query, variables })),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
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
            throw new RpcException((int)response.StatusCode, text.Length > 300 ? text[..300] : text, method: "graphql");
        }
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            string? code = first.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var c) ? c.GetString() : null;
            throw new RpcException((int)response.StatusCode, first.GetProperty("message").GetString() ?? text, code, "graphql");
        }
        if (!response.IsSuccessStatusCode) throw new RpcException((int)response.StatusCode, text, method: "graphql");
        return root.GetProperty("data");
    }

    /// <summary>Latest checkpoint sequence number (Sui's block height).</summary>
    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default)
    {
        var d = await QueryAsync("{ checkpoint { sequenceNumber } }", null, ct).ConfigureAwait(false);
        return d.GetProperty("checkpoint").GetProperty("sequenceNumber").GetUInt64();
    }

    public async ValueTask<string> GetChainIdentifierAsync(CancellationToken ct = default) =>
        (await QueryAsync("{ chainIdentifier }", null, ct).ConfigureAwait(false)).GetProperty("chainIdentifier").GetString()!;

    /// <summary>The current epoch's reference gas price (MIST per gas unit).</summary>
    public async ValueTask<ulong> GetReferenceGasPriceAsync(CancellationToken ct = default)
    {
        var d = await QueryAsync("{ epoch { referenceGasPrice } }", null, ct).ConfigureAwait(false);
        return ParseU64(d.GetProperty("epoch").GetProperty("referenceGasPrice"));
    }

    public async ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default) =>
        await GetBalanceAsync(address.Value, SuiCoinType, ct).ConfigureAwait(false);

    /// <summary>Total balance of <paramref name="coinType"/> (coin objects plus address balance). Non-SUI coins use 0 decimals.</summary>
    public async ValueTask<Amount> GetBalanceAsync(string address, string coinType, CancellationToken ct = default)
    {
        var d = await QueryAsync("query($a: SuiAddress!, $t: String!) { address(address: $a) { balance(coinType: $t) { totalBalance } } }",
            new { a = SuiAddress.Normalize(address), t = coinType }, ct).ConfigureAwait(false);
        ulong total = d.GetProperty("address") is { ValueKind: JsonValueKind.Object } a && a.GetProperty("balance") is { ValueKind: JsonValueKind.Object } b
            ? ParseU64(b.GetProperty("totalBalance")) : 0;
        return new Amount(total, coinType == SuiCoinType ? Network.Decimals : 0);
    }

    /// <summary>Coin objects of <paramref name="coinType"/> owned by <paramref name="owner"/> (first page, up to 50).</summary>
    public async ValueTask<IReadOnlyList<SuiCoin>> GetCoinsAsync(string owner, string coinType = SuiCoinType, CancellationToken ct = default)
    {
        string type = $"0x2::coin::Coin<{coinType}>";
        var d = await QueryAsync("query($a: SuiAddress!, $t: String!) { address(address: $a) { objects(first: 50, filter: { type: $t }) { nodes { address version digest contents { json } } } } }",
            new { a = SuiAddress.Normalize(owner), t = type }, ct).ConfigureAwait(false);
        if (d.GetProperty("address").ValueKind != JsonValueKind.Object) return [];
        return d.GetProperty("address").GetProperty("objects").GetProperty("nodes").EnumerateArray()
            .Select(n => new SuiCoin(
                new SuiObjectRef(n.GetProperty("address").GetString()!, n.GetProperty("version").GetUInt64(), n.GetProperty("digest").GetString()!),
                coinType,
                ParseU64(n.GetProperty("contents").GetProperty("json").GetProperty("balance"))))
            .ToList();
    }

    /// <summary>The current reference (version and digest) of an object, or null if it does not exist.</summary>
    public async ValueTask<SuiObjectRef?> GetObjectRefAsync(string objectId, CancellationToken ct = default)
    {
        var d = await QueryAsync("query($a: SuiAddress!) { object(address: $a) { address version digest } }", new { a = SuiAddress.Normalize(objectId) }, ct).ConfigureAwait(false);
        var o = d.GetProperty("object");
        return o.ValueKind == JsonValueKind.Object
            ? new SuiObjectRef(o.GetProperty("address").GetString()!, o.GetProperty("version").GetUInt64(), o.GetProperty("digest").GetString()!)
            : null;
    }

    /// <summary>
    /// Completes a transaction: uses the sender's SUI coins (largest first, up to 16) for gas, the reference gas price,
    /// and a budget from a dry run plus 20% (at least <paramref name="minBudget"/>).
    /// </summary>
    public async ValueTask<SuiTransactionData> BuildAsync(SuiTransactionBuilder builder, ulong minBudget = 2_000_000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var coins = await GetCoinsAsync(builder.Sender, ct: ct).ConfigureAwait(false);
        if (coins.Count == 0) throw new InvalidOperationException($"{builder.Sender} has no SUI coins to pay for gas");
        var top = coins.OrderByDescending(c => c.Balance).Take(16).ToList();
        var payment = top.Select(c => c.Ref).ToList();
        ulong price = await GetReferenceGasPriceAsync(ct).ConfigureAwait(false);
        ulong cap = Math.Min(50_000_000_000UL, top.Aggregate(0UL, (sum, c) => sum + c.Balance));

        // Dry-run with a modest budget first: the budget is reserved from the gas coins, so probing with the whole
        // balance would starve commands that split the gas coin. Retry with the full cap only if gas ran out.
        var sim = await SimulateAsync(builder.Build(payment, price, Math.Min(cap, 50_000_000)).Serialize(), ct).ConfigureAwait(false);
        if (!sim.Success && cap > 50_000_000 && (sim.Error?.Contains("InsufficientGas", StringComparison.OrdinalIgnoreCase) ?? false))
            sim = await SimulateAsync(builder.Build(payment, price, cap).Serialize(), ct).ConfigureAwait(false);
        if (!sim.Success) throw new InvalidOperationException($"Transaction would fail: {sim.Error}");
        ulong budget = Math.Max(minBudget, (sim.ComputationCost + sim.StorageCost) * 12 / 10);
        return builder.Build(payment, price, budget);
    }

    /// <summary>Dry-runs transaction data BCS (no signature needed).</summary>
    public async ValueTask<SuiEffects> SimulateAsync(byte[] transactionData, CancellationToken ct = default)
    {
        var d = await QueryAsync($"query($t: JSON!) {{ simulateTransaction(transaction: $t) {{ effects {{ {EffectsFields} }} }} }}",
            new { t = new { bcs = new { value = System.Convert.ToBase64String(transactionData) } } }, ct).ConfigureAwait(false);
        return ParseEffects(d.GetProperty("simulateTransaction").GetProperty("effects"));
    }

    /// <summary>Executes transaction data with its serialized signatures and waits for the effects.</summary>
    public async ValueTask<SuiEffects> ExecuteAsync(byte[] transactionData, IReadOnlyList<byte[]> signatures, CancellationToken ct = default)
    {
        var d = await QueryAsync($"mutation($t: Base64!, $s: [Base64!]!) {{ executeTransaction(transactionDataBcs: $t, signatures: $s) {{ effects {{ {EffectsFields} }} }} }}",
            new { t = System.Convert.ToBase64String(transactionData), s = signatures.Select(System.Convert.ToBase64String).ToArray() }, ct).ConfigureAwait(false);
        return ParseEffects(d.GetProperty("executeTransaction").GetProperty("effects"));
    }

    /// <summary>Submits BCS <c>SenderSignedData</c> (see <see cref="SuiSignedTransaction"/>); returns the digest.</summary>
    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        var (data, signatures) = SuiSignedTransaction.Decode(signedTxBytes.Span);
        var effects = await ExecuteAsync(data, signatures, ct).ConfigureAwait(false);
        return new TxHash(effects.Digest);
    }

    /// <summary>Builds (gas, budget), signs and executes the transaction.</summary>
    public async ValueTask<SuiEffects> SignAndExecuteAsync(SuiAccount account, SuiTransactionBuilder builder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var data = await BuildAsync(builder, ct: ct).ConfigureAwait(false);
        byte[] bcs = data.Serialize();
        return await ExecuteAsync(bcs, [account.SignTransaction(bcs)], ct).ConfigureAwait(false);
    }

    /// <summary>Sends <paramref name="amount"/> SUI to <paramref name="recipient"/>.</summary>
    public ValueTask<SuiEffects> TransferAsync(SuiAccount account, string recipient, Amount amount, CancellationToken ct = default) =>
        SignAndExecuteAsync(account, new SuiTransactionBuilder(account.Address.Value).TransferSui(recipient, (ulong)amount.WithDecimals(9).BaseUnits), ct);

    /// <summary>Effects of an executed transaction, or null if the indexer does not know it (yet).</summary>
    public async ValueTask<SuiEffects?> GetEffectsAsync(string digest, CancellationToken ct = default)
    {
        var d = await QueryAsync($"query($d: String!) {{ transactionEffects(digest: $d) {{ {EffectsFields} }} }}", new { d = digest }, ct).ConfigureAwait(false);
        var e = d.GetProperty("transactionEffects");
        return e.ValueKind == JsonValueKind.Object ? ParseEffects(e) : null;
    }

    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default)
    {
        var e = await GetEffectsAsync(hash.Value, ct).ConfigureAwait(false);
        if (e?.Checkpoint is not { } checkpoint) return null;
        return new TxReceipt(hash, checkpoint, e.Success, e.NetGas, e.Error);
    }

    private static SuiEffects ParseEffects(JsonElement e)
    {
        var summary = e.GetProperty("gasEffects").GetProperty("gasSummary");
        string? error = e.TryGetProperty("executionError", out var x) && x.ValueKind == JsonValueKind.Object ? x.GetProperty("message").GetString() : null;
        ulong? checkpoint = e.TryGetProperty("checkpoint", out var c) && c.ValueKind == JsonValueKind.Object ? c.GetProperty("sequenceNumber").GetUInt64() : null;
        return new SuiEffects(
            e.GetProperty("digest").GetString()!,
            e.GetProperty("status").GetString() == "SUCCESS",
            error,
            ParseU64(summary.GetProperty("computationCost")),
            ParseU64(summary.GetProperty("storageCost")),
            ParseU64(summary.GetProperty("storageRebate")),
            checkpoint);
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
