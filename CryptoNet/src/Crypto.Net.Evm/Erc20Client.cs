using System.Numerics;
using Crypto.Net.Core;

namespace Crypto.Net.Evm;

/// <summary>Typed client for an ERC-20 token contract.</summary>
public sealed class Erc20Client
{
    private readonly EvmRpcClient _rpc;
    private int? _decimals;

    public Erc20Client(EvmRpcClient rpcClient, string contractAddress)
    {
        ArgumentNullException.ThrowIfNull(rpcClient);
        _rpc = rpcClient;
        ContractAddress = EvmAddress.Parse(contractAddress, rpcClient.EvmChain.Id).Value;
    }

    public Erc20Client(EvmRpcClient rpcClient, Address contractAddress) : this(rpcClient, contractAddress.Value) { }

    public string ContractAddress { get; }

    /// <summary>keccak256("Transfer(address,address,uint256)") — topic[0] of transfer logs.</summary>
    public static string TransferEventTopic { get; } = HexUtil.Encode(EvmAbi.GetEventTopic("Transfer(address,address,uint256)"));

    public static byte[] BuildTransferData(string recipient, BigInteger rawAmount) =>
        EvmAbi.EncodeFunctionCall("transfer(address,uint256)", recipient, rawAmount);

    public static byte[] BuildTransferData(Address recipient, BigInteger rawAmount) => BuildTransferData(recipient.Value, rawAmount);

    public static byte[] BuildApproveData(string spender, BigInteger rawAmount) =>
        EvmAbi.EncodeFunctionCall("approve(address,uint256)", spender, rawAmount);

    public static byte[] BuildApproveData(Address spender, BigInteger rawAmount) => BuildApproveData(spender.Value, rawAmount);

    public async ValueTask<string> GetNameAsync(CancellationToken ct = default) =>
        (string)(await _rpc.CallFunctionAsync(ContractAddress, "name()", "string", [], ct))[0]!;

    public async ValueTask<string> GetSymbolAsync(CancellationToken ct = default) =>
        (string)(await _rpc.CallFunctionAsync(ContractAddress, "symbol()", "string", [], ct))[0]!;

    public async ValueTask<int> GetDecimalsAsync(CancellationToken ct = default) =>
        _decimals ??= (int)(BigInteger)(await _rpc.CallFunctionAsync(ContractAddress, "decimals()", "uint8", [], ct))[0]!;

    public async ValueTask<Amount> GetTotalSupplyAsync(CancellationToken ct = default)
    {
        var raw = (BigInteger)(await _rpc.CallFunctionAsync(ContractAddress, "totalSupply()", "uint256", [], ct))[0]!;
        return new Amount(raw, await GetDecimalsAsync(ct));
    }

    public async ValueTask<Amount> GetBalanceAsync(string owner, CancellationToken ct = default)
    {
        var raw = (BigInteger)(await _rpc.CallFunctionAsync(ContractAddress, "balanceOf(address)", "uint256", [owner], ct))[0]!;
        return new Amount(raw, await GetDecimalsAsync(ct));
    }

    public ValueTask<Amount> GetBalanceAsync(Address owner, CancellationToken ct = default) => GetBalanceAsync(owner.Value, ct);

    public async ValueTask<Amount> GetAllowanceAsync(string owner, string spender, CancellationToken ct = default)
    {
        var raw = (BigInteger)(await _rpc.CallFunctionAsync(ContractAddress, "allowance(address,address)", "uint256", [owner, spender], ct))[0]!;
        return new Amount(raw, await GetDecimalsAsync(ct));
    }

    /// <summary>Transfers tokens; <paramref name="amount"/> is rescaled to the token decimals.</summary>
    public async ValueTask<TxHash> TransferAsync(ISigner signer, string to, Amount amount, CancellationToken ct = default)
    {
        int decimals = await GetDecimalsAsync(ct);
        byte[] data = BuildTransferData(to, amount.WithDecimals(decimals).BaseUnits);
        var tx = await _rpc.PrepareTransactionAsync(signer.Address.Value, ContractAddress, BigInteger.Zero, data, ct);
        return await _rpc.SendTransactionAsync(signer, tx, ct);
    }

    public async ValueTask<TxHash> ApproveAsync(ISigner signer, string spender, Amount amount, CancellationToken ct = default)
    {
        int decimals = await GetDecimalsAsync(ct);
        byte[] data = BuildApproveData(spender, amount.WithDecimals(decimals).BaseUnits);
        var tx = await _rpc.PrepareTransactionAsync(signer.Address.Value, ContractAddress, BigInteger.Zero, data, ct);
        return await _rpc.SendTransactionAsync(signer, tx, ct);
    }
}
