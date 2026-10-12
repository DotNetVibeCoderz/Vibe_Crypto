using System.Numerics;
using System.Text;
using System.Text.Json;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Evm;

/// <summary>ERC-4337 EntryPoint versions and their canonical deployments (same address on every chain).</summary>
public enum EntryPointVersion
{
    V06,
    V07,
    V08,
}

public static class EntryPoints
{
    public const string V06 = "0x5FF137D4b0FDCD49DcA30c7CF57E578a026d2789";
    public const string V07 = "0x0000000071727De22E5E9d8BAf0edAc6f37da032";
    public const string V08 = "0x4337084D9E255Ff0702461CF8895CE9E3b5Ff108";

    public static string AddressOf(EntryPointVersion version) => version switch
    {
        EntryPointVersion.V06 => V06,
        EntryPointVersion.V07 => V07,
        _ => V08,
    };
}

/// <summary>
/// An ERC-4337 user operation in the unpacked form bundlers accept since EntryPoint v0.7. For v0.6, <see cref="InitCode"/>
/// is <c>factory ‖ factoryData</c> and <c>paymasterAndData</c> is <c>paymaster ‖ paymasterData</c>.
/// </summary>
public sealed class UserOperation
{
    public required string Sender { get; set; }
    public BigInteger Nonce { get; set; }
    public string? Factory { get; set; }
    public byte[] FactoryData { get; set; } = [];
    public byte[] CallData { get; set; } = [];
    public BigInteger CallGasLimit { get; set; }
    public BigInteger VerificationGasLimit { get; set; }
    public BigInteger PreVerificationGas { get; set; }
    public BigInteger MaxFeePerGas { get; set; }
    public BigInteger MaxPriorityFeePerGas { get; set; }
    public string? Paymaster { get; set; }
    public BigInteger PaymasterVerificationGasLimit { get; set; }
    public BigInteger PaymasterPostOpGasLimit { get; set; }
    public byte[] PaymasterData { get; set; } = [];
    public byte[] Signature { get; set; } = [];

    public byte[] InitCode => Factory is null ? [] : [.. HexUtil.Decode(Factory), .. FactoryData];

    /// <summary>v0.7+ packed <c>paymasterAndData</c>: paymaster ‖ uint128 verification gas ‖ uint128 post-op gas ‖ data.</summary>
    public byte[] PackedPaymasterAndData => Paymaster is null ? []
        : [.. HexUtil.Decode(Paymaster), .. Uint128(PaymasterVerificationGasLimit), .. Uint128(PaymasterPostOpGasLimit), .. PaymasterData];

    /// <summary>v0.6 <c>paymasterAndData</c>: paymaster ‖ data.</summary>
    public byte[] PaymasterAndDataV06 => Paymaster is null ? [] : [.. HexUtil.Decode(Paymaster), .. PaymasterData];

    /// <summary><c>verificationGasLimit ‖ callGasLimit</c> as two uint128 halves of a bytes32.</summary>
    public byte[] AccountGasLimits => [.. Uint128(VerificationGasLimit), .. Uint128(CallGasLimit)];

    /// <summary><c>maxPriorityFeePerGas ‖ maxFeePerGas</c> as two uint128 halves of a bytes32.</summary>
    public byte[] GasFees => [.. Uint128(MaxPriorityFeePerGas), .. Uint128(MaxFeePerGas)];

    /// <summary>The hash the account signs (what <c>EntryPoint.getUserOpHash</c> returns).</summary>
    public byte[] GetHash(EntryPointVersion version, ulong chainId, string? entryPoint = null)
    {
        entryPoint ??= EntryPoints.AddressOf(version);
        byte[] K(byte[] data) => CryptoNative.Keccak256(data);
        switch (version)
        {
            case EntryPointVersion.V06:
            {
                byte[] inner = EvmAbi.Encode("address,uint256,bytes32,bytes32,uint256,uint256,uint256,uint256,uint256,bytes32",
                    Sender, Nonce, K(InitCode), K(CallData), CallGasLimit, VerificationGasLimit, PreVerificationGas, MaxFeePerGas, MaxPriorityFeePerGas, K(PaymasterAndDataV06));
                return K(EvmAbi.Encode("bytes32,address,uint256", K(inner), entryPoint, new BigInteger(chainId)));
            }
            case EntryPointVersion.V07:
            {
                byte[] inner = EvmAbi.Encode("address,uint256,bytes32,bytes32,bytes32,uint256,bytes32,bytes32",
                    Sender, Nonce, K(InitCode), K(CallData), AccountGasLimits, PreVerificationGas, GasFees, K(PackedPaymasterAndData));
                return K(EvmAbi.Encode("bytes32,address,uint256", K(inner), entryPoint, new BigInteger(chainId)));
            }
            default:
            {
                // v0.8: EIP-712 typed data with domain { name: "ERC4337", version: "1", chainId, verifyingContract }.
                byte[] typeHash = K(Encoding.UTF8.GetBytes("PackedUserOperation(address sender,uint256 nonce,bytes initCode,bytes callData,bytes32 accountGasLimits,uint256 preVerificationGas,bytes32 gasFees,bytes paymasterAndData)"));
                byte[] structHash = K(EvmAbi.Encode("bytes32,address,uint256,bytes32,bytes32,bytes32,uint256,bytes32,bytes32",
                    typeHash, Sender, Nonce, K(InitCode), K(CallData), AccountGasLimits, PreVerificationGas, GasFees, K(PackedPaymasterAndData)));
                byte[] domainType = K(Encoding.UTF8.GetBytes("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"));
                byte[] domain = K(EvmAbi.Encode("bytes32,bytes32,bytes32,uint256,address", domainType, K("ERC4337"u8.ToArray()), K("1"u8.ToArray()), new BigInteger(chainId), entryPoint));
                return K([0x19, 0x01, .. domain, .. structHash]);
            }
        }
    }

    /// <summary>The JSON object bundlers expect in <c>eth_sendUserOperation</c> for <paramref name="version"/>.</summary>
    public Dictionary<string, object?> ToRpc(EntryPointVersion version)
    {
        static string Q(BigInteger v) => HexUtil.FormatQuantity(v);
        if (version == EntryPointVersion.V06)
        {
            return new()
            {
                ["sender"] = Sender, ["nonce"] = Q(Nonce), ["initCode"] = HexUtil.Encode(InitCode), ["callData"] = HexUtil.Encode(CallData),
                ["callGasLimit"] = Q(CallGasLimit), ["verificationGasLimit"] = Q(VerificationGasLimit), ["preVerificationGas"] = Q(PreVerificationGas),
                ["maxFeePerGas"] = Q(MaxFeePerGas), ["maxPriorityFeePerGas"] = Q(MaxPriorityFeePerGas),
                ["paymasterAndData"] = HexUtil.Encode(PaymasterAndDataV06), ["signature"] = HexUtil.Encode(Signature),
            };
        }
        var d = new Dictionary<string, object?>
        {
            ["sender"] = Sender, ["nonce"] = Q(Nonce), ["callData"] = HexUtil.Encode(CallData),
            ["callGasLimit"] = Q(CallGasLimit), ["verificationGasLimit"] = Q(VerificationGasLimit), ["preVerificationGas"] = Q(PreVerificationGas),
            ["maxFeePerGas"] = Q(MaxFeePerGas), ["maxPriorityFeePerGas"] = Q(MaxPriorityFeePerGas), ["signature"] = HexUtil.Encode(Signature),
        };
        if (Factory is not null)
        {
            d["factory"] = Factory;
            d["factoryData"] = HexUtil.Encode(FactoryData);
        }
        if (Paymaster is not null)
        {
            d["paymaster"] = Paymaster;
            d["paymasterVerificationGasLimit"] = Q(PaymasterVerificationGasLimit);
            d["paymasterPostOpGasLimit"] = Q(PaymasterPostOpGasLimit);
            d["paymasterData"] = HexUtil.Encode(PaymasterData);
        }
        return d;
    }

    private static byte[] Uint128(BigInteger v)
    {
        if (v.Sign < 0 || v >= BigInteger.One << 128) throw new ArgumentOutOfRangeException(nameof(v), "Value does not fit in uint128");
        var b = new byte[16];
        v.TryWriteBytes(b.AsSpan(16 - v.GetByteCount(isUnsigned: true)), out _, isUnsigned: true, isBigEndian: true);
        return b;
    }
}

/// <summary>Gas fields returned by <c>eth_estimateUserOperationGas</c>.</summary>
public sealed record UserOperationGas(BigInteger PreVerificationGas, BigInteger VerificationGasLimit, BigInteger CallGasLimit,
    BigInteger PaymasterVerificationGasLimit, BigInteger PaymasterPostOpGasLimit);

/// <summary>Result of <c>eth_getUserOperationReceipt</c>.</summary>
public sealed record UserOperationReceipt(string UserOpHash, bool Success, string TransactionHash, ulong BlockNumber, BigInteger ActualGasCost, string? Reason);

/// <summary>JSON-RPC client for an ERC-4337 bundler (Pimlico, Alchemy, Stackup, Candide, a local Skandha/Alto…).</summary>
public sealed class BundlerClient : IDisposable
{
    private readonly JsonRpcClient _rpc;

    public BundlerClient(string url, HttpClient? httpClient = null) : this(new JsonRpcClient(url, httpClient)) { }

    public BundlerClient(JsonRpcClient rpc) => _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));

    public async ValueTask<IReadOnlyList<string>> GetSupportedEntryPointsAsync(CancellationToken ct = default) =>
        [.. (await _rpc.CallAsync("eth_supportedEntryPoints", null, ct).ConfigureAwait(false)).EnumerateArray().Select(e => e.GetString()!)];

    public async ValueTask<UserOperationGas> EstimateGasAsync(UserOperation op, EntryPointVersion version, string? entryPoint = null, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("eth_estimateUserOperationGas", [op.ToRpc(version), entryPoint ?? EntryPoints.AddressOf(version)], ct).ConfigureAwait(false);
        BigInteger Q(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? HexUtil.ParseQuantity(v.GetString()) : BigInteger.Zero;
        return new UserOperationGas(Q("preVerificationGas"), Q("verificationGasLimit"), Q("callGasLimit"), Q("paymasterVerificationGasLimit"), Q("paymasterPostOpGasLimit"));
    }

    /// <summary>Submits a signed user operation; returns its hash.</summary>
    public async ValueTask<string> SendAsync(UserOperation op, EntryPointVersion version, string? entryPoint = null, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("eth_sendUserOperation", [op.ToRpc(version), entryPoint ?? EntryPoints.AddressOf(version)], ct).ConfigureAwait(false);
        return r.GetString()!;
    }

    /// <summary>Receipt of an included user operation, or null while it is pending.</summary>
    public async ValueTask<UserOperationReceipt?> GetReceiptAsync(string userOpHash, CancellationToken ct = default)
    {
        var r = await _rpc.CallAsync("eth_getUserOperationReceipt", [userOpHash], ct).ConfigureAwait(false);
        if (r.ValueKind == JsonValueKind.Null) return null;
        var receipt = r.GetProperty("receipt");
        return new UserOperationReceipt(
            r.GetProperty("userOpHash").GetString()!,
            r.GetProperty("success").GetBoolean(),
            receipt.GetProperty("transactionHash").GetString()!,
            (ulong)HexUtil.ParseQuantity(receipt.GetProperty("blockNumber").GetString()),
            HexUtil.ParseQuantity(r.GetProperty("actualGasCost").GetString()),
            r.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null);
    }

    public void Dispose() => _rpc.Dispose();
}

/// <summary>Helpers for the reference SimpleAccount (eth-infinitism) and compatible accounts.</summary>
public static class SimpleAccount
{
    /// <summary>SimpleAccountFactory v0.7 (same address on every chain).</summary>
    public const string FactoryV07 = "0x91E60e0613810449d098b0b5Ec8b51A0FE8c8985";

    /// <summary><c>execute(dest, value, func)</c> call data.</summary>
    public static byte[] EncodeExecute(string destination, BigInteger value, byte[] data) =>
        EvmAbi.EncodeFunctionCall("execute(address,uint256,bytes)", destination, value, data);

    /// <summary><c>executeBatch(dest[], value[], func[])</c> call data (v0.7 signature).</summary>
    public static byte[] EncodeExecuteBatch(IReadOnlyList<(string Destination, BigInteger Value, byte[] Data)> calls) =>
        EvmAbi.EncodeFunctionCall("executeBatch(address[],uint256[],bytes[])",
            calls.Select(c => c.Destination).ToArray(), calls.Select(c => c.Value).ToArray(), calls.Select(c => c.Data).ToArray());

    /// <summary>Factory call data that deploys the account on its first user operation.</summary>
    public static byte[] EncodeCreateAccount(string owner, BigInteger salt) =>
        EvmAbi.EncodeFunctionCall("createAccount(address,uint256)", owner, salt);

    /// <summary>The counterfactual account address for <paramref name="owner"/> and <paramref name="salt"/>.</summary>
    public static async ValueTask<string> GetAddressAsync(EvmRpcClient rpc, string owner, BigInteger salt, string factory = FactoryV07, CancellationToken ct = default) =>
        (string)(await rpc.CallFunctionAsync(factory, "getAddress(address,uint256)", "address", [owner, salt], ct).ConfigureAwait(false))[0]!;

    /// <summary>The next nonce of <paramref name="sender"/> from the EntryPoint (key 0 unless parallel nonces are used).</summary>
    public static async ValueTask<BigInteger> GetNonceAsync(EvmRpcClient rpc, string sender, EntryPointVersion version, BigInteger? key = null, CancellationToken ct = default) =>
        (BigInteger)(await rpc.CallFunctionAsync(EntryPoints.AddressOf(version), "getNonce(address,uint192)", "uint256", [sender, key ?? BigInteger.Zero], ct).ConfigureAwait(false))[0]!;

    /// <summary>
    /// Signs the user operation as SimpleAccount expects: an EIP-191 personal signature over the user-op hash
    /// (v0.6/v0.7). For v0.8 the account verifies the raw EIP-712 hash instead.
    /// </summary>
    public static byte[] Sign(UserOperation op, ReadOnlySpan<byte> ownerPrivateKey, EntryPointVersion version, ulong chainId)
    {
        byte[] hash = op.GetHash(version, chainId);
        byte[] digest = version == EntryPointVersion.V08 ? hash : EvmMessageSigner.HashPersonalMessage(hash);
        var (rs, recovery) = CryptoNative.Secp256k1SignRecoverable(ownerPrivateKey, digest);
        op.Signature = [.. rs, (byte)(27 + recovery)];
        return op.Signature;
    }
}
