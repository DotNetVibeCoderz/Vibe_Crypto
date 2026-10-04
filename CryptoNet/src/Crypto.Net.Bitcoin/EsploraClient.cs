using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Bitcoin;

/// <summary>
/// Client for the Esplora REST API (Blockstream, mempool.space, self-hosted electrs).
/// </summary>
public sealed class EsploraClient : IChainClient
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;

    public EsploraClient(BitcoinNetwork network, string? baseUrl = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        Network = network;
        _baseUrl = (baseUrl ?? network.DefaultEsploraUrl).TrimEnd('/');
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public BitcoinNetwork Network { get; }
    public IChain Chain => Network;

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{_baseUrl}{path}", ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RpcException((int)response.StatusCode, body.Trim(), method: "GET " + path);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>Confirmed + mempool balance.</summary>
    public async ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default) =>
        Amount.FromSatoshi(await GetBalanceSatsAsync(address.Value, includeMempool: true, ct));

    public async ValueTask<long> GetBalanceSatsAsync(string address, bool includeMempool = true, CancellationToken ct = default)
    {
        var data = await GetJsonAsync($"/address/{address}", ct);
        long Sum(string section) => data.TryGetProperty(section, out var s)
            ? s.GetProperty("funded_txo_sum").GetInt64() - s.GetProperty("spent_txo_sum").GetInt64()
            : 0;
        return Sum("chain_stats") + (includeMempool ? Sum("mempool_stats") : 0);
    }

    /// <summary>Unspent outputs of <paramref name="address"/> (confirmed and unconfirmed).</summary>
    public async ValueTask<IReadOnlyList<BitcoinUtxo>> GetUtxosAsync(string address, CancellationToken ct = default)
    {
        byte[] script = BitcoinAddress.GetScriptPubKey(address, Network);
        var data = await GetJsonAsync($"/address/{address}/utxo", ct);
        return data.EnumerateArray()
            .Select(u => new BitcoinUtxo(u.GetProperty("txid").GetString()!, u.GetProperty("vout").GetUInt32(), u.GetProperty("value").GetInt64(), script))
            .ToList();
    }

    /// <summary>Fee estimates in sat/vB keyed by confirmation target (blocks).</summary>
    public async ValueTask<IReadOnlyDictionary<int, decimal>> GetFeeEstimatesAsync(CancellationToken ct = default)
    {
        var data = await GetJsonAsync("/fee-estimates", ct);
        return data.EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetDecimal());
    }

    /// <summary>Fee rate for confirmation within <paramref name="targetBlocks"/> (minimum 1 sat/vB).</summary>
    public async ValueTask<decimal> GetFeeRateAsync(int targetBlocks = 6, CancellationToken ct = default)
    {
        var estimates = await GetFeeEstimatesAsync(ct);
        var best = estimates.Where(e => e.Key <= targetBlocks).OrderByDescending(e => e.Key).Select(e => e.Value).FirstOrDefault();
        if (best == 0 && estimates.Count > 0) best = estimates.OrderBy(e => e.Key).First().Value;
        return Math.Max(1m, Math.Round(best, 2));
    }

    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default)
    {
        string response = await _http.GetStringAsync($"{_baseUrl}/blocks/tip/height", ct).ConfigureAwait(false);
        return ulong.Parse(response.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        using var content = new StringContent(Convert.ToHexStringLower(signedTxBytes.Span), Encoding.ASCII, "text/plain");
        using var response = await _http.PostAsync($"{_baseUrl}/tx", content, ct).ConfigureAwait(false);
        string body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        if (!response.IsSuccessStatusCode) throw new RpcException((int)response.StatusCode, body, method: "POST /tx");
        return new TxHash(body);
    }

    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default)
    {
        JsonElement status;
        try
        {
            status = await GetJsonAsync($"/tx/{hash.Value}/status", ct);
        }
        catch (RpcException ex) when (ex.Code == 404)
        {
            return null;
        }
        if (!status.GetProperty("confirmed").GetBoolean()) return null;
        return new TxReceipt(hash, status.GetProperty("block_height").GetUInt64(), true);
    }

    /// <summary>
    /// Builds, signs and broadcasts a payment from the single-key <paramref name="account"/> to <paramref name="to"/>,
    /// sending change back to the account. Uses the network fee estimate unless <paramref name="feeRateSatPerVb"/> is given.
    /// </summary>
    public async ValueTask<TxHash> TransferAsync(BitcoinAccount account, string to, long amountSats, decimal? feeRateSatPerVb = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var tx = await BuildTransferAsync(account, to, amountSats, feeRateSatPerVb, ct);
        return await SendRawTransactionAsync(tx.Serialize(), ct);
    }

    /// <summary>Same as <see cref="TransferAsync"/> but returns the signed transaction without broadcasting it.</summary>
    public async ValueTask<BitcoinTransaction> BuildTransferAsync(BitcoinAccount account, string to, long amountSats, decimal? feeRateSatPerVb = null, CancellationToken ct = default)
    {
        byte[] recipientScript = BitcoinAddress.GetScriptPubKey(to, Network);
        var utxos = await GetUtxosAsync(account.Address.Value, ct);
        decimal feeRate = feeRateSatPerVb ?? await GetFeeRateAsync(ct: ct);
        var selection = CoinSelector.Select(utxos, amountSats, feeRate, account.Type);

        var tx = new BitcoinTransaction();
        foreach (var u in selection.Inputs) tx.AddInput(u.TxId, u.Vout);
        tx.AddOutput(recipientScript, amountSats);
        if (selection.ChangeSats > 0) tx.AddOutput(account.ScriptPubKey, selection.ChangeSats);
        account.Sign(tx, selection.Inputs);
        return tx;
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
