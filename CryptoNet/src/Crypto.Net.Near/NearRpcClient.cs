using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Near;

/// <summary>Balance details of a NEAR account (yoctoNEAR).</summary>
public sealed record NearAccountState(BigInteger Amount, BigInteger Locked, ulong StorageUsage, string CodeHash)
{
    public bool HasContract => CodeHash != "11111111111111111111111111111111";
}

/// <summary>NEAR JSON-RPC client.</summary>
public sealed class NearRpcClient : IChainClient
{
    private readonly JsonRpcClient _rpc;
    private readonly ConcurrentDictionary<string, string> _senders = new(StringComparer.Ordinal);

    public NearRpcClient(NearChain network, string? rpcUrl = null, HttpClient? httpClient = null)
        : this(network, [new RpcEndpoint(rpcUrl ?? network.DefaultRpcUrl)], httpClient)
    {
    }

    public NearRpcClient(NearChain network, IReadOnlyList<RpcEndpoint> endpoints, HttpClient? httpClient = null)
    {
        Network = network ?? throw new ArgumentNullException(nameof(network));
        _rpc = new JsonRpcClient(endpoints, httpClient);
    }

    public NearChain Network { get; }
    public IChain Chain => Network;
    public JsonRpcClient Rpc => _rpc;

    public async ValueTask<NearAccountState?> GetAccountAsync(string accountId, CancellationToken ct = default)
    {
        try
        {
            var r = await RawAsync("query", new { request_type = "view_account", finality = "final", account_id = accountId }, ct).ConfigureAwait(false);
            return new NearAccountState(
                BigInteger.Parse(r.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                BigInteger.Parse(r.GetProperty("locked").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                r.GetProperty("storage_usage").GetUInt64(),
                r.GetProperty("code_hash").GetString()!);
        }
        catch (RpcException ex) when (IsUnknownAccount(ex))
        {
            return null;
        }
    }

    public async ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default) =>
        new((await GetAccountAsync(address.Value, ct).ConfigureAwait(false))?.Amount ?? BigInteger.Zero, 24);

    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default)
    {
        var r = await RawAsync("block", new { finality = "final" }, ct).ConfigureAwait(false);
        return r.GetProperty("header").GetProperty("height").GetUInt64();
    }

    /// <summary>Hash of the latest final block (Base58), used as the transaction's recent block hash.</summary>
    public async ValueTask<string> GetLatestBlockHashAsync(CancellationToken ct = default)
    {
        var r = await RawAsync("block", new { finality = "final" }, ct).ConfigureAwait(false);
        return r.GetProperty("header").GetProperty("hash").GetString()!;
    }

    /// <summary>Current nonce of an access key (the next transaction uses nonce + 1).</summary>
    public async ValueTask<ulong> GetAccessKeyNonceAsync(string accountId, string publicKey, CancellationToken ct = default)
    {
        var r = await RawAsync("query", new { request_type = "view_access_key", finality = "final", account_id = accountId, public_key = publicKey }, ct).ConfigureAwait(false);
        return r.GetProperty("nonce").GetUInt64();
    }

    /// <summary>Calls a view method and returns its raw result (usually UTF-8 JSON).</summary>
    public async ValueTask<byte[]> ViewFunctionRawAsync(string contractId, string method, object? args = null, CancellationToken ct = default)
    {
        byte[] argBytes = args is null ? "{}"u8.ToArray() : args is byte[] b ? b : JsonSerializer.SerializeToUtf8Bytes(args, args.GetType());
        var r = await RawAsync("query", new
        {
            request_type = "call_function",
            finality = "final",
            account_id = contractId,
            method_name = method,
            args_base64 = System.Convert.ToBase64String(argBytes),
        }, ct).ConfigureAwait(false);
        return r.GetProperty("result").EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray();
    }

    /// <summary>Calls a view method and parses its JSON result.</summary>
    public async ValueTask<JsonElement> ViewFunctionAsync(string contractId, string method, object? args = null, CancellationToken ct = default)
    {
        byte[] raw = await ViewFunctionRawAsync(contractId, method, args, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw.Length == 0 ? "null"u8.ToArray() : raw);
        return doc.RootElement.Clone();
    }

    /// <summary>Submits a signed transaction without waiting (<c>wait_until: NONE</c>); returns its hash.</summary>
    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        await RawAsync("send_tx", new { signed_tx_base64 = System.Convert.ToBase64String(signedTxBytes.Span), wait_until = "NONE" }, ct).ConfigureAwait(false);
        return new TxHash(TransactionHashOf(signedTxBytes.Span));
    }

    /// <summary>Builds, signs and submits a transaction from <paramref name="account"/> to <paramref name="receiverId"/>.</summary>
    public async ValueTask<TxHash> SignAndSendAsync(NearAccount account, string receiverId, IEnumerable<NearAction> actions, CancellationToken ct = default)
    {
        ulong nonce = await GetAccessKeyNonceAsync(account.AccountId, account.PublicKeyString, ct).ConfigureAwait(false);
        string blockHash = await GetLatestBlockHashAsync(ct).ConfigureAwait(false);
        var tx = new NearTransaction
        {
            SignerId = account.AccountId,
            PublicKey = account.PublicKey.ToArray(),
            Nonce = nonce + 1,
            ReceiverId = receiverId,
            BlockHash = CryptoNative.Base58Decode(blockHash),
            Actions = [.. actions],
        };
        var hash = await SendRawTransactionAsync(tx.Sign(account), ct).ConfigureAwait(false);
        _senders[hash.Value] = account.AccountId;
        return hash;
    }

    public ValueTask<TxHash> TransferAsync(NearAccount account, string receiverId, Amount amount, CancellationToken ct = default) =>
        SignAndSendAsync(account, receiverId, [new NearAction.Transfer(amount.WithDecimals(24).BaseUnits)], ct);

    /// <summary>
    /// Receipt of a transaction sent through this client (NEAR's RPC needs the sender, which the client remembers);
    /// use <see cref="GetTransactionStatusAsync"/> for other transactions.
    /// </summary>
    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default)
    {
        if (!_senders.TryGetValue(hash.Value, out string? sender))
            throw new NotSupportedException("NEAR needs the sender account to look up a transaction; call GetTransactionStatusAsync(hash, sender).");
        return await GetTransactionStatusAsync(hash.Value, sender, ct).ConfigureAwait(false);
    }

    /// <summary>Final outcome of a transaction, or null while it is not yet executed.</summary>
    public async ValueTask<TxReceipt?> GetTransactionStatusAsync(string hash, string senderId, CancellationToken ct = default)
    {
        JsonElement r;
        try
        {
            r = await RawAsync("tx", new { tx_hash = hash, sender_account_id = senderId, wait_until = "EXECUTED_OPTIMISTIC" }, ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.RpcMessage.Contains("UNKNOWN_TRANSACTION", StringComparison.OrdinalIgnoreCase) || (ex.ErrorData?.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return null;
        }
        if (!r.TryGetProperty("status", out var status)) return null;
        bool ok = status.ValueKind == JsonValueKind.Object && (status.TryGetProperty("SuccessValue", out _) || status.TryGetProperty("SuccessReceiptId", out _));
        ulong gas = r.TryGetProperty("transaction_outcome", out var o) ? o.GetProperty("outcome").GetProperty("gas_burnt").GetUInt64() : 0;
        ulong height = 0;
        if (r.TryGetProperty("transaction_outcome", out var to) && to.TryGetProperty("block_hash", out var bh))
        {
            var block = await RawAsync("block", new { block_id = bh.GetString() }, ct).ConfigureAwait(false);
            height = block.GetProperty("header").GetProperty("height").GetUInt64();
        }
        return new TxReceipt(new TxHash(hash), height, ok, gas, ok ? null : status.GetRawText());
    }

    /// <summary>The transaction hash (Base58 sha256 of the unsigned part) of a signed transaction.</summary>
    public static string TransactionHashOf(ReadOnlySpan<byte> signedTx) => CryptoNative.Base58Encode(CryptoNative.Sha256(signedTx[..^65]));

    // NEAR methods take a JSON object (not an array) as params.
    private async Task<JsonElement> RawAsync(string method, object parameters, CancellationToken ct)
    {
        var r = await _rpc.CallWithObjectParamsAsync(method, parameters, ct).ConfigureAwait(false);
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            throw new RpcException(-32000, err.GetString()!, method: method);
        return r;
    }

    private static bool IsUnknownAccount(RpcException ex) =>
        ex.RpcMessage.Contains("UNKNOWN_ACCOUNT", StringComparison.OrdinalIgnoreCase) || (ex.ErrorData?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ?? false)
        || ex.RpcMessage.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _rpc.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
