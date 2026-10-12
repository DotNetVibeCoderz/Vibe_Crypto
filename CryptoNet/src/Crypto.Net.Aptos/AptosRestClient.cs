using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Aptos;

/// <summary>Ledger summary returned by the node's index endpoint.</summary>
public sealed record AptosLedgerInfo(byte ChainId, ulong LedgerVersion, ulong BlockHeight, ulong TimestampMicros, string NodeRole);

/// <summary>Sequence number and authentication key of an account (both default for accounts not yet on chain).</summary>
public sealed record AptosAccountInfo(ulong SequenceNumber, string AuthenticationKey);

/// <summary>Result of <c>/transactions/simulate</c>.</summary>
public sealed record AptosSimulation(bool Success, string VmStatus, ulong GasUsed, ulong GasUnitPrice);

/// <summary>Aptos node REST API client (<c>/v1</c>).</summary>
public sealed class AptosRestClient : IChainClient
{
    private const string BcsSignedTransaction = "application/x.aptos.signed_transaction+bcs";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    /// <param name="apiKey">Optional Aptos Build API key, sent as a bearer token for higher rate limits.</param>
    public AptosRestClient(AptosChain network, string? baseUrl = null, HttpClient? httpClient = null, string? apiKey = null)
    {
        Network = network ?? throw new ArgumentNullException(nameof(network));
        _baseUrl = (baseUrl ?? network.DefaultRestUrl).TrimEnd('/');
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _apiKey = apiKey;
    }

    public AptosChain Network { get; }
    public IChain Chain => Network;

    public async ValueTask<AptosLedgerInfo> GetLedgerInfoAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, "/", null, ct).ConfigureAwait(false);
        return new AptosLedgerInfo(r.GetProperty("chain_id").GetByte(), U64(r, "ledger_version"), U64(r, "block_height"), U64(r, "ledger_timestamp"), r.GetProperty("node_role").GetString()!);
    }

    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default) => (await GetLedgerInfoAsync(ct).ConfigureAwait(false)).BlockHeight;

    public async ValueTask<AptosAccountInfo> GetAccountAsync(string address, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/accounts/{AptosAddress.Normalize(address)}", null, ct).ConfigureAwait(false);
        return new AptosAccountInfo(U64(r, "sequence_number"), r.GetProperty("authentication_key").GetString()!);
    }

    public async ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default) =>
        await GetBalanceAsync(address.Value, ct: ct).ConfigureAwait(false);

    /// <summary>
    /// Balance of a coin type (<c>0x1::aptos_coin::AptosCoin</c> by default) or fungible asset metadata address,
    /// counting both the coin store and the primary fungible store. Non-APT assets are returned with 0 decimals.
    /// </summary>
    public async ValueTask<Amount> GetBalanceAsync(string address, string assetType = "0x1::aptos_coin::AptosCoin", CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/accounts/{AptosAddress.Normalize(address)}/balance/{assetType}", null, ct).ConfigureAwait(false);
        ulong value = r.ValueKind == JsonValueKind.String ? ulong.Parse(r.GetString()!, CultureInfo.InvariantCulture) : r.GetUInt64();
        bool apt = assetType is "0x1::aptos_coin::AptosCoin" or "0xa" || assetType == AptosAddress.Normalize("0xa");
        return new Amount(value, apt ? Network.Decimals : 0);
    }

    /// <summary>A Move resource of an account, or null when the account does not hold it.</summary>
    public async ValueTask<JsonElement?> GetAccountResourceAsync(string address, string resourceType, CancellationToken ct = default)
    {
        try
        {
            var r = await SendAsync(HttpMethod.Get, $"/accounts/{AptosAddress.Normalize(address)}/resource/{resourceType}", null, ct).ConfigureAwait(false);
            return r.GetProperty("data");
        }
        catch (RpcException ex) when (ex.Code == 404)
        {
            return null;
        }
    }

    /// <summary>Calls a <c>#[view]</c> function; <paramref name="arguments"/> use the JSON encoding (u64 as strings, addresses as hex).</summary>
    public async ValueTask<JsonElement> ViewAsync(string function, IReadOnlyList<string>? typeArguments = null, IReadOnlyList<object>? arguments = null, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "/view", new { function, type_arguments = typeArguments ?? [], arguments = arguments ?? [] }, ct).ConfigureAwait(false);

    /// <summary>The node's gas unit price estimate (octas).</summary>
    public async ValueTask<ulong> EstimateGasPriceAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, "/estimate_gas_price", null, ct).ConfigureAwait(false);
        return r.GetProperty("gas_estimate").GetUInt64();
    }

    /// <summary>Fills in the sequence number, chain id, gas price and expiry for a payload from <paramref name="sender"/>.</summary>
    public async ValueTask<AptosRawTransaction> BuildTransactionAsync(string sender, AptosEntryFunction payload, ulong maxGasAmount = 200_000, ulong? gasUnitPrice = null, TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        var account = await GetAccountAsync(sender, ct).ConfigureAwait(false);
        ulong price = gasUnitPrice ?? await EstimateGasPriceAsync(ct).ConfigureAwait(false);
        return new AptosRawTransaction
        {
            Sender = sender,
            SequenceNumber = account.SequenceNumber,
            Payload = payload,
            MaxGasAmount = maxGasAmount,
            GasUnitPrice = price,
            ExpirationTimestampSecs = AptosRawTransaction.ExpiresIn(lifetime ?? TimeSpan.FromMinutes(10)),
            ChainId = Network.ChainId,
        };
    }

    /// <summary>Simulates a transaction without a signature to check that it succeeds and see the gas it uses.</summary>
    public async ValueTask<AptosSimulation> SimulateAsync(AptosRawTransaction transaction, ReadOnlyMemory<byte> publicKey, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "/transactions/simulate", transaction.ForSimulation(publicKey.Span), ct).ConfigureAwait(false);
        var t = r[0];
        return new AptosSimulation(t.GetProperty("success").GetBoolean(), t.GetProperty("vm_status").GetString()!, U64(t, "gas_used"), U64(t, "gas_unit_price"));
    }

    /// <summary>Submits a BCS <c>SignedTransaction</c>; returns its hash.</summary>
    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "/transactions", signedTxBytes.ToArray(), ct).ConfigureAwait(false);
        return new TxHash(r.GetProperty("hash").GetString()!);
    }

    /// <summary>Builds, signs and submits <paramref name="payload"/> from <paramref name="account"/>.</summary>
    public async ValueTask<TxHash> SignAndSendAsync(AptosAccount account, AptosEntryFunction payload, ulong maxGasAmount = 200_000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var tx = await BuildTransactionAsync(account.Address.Value, payload, maxGasAmount, ct: ct).ConfigureAwait(false);
        return await SendRawTransactionAsync(tx.Sign(account), ct).ConfigureAwait(false);
    }

    /// <summary>Sends APT with <c>0x1::aptos_account::transfer</c> (creates the recipient if needed).</summary>
    public ValueTask<TxHash> TransferAsync(AptosAccount account, string to, Amount amount, CancellationToken ct = default) =>
        SignAndSendAsync(account, AptosEntryFunction.Transfer(to, (ulong)amount.WithDecimals(8).BaseUnits), ct: ct);

    /// <summary>The transaction as JSON (pending or committed), or null when the node does not know it.</summary>
    public async ValueTask<JsonElement?> GetTransactionAsync(string hash, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync(HttpMethod.Get, $"/transactions/by_hash/{hash}", null, ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code == 404)
        {
            return null;
        }
    }

    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default)
    {
        var t = await GetTransactionAsync(hash.Value, ct).ConfigureAwait(false);
        if (t is not { } tx || tx.GetProperty("type").GetString() == "pending_transaction") return null;
        bool ok = tx.GetProperty("success").GetBoolean();
        var block = await SendAsync(HttpMethod.Get, $"/blocks/by_version/{U64(tx, "version")}", null, ct).ConfigureAwait(false);
        return new TxReceipt(hash, U64(block, "block_height"), ok, U64(tx, "gas_used"), ok ? null : tx.GetProperty("vm_status").GetString());
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (body is byte[] bcs)
        {
            request.Content = new ByteArrayContent(bcs);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(BcsSignedTransaction);
        }
        else if (body is not null)
        {
            // The node rejects chunked bodies, so send JSON with a Content-Length.
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
            string? code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error_code", out var c) ? c.GetString() : null;
            throw new RpcException((int)response.StatusCode, msg, code, path);
        }
        return root;
    }

    private static ulong U64(JsonElement e, string name)
    {
        var v = e.GetProperty(name);
        return v.ValueKind == JsonValueKind.String ? ulong.Parse(v.GetString()!, CultureInfo.InvariantCulture) : v.GetUInt64();
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
