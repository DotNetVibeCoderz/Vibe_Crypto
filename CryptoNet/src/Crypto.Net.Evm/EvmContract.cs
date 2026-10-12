using System.Numerics;
using Crypto.Net.Core;

namespace Crypto.Net.Evm;

/// <summary>
/// Base class of typed contract clients produced by the Crypto.Net source generator (<c>*.abi.json</c>) and
/// <c>cnet gen</c>. It can also be used directly to wrap a contract by hand.
/// </summary>
public abstract class EvmContract
{
    protected EvmContract(EvmRpcClient rpc, string address)
    {
        Rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        if (!EvmAddress.IsValid(address)) throw new FormatException($"Invalid contract address '{address}'");
        Address = address;
    }

    public EvmRpcClient Rpc { get; }
    public string Address { get; }

    /// <summary>Runs a view call; <paramref name="outputTypes"/> is a comma-separated ABI type list.</summary>
    protected async ValueTask<object?[]> CallAsync(string signature, string outputTypes, object?[] args, string block, CancellationToken ct)
    {
        byte[] result = await Rpc.CallAsync(Address, EvmAbi.EncodeFunctionCall(signature, args), block: block, ct: ct).ConfigureAwait(false);
        if (result.Length == 0 && outputTypes.Length > 0)
            throw new RpcException(-32000, $"{signature} returned no data; is {Address} a contract on this network?", method: "eth_call");
        return outputTypes.Length == 0 ? [] : EvmAbi.Decode(outputTypes, result);
    }

    /// <summary>Encodes, simulates (gas estimate), signs and broadcasts a state-changing call.</summary>
    protected async ValueTask<TxHash> SendAsync(ISigner signer, string signature, object?[] args, BigInteger value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signer);
        byte[] data = EvmAbi.EncodeFunctionCall(signature, args);
        var tx = await Rpc.PrepareTransactionAsync(signer.Address.Value, Address, value, data, ct).ConfigureAwait(false);
        return await Rpc.SendTransactionAsync(signer, tx, ct).ConfigureAwait(false);
    }

    /// <summary>Converts a decoded ABI array to a typed array.</summary>
    protected static T[] ToArray<T>(object? value, Func<object?, T> convert) =>
        value is object?[] items ? Array.ConvertAll(items, x => convert(x)) : throw new FormatException("Expected an ABI array");

    /// <summary>The fields of a decoded ABI tuple.</summary>
    protected static object?[] Fields(object? value) => value as object?[] ?? throw new FormatException("Expected an ABI tuple");
}
