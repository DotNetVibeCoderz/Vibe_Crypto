using System.Numerics;
using System.Text.Json;
using Crypto.Net.Core;

namespace Crypto.Net.Evm;

/// <summary>An event log returned by <c>eth_getLogs</c> or a receipt.</summary>
public sealed record EvmLog(string Address, IReadOnlyList<string> Topics, string Data, ulong BlockNumber, string TransactionHash, ulong LogIndex);

/// <summary>Full EVM receipt including logs.</summary>
public sealed record EvmReceipt(
    string TransactionHash, ulong BlockNumber, bool Status, ulong GasUsed, BigInteger EffectiveGasPrice,
    string From, string? To, string? ContractAddress, IReadOnlyList<EvmLog> Logs);

/// <summary>Suggested EIP-1559 fees in wei.</summary>
public sealed record EvmFeeEstimate(BigInteger BaseFee, BigInteger MaxPriorityFeePerGas, BigInteger MaxFeePerGas);

/// <summary>
/// JSON-RPC client for Ethereum and EVM-compatible networks, plus helpers that fill nonce, fees and gas
/// and broadcast signed transactions.
/// </summary>
public sealed class EvmRpcClient : IChainClient
{
    private readonly JsonRpcClient _rpc;

    public EvmRpcClient(EvmChain chain, string? rpcUrl = null, HttpClient? httpClient = null)
        : this(chain, [new RpcEndpoint(rpcUrl ?? chain.DefaultRpcUrl)], httpClient)
    {
    }

    /// <summary>Uses <paramref name="endpoints"/> in order, failing over when a provider denies access.</summary>
    public EvmRpcClient(EvmChain chain, IReadOnlyList<RpcEndpoint> endpoints, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        EvmChain = chain;
        _rpc = new JsonRpcClient(endpoints, httpClient);
    }

    public EvmChain EvmChain { get; }
    public IChain Chain => EvmChain;
    public JsonRpcClient Rpc => _rpc;

    #region Queries

    public async ValueTask<ulong> GetChainIdAsync(CancellationToken ct = default) =>
        (ulong)HexUtil.ParseQuantity((await _rpc.CallAsync("eth_chainId", null, ct)).GetString());

    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default) =>
        (ulong)HexUtil.ParseQuantity((await _rpc.CallAsync("eth_blockNumber", null, ct)).GetString());

    public ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default) => GetBalanceAsync(address.Value, "latest", ct);

    public async ValueTask<Amount> GetBalanceAsync(string address, string block = "latest", CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("eth_getBalance", [address, block], ct);
        return Amount.FromWei(HexUtil.ParseQuantity(r.GetString()));
    }

    /// <summary>Next nonce for <paramref name="address"/> (includes pending transactions by default).</summary>
    public async ValueTask<ulong> GetTransactionCountAsync(string address, string block = "pending", CancellationToken ct = default) =>
        (ulong)HexUtil.ParseQuantity((await _rpc.CallAsync("eth_getTransactionCount", [address, block], ct)).GetString());

    public async ValueTask<BigInteger> GetGasPriceAsync(CancellationToken ct = default) =>
        HexUtil.ParseQuantity((await _rpc.CallAsync("eth_gasPrice", null, ct)).GetString());

    public async ValueTask<string> GetCodeAsync(string address, string block = "latest", CancellationToken ct = default) =>
        (await _rpc.CallAsync("eth_getCode", [address, block], ct)).GetString() ?? "0x";

    /// <summary>Executes a read-only call and returns the raw return data.</summary>
    public async ValueTask<byte[]> CallAsync(string to, ReadOnlyMemory<byte> data, string? from = null, string block = "latest", CancellationToken ct = default)
    {
        var call = new Dictionary<string, string> { ["to"] = to, ["data"] = HexUtil.Encode(data.Span) };
        if (from is not null) call["from"] = from;
        var r = await _rpc.CallAsync("eth_call", [call, block], ct);
        return HexUtil.Decode(r.GetString() ?? "0x");
    }

    public ValueTask<byte[]> CallAsync(Address to, byte[] data, CancellationToken ct = default) => CallAsync(to.Value, data, ct: ct);

    /// <summary>Calls a view function by signature and decodes the outputs, e.g. <c>("balanceOf(address)", "uint256", owner)</c>.</summary>
    public async ValueTask<object?[]> CallFunctionAsync(string contract, string signature, string outputTypes, object?[] args, CancellationToken ct = default)
    {
        byte[] result = await CallAsync(contract, EvmAbi.EncodeFunctionCall(signature, args), ct: ct);
        return EvmAbi.Decode(outputTypes, result);
    }

    /// <summary>Simulates the transaction and returns the gas it needs. Reverts surface as <see cref="RpcException"/>.</summary>
    public async ValueTask<ulong> EstimateGasAsync(string from, string? to, BigInteger value, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var tx = new Dictionary<string, string> { ["from"] = from, ["value"] = HexUtil.FormatQuantity(value) };
        if (to is not null) tx["to"] = to;
        if (!data.IsEmpty) tx["data"] = HexUtil.Encode(data.Span);
        return (ulong)HexUtil.ParseQuantity((await _rpc.CallAsync("eth_estimateGas", [tx], ct)).GetString());
    }

    /// <summary>Suggests EIP-1559 fees: maxFee = 2 × baseFee + tip.</summary>
    public async ValueTask<EvmFeeEstimate> EstimateFeesAsync(CancellationToken ct = default)
    {
        var block = await _rpc.CallAsync("eth_getBlockByNumber", ["latest", false], ct);
        if (!block.TryGetProperty("baseFeePerGas", out var baseFeeEl))
        {
            BigInteger gasPrice = await GetGasPriceAsync(ct);
            return new EvmFeeEstimate(0, gasPrice, gasPrice);
        }

        BigInteger baseFee = HexUtil.ParseQuantity(baseFeeEl.GetString());
        BigInteger tip;
        try
        {
            tip = HexUtil.ParseQuantity((await _rpc.CallAsync("eth_maxPriorityFeePerGas", null, ct)).GetString());
        }
        catch (RpcException)
        {
            tip = 1_500_000_000; // 1.5 gwei
        }
        return new EvmFeeEstimate(baseFee, tip, baseFee * 2 + tip);
    }

    public async ValueTask<EvmReceipt?> GetTransactionReceiptAsync(string txHash, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("eth_getTransactionReceipt", [txHash], ct);
        if (r.ValueKind == JsonValueKind.Null) return null;
        return new EvmReceipt(
            r.GetProperty("transactionHash").GetString()!,
            (ulong)HexUtil.ParseQuantity(Str(r, "blockNumber")),
            Str(r, "status") == "0x1",
            (ulong)HexUtil.ParseQuantity(Str(r, "gasUsed")),
            HexUtil.ParseQuantity(Str(r, "effectiveGasPrice")),
            Str(r, "from") ?? "",
            Str(r, "to"),
            Str(r, "contractAddress"),
            r.TryGetProperty("logs", out var logs) ? logs.EnumerateArray().Select(ParseLog).ToList() : []);
    }

    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default)
    {
        var r = await GetTransactionReceiptAsync(hash.Value, ct);
        return r is null ? null : new TxReceipt(hash, r.BlockNumber, r.Status, r.GasUsed, r.Status ? null : "Execution reverted");
    }

    /// <summary><c>eth_getLogs</c> for a contract and optional topic filter.</summary>
    public async ValueTask<IReadOnlyList<EvmLog>> GetLogsAsync(string? address, ulong fromBlock, ulong? toBlock = null, IReadOnlyList<string?>? topics = null, CancellationToken ct = default)
    {
        var filter = new Dictionary<string, object?>
        {
            ["fromBlock"] = HexUtil.FormatQuantity(fromBlock),
            ["toBlock"] = toBlock is null ? "latest" : HexUtil.FormatQuantity(toBlock.Value),
        };
        if (address is not null) filter["address"] = address;
        if (topics is not null) filter["topics"] = topics;
        var r = await _rpc.CallAsync("eth_getLogs", [filter], ct);
        return r.EnumerateArray().Select(ParseLog).ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static EvmLog ParseLog(JsonElement l) => new(
        Str(l, "address") ?? "",
        l.GetProperty("topics").EnumerateArray().Select(t => t.GetString()!).ToList(),
        Str(l, "data") ?? "0x",
        (ulong)HexUtil.ParseQuantity(Str(l, "blockNumber")),
        Str(l, "transactionHash") ?? "",
        (ulong)HexUtil.ParseQuantity(Str(l, "logIndex")));

    #endregion

    #region Sending

    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("eth_sendRawTransaction", [HexUtil.Encode(signedTxBytes.Span)], ct);
        return new TxHash(r.GetString()!);
    }

    /// <summary>
    /// Builds an EIP-1559 transaction with the next nonce, suggested fees and an estimated gas limit
    /// (the estimate simulates the call, so reverting transactions fail here before anything is signed).
    /// </summary>
    public async ValueTask<EvmTransaction> PrepareTransactionAsync(string from, string? to, BigInteger value, byte[]? data = null, CancellationToken ct = default)
    {
        data ??= [];
        var nonceTask = GetTransactionCountAsync(from, "pending", ct).AsTask();
        var feesTask = EstimateFeesAsync(ct).AsTask();
        var gasTask = EstimateGasAsync(from, to, value, data, ct).AsTask();
        await Task.WhenAll(nonceTask, feesTask, gasTask);

        var fees = feesTask.Result;
        return new EvmTransaction
        {
            Type = fees.BaseFee.IsZero ? EvmTransactionType.Legacy : EvmTransactionType.DynamicFee,
            ChainId = EvmChain.NumericChainId,
            Nonce = nonceTask.Result,
            GasPrice = fees.MaxFeePerGas,
            MaxPriorityFeePerGas = fees.MaxPriorityFeePerGas,
            MaxFeePerGas = fees.MaxFeePerGas,
            GasLimit = gasTask.Result * 12 / 10, // 20% headroom
            To = to,
            Value = value,
            Data = data,
        };
    }

    /// <summary>Signs and broadcasts <paramref name="transaction"/>.</summary>
    public async ValueTask<TxHash> SendTransactionAsync(ISigner signer, EvmTransaction transaction, CancellationToken ct = default)
    {
        var signed = await transaction.SignAsync(signer, ct);
        return await SendRawTransactionAsync(signed.RawBytes, ct);
    }

    /// <summary>Sends the native asset (ETH, POL, BNB…). Simulated via <c>eth_estimateGas</c> before signing.</summary>
    public async ValueTask<TxHash> TransferAsync(ISigner signer, string to, Amount amount, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (!EvmAddress.IsValid(to)) throw new FormatException($"Invalid recipient '{to}'");
        var tx = await PrepareTransactionAsync(signer.Address.Value, to, amount.WithDecimals(18).BaseUnits, null, ct);
        return await SendTransactionAsync(signer, tx, ct);
    }

    #endregion

    public void Dispose() => _rpc.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
