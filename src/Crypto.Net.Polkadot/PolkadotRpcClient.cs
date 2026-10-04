using System.Numerics;
using System.Text.Json;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Polkadot;

/// <summary>Decoded <c>System.Account</c> storage entry.</summary>
public sealed record SubstrateAccountInfo(uint Nonce, BigInteger Free, BigInteger Reserved, BigInteger Frozen)
{
    /// <summary>Spendable balance: free minus the part frozen beyond what is reserved.</summary>
    public BigInteger Transferable => Free - BigInteger.Max(Frozen - Reserved, BigInteger.Zero);
}

/// <summary>Substrate JSON-RPC client (Polkadot, Kusama, Asset Hubs, Westend, Paseo, dev nodes).</summary>
public sealed class PolkadotRpcClient : IChainClient
{
    // twox128("System") ++ twox128("Account")
    private static readonly byte[] SystemAccountPrefix = Convert.FromHexString("26aa394eea5630e07c48ae0c9558cef7b99d880ec681799c0cf30e8886371da9");

    private readonly JsonRpcClient _rpc;
    private RuntimeMetadata? _metadata;
    private uint _metadataSpec;

    public PolkadotRpcClient(PolkadotChain chain, string? rpcUrl = null, HttpClient? httpClient = null)
        : this(chain, [new RpcEndpoint(rpcUrl ?? chain.DefaultRpcUrl)], httpClient)
    {
    }

    /// <summary>Uses <paramref name="endpoints"/> in order, failing over when a provider denies access.</summary>
    public PolkadotRpcClient(PolkadotChain chain, IReadOnlyList<RpcEndpoint> endpoints, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        Network = chain;
        _rpc = new JsonRpcClient(endpoints, httpClient);
    }

    public PolkadotChain Network { get; }
    public IChain Chain => Network;
    public JsonRpcClient Rpc => _rpc;

    public async ValueTask<string> GetChainNameAsync(CancellationToken ct = default) =>
        (await _rpc.CallAsync("system_chain", null, ct)).GetString()!;

    public async ValueTask<(uint SpecVersion, uint TransactionVersion, string SpecName)> GetRuntimeVersionAsync(CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("state_getRuntimeVersion", null, ct);
        return (r.GetProperty("specVersion").GetUInt32(), r.GetProperty("transactionVersion").GetUInt32(), r.GetProperty("specName").GetString()!);
    }

    /// <summary>Runtime metadata, cached per spec version.</summary>
    public async ValueTask<RuntimeMetadata> GetMetadataAsync(CancellationToken ct = default)
    {
        var (spec, _, _) = await GetRuntimeVersionAsync(ct);
        if (_metadata is not null && _metadataSpec == spec) return _metadata;
        var r = await _rpc.CallAsync("state_getMetadata", null, ct);
        _metadata = RuntimeMetadata.Parse(HexUtil.Decode(r.GetString()!));
        _metadataSpec = spec;
        return _metadata;
    }

    public async ValueTask<byte[]> GetBlockHashAsync(ulong? number = null, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("chain_getBlockHash", number is null ? [] : [number.Value], ct);
        return HexUtil.Decode(r.GetString()!);
    }

    public async ValueTask<ulong> GetBlockNumberAsync(CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("chain_getHeader", null, ct);
        return (ulong)HexUtil.ParseQuantity(r.GetProperty("number").GetString());
    }

    /// <summary>Next nonce including pending transactions in the pool.</summary>
    public async ValueTask<uint> GetNonceAsync(string address, CancellationToken ct = default) =>
        (await _rpc.CallAsync("system_accountNextIndex", [address], ct)).GetUInt32();

    public async ValueTask<SubstrateAccountInfo> GetAccountInfoAsync(string address, CancellationToken ct = default)
    {
        var (_, accountId) = Ss58Address.Decode(address);
        byte[] key = [.. SystemAccountPrefix, .. CryptoNative.Blake2b128(accountId), .. accountId];
        var r = await _rpc.CallAsync("state_getStorage", [HexUtil.Encode(key)], ct);
        if (r.ValueKind == JsonValueKind.Null) return new SubstrateAccountInfo(0, 0, 0, 0);

        var reader = new ScaleReader(HexUtil.Decode(r.GetString()!));
        uint nonce = reader.U32();
        reader.U32(); // consumers
        reader.U32(); // providers
        reader.U32(); // sufficients
        return new SubstrateAccountInfo(nonce, reader.U128(), reader.U128(), reader.U128());
    }

    /// <summary>Transferable balance in the chain's native token.</summary>
    public async ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default)
    {
        var info = await GetAccountInfoAsync(address.Value, ct);
        return new Amount(info.Transferable, Network.Decimals);
    }

    public async ValueTask<TxHash> SendRawTransactionAsync(ReadOnlyMemory<byte> signedTxBytes, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("author_submitExtrinsic", [HexUtil.Encode(signedTxBytes.Span)], ct);
        return new TxHash(r.GetString()!);
    }

    /// <summary>
    /// Looks for the extrinsic in the 50 most recent blocks. A receipt means the extrinsic was
    /// included; dispatch success requires decoding <c>System.Events</c> (check an explorer for failures).
    /// </summary>
    public async ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default) => await FindExtrinsicAsync(hash, 50, ct);

    public async ValueTask<TxReceipt?> FindExtrinsicAsync(TxHash hash, int depth, CancellationToken ct = default)
    {
        ulong head = await GetBlockNumberAsync(ct);
        for (ulong n = head; n + (ulong)depth > head && n > 0; n--)
        {
            byte[] blockHash = await GetBlockHashAsync(n, ct);
            var block = await _rpc.CallAsync("chain_getBlock", [HexUtil.Encode(blockHash)], ct);
            foreach (var ext in block.GetProperty("block").GetProperty("extrinsics").EnumerateArray())
            {
                if (Extrinsic.Hash(HexUtil.Decode(ext.GetString()!)) == hash)
                    return new TxReceipt(hash, n, true);
            }
        }
        return null;
    }

    /// <summary>Collects everything needed to sign an extrinsic for <paramref name="signer"/>.</summary>
    public async ValueTask<ExtrinsicContext> GetExtrinsicContextAsync(string signer, BigInteger? tip = null, ulong? mortalPeriod = 64, CancellationToken ct = default)
    {
        var versionTask = GetRuntimeVersionAsync(ct).AsTask();
        var genesisTask = GetBlockHashAsync(0, ct).AsTask();
        var nonceTask = GetNonceAsync(signer, ct).AsTask();
        var headTask = GetBlockNumberAsync(ct).AsTask();
        await Task.WhenAll(versionTask, genesisTask, nonceTask, headTask);
        ulong head = headTask.Result;
        byte[] blockHash = await GetBlockHashAsync(head, ct);
        return new ExtrinsicContext(versionTask.Result.SpecVersion, versionTask.Result.TransactionVersion, genesisTask.Result,
            blockHash, head, nonceTask.Result, tip ?? BigInteger.Zero, mortalPeriod);
    }

    /// <summary>Signs and submits an arbitrary call.</summary>
    public async ValueTask<TxHash> SubmitCallAsync(PolkadotAccount signer, byte[] call, BigInteger? tip = null, CancellationToken ct = default)
    {
        var metadata = await GetMetadataAsync(ct);
        var ctx = await GetExtrinsicContextAsync(signer.Address.Value, tip, ct: ct);
        byte[] extrinsic = Extrinsic.Sign(metadata, call, signer, ctx);
        return await SendRawTransactionAsync(extrinsic, ct);
    }

    /// <summary>Transfers the native token with <c>Balances.transfer_keep_alive</c> (never reaps the sender).</summary>
    public async ValueTask<TxHash> TransferAsync(PolkadotAccount signer, string to, Amount amount, CancellationToken ct = default)
    {
        if (!Ss58Address.IsValid(to)) throw new FormatException($"Invalid SS58 address '{to}'");
        var metadata = await GetMetadataAsync(ct);
        byte[] call = Extrinsic.TransferKeepAlive(metadata, to, amount.WithDecimals(Network.Decimals).BaseUnits);
        return await SubmitCallAsync(signer, call, ct: ct);
    }

    /// <summary>Estimated partial fee for a signed extrinsic (<c>payment_queryInfo</c>).</summary>
    public async ValueTask<Amount> EstimateFeeAsync(byte[] signedExtrinsic, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("payment_queryInfo", [HexUtil.Encode(signedExtrinsic)], ct);
        var fee = r.GetProperty("partialFee");
        BigInteger value = fee.ValueKind == JsonValueKind.String
            ? (fee.GetString()!.StartsWith("0x", StringComparison.Ordinal) ? HexUtil.ParseQuantity(fee.GetString()) : BigInteger.Parse(fee.GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            : new BigInteger(fee.GetDecimal());
        return new Amount(value, Network.Decimals);
    }

    public void Dispose() => _rpc.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
