using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Cosmos;

/// <summary>Cosmos SDK REST (gRPC-gateway / LCD) client.</summary>
public sealed class CosmosRestClient : IChainClient
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;

    public CosmosRestClient(CosmosChain chain, string? baseUrl = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        Network = chain;
        _baseUrl = (baseUrl ?? chain.DefaultRestUrl).TrimEnd('/');
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public CosmosChain Network { get; }
    public IChain Chain => Network;

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        if (body is not null) request.Content = JsonContent.Create(body);
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
            long code = root.TryGetProperty("code", out var c) && c.TryGetInt64(out long cv) ? cv : (int)response.StatusCode;
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? text : text;
            throw new RpcException(code, msg, method: path);
        }
        return root;
    }

    public async ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default) =>
        await GetBalanceAsync(address.Value, Network.Denom, ct);

    public async ValueTask<Amount> GetBalanceAsync(string address, string denom, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/cosmos/bank/v1beta1/balances/{address}/by_denom?denom={Uri.EscapeDataString(denom)}", null, ct);
        string amount = r.GetProperty("balance").GetProperty("amount").GetString() ?? "0";
        return new Amount(BigInteger.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), denom == Network.Denom ? Network.Decimals : 0);
    }

    /// <summary>All balances (denom → base units).</summary>
    public async ValueTask<IReadOnlyList<Coin>> GetAllBalancesAsync(string address, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"/cosmos/bank/v1beta1/balances/{address}", null, ct);
        return r.GetProperty("balances").EnumerateArray()
            .Select(b => new Coin(b.GetProperty("denom").GetString()!, BigInteger.Parse(b.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
    }

    /// <summary>Account number and sequence, needed for signing. Returns (0, 0) for accounts never seen on-chain.</summary>
    public async ValueTask<(ulong AccountNumber, ulong Sequence)> GetAccountAsync(string address, CancellationToken ct = default)
    {
        JsonElement r;
        try
        {
            r = await SendAsync(HttpMethod.Get, $"/cosmos/auth/v1beta1/accounts/{address}", null, ct);
        }
        catch (RpcException ex) when (ex.Code is 5 or 404)
        {
            return (0, 0);
        }
        var account = r.GetProperty("account");
        // Vesting accounts nest the BaseAccount.
        if (account.TryGetProperty("base_vesting_account", out var bva)) account = bva.GetProperty("base_account");
        else if (account.TryGetProperty("base_account", out var ba)) account = ba;
        ulong Read(string name) => account.TryGetProperty(name, out var v) && ulong.TryParse(v.GetString(), out ulong n) ? n : 0;
        return (Read("account_number"), Read("sequence"));
    }

    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, "/cosmos/base/tendermint/v1beta1/blocks/latest", null, ct);
        var header = r.TryGetProperty("sdk_block", out var sdk) ? sdk.GetProperty("header") : r.GetProperty("block").GetProperty("header");
        return ulong.Parse(header.GetProperty("height").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Broadcasts TxRaw bytes in SYNC mode; a non-zero CheckTx code raises <see cref="RpcException"/>.</summary>
    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        var body = new { tx_bytes = Convert.ToBase64String(signedTxBytes.Span), mode = "BROADCAST_MODE_SYNC" };
        var r = await SendAsync(HttpMethod.Post, "/cosmos/tx/v1beta1/txs", body, ct);
        var txr = r.GetProperty("tx_response");
        long code = txr.GetProperty("code").GetInt64();
        if (code != 0) throw new RpcException(code, txr.GetProperty("raw_log").GetString() ?? "CheckTx failed", method: "broadcast");
        return new TxHash(txr.GetProperty("txhash").GetString()!);
    }

    /// <summary>Simulates a signed (or empty-signature) transaction and returns the gas used.</summary>
    public async ValueTask<ulong> SimulateAsync(byte[] txRaw, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "/cosmos/tx/v1beta1/simulate", new { tx_bytes = Convert.ToBase64String(txRaw) }, ct);
        return ulong.Parse(r.GetProperty("gas_info").GetProperty("gas_used").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default)
    {
        JsonElement r;
        try
        {
            r = await SendAsync(HttpMethod.Get, $"/cosmos/tx/v1beta1/txs/{hash.Value}", null, ct);
        }
        catch (RpcException)
        {
            return null;
        }
        var txr = r.GetProperty("tx_response");
        long code = txr.GetProperty("code").GetInt64();
        return new TxReceipt(hash,
            ulong.Parse(txr.GetProperty("height").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            code == 0,
            ulong.Parse(txr.GetProperty("gas_used").GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
            code == 0 ? null : txr.GetProperty("raw_log").GetString());
    }

    /// <summary>
    /// Signs and broadcasts <paramref name="builder"/>: fetches account number/sequence, simulates to size the gas
    /// (×1.3) and sets the fee from <see cref="CosmosChain.GasPrice"/>.
    /// </summary>
    public async ValueTask<TxHash> SignAndBroadcastAsync(CosmosAccount account, CosmosTxBuilder builder, CancellationToken ct = default)
    {
        var (accountNumber, sequence) = await GetAccountAsync(account.Address.Value, ct);

        builder.Fee.Clear();
        byte[] probe = account.Sign(builder, Network.NetworkChainId, accountNumber, sequence);
        ulong gasUsed = await SimulateAsync(probe, ct);
        builder.GasLimit = (ulong)Math.Ceiling(gasUsed * 1.3);
        BigInteger fee = new(Math.Ceiling(builder.GasLimit * Network.GasPrice));
        builder.Fee.Add(new Coin(Network.Denom, fee));

        byte[] tx = account.Sign(builder, Network.NetworkChainId, accountNumber, sequence);
        return await SendRawTransactionAsync(tx, ct);
    }

    /// <summary>Sends the chain's native token.</summary>
    public ValueTask<TxHash> TransferAsync(CosmosAccount account, string to, Amount amount, string memo = "", CancellationToken ct = default)
    {
        if (!CosmosAddress.IsValid(to, Network.Bech32Prefix)) throw new FormatException($"Invalid {Network.Bech32Prefix} address '{to}'");
        var builder = new CosmosTxBuilder { Memo = memo }
            .Add(CosmosMessage.BankSend(account.Address.Value, to, new Coin(Network.Denom, amount.WithDecimals(Network.Decimals).BaseUnits)));
        return SignAndBroadcastAsync(account, builder, ct);
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
