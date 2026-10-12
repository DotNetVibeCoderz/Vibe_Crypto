using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Aptos;

/// <summary>An entry function call, the usual transaction payload.</summary>
public sealed record AptosEntryFunction(string ModuleAddress, string Module, string Function, IReadOnlyList<MoveTypeTag> TypeArgs, IReadOnlyList<byte[]> Args)
{
    /// <summary>Builds a call from <c>"0x1::module::function"</c>.</summary>
    public static AptosEntryFunction Create(string function, IReadOnlyList<MoveTypeTag>? typeArgs = null, params byte[][] args)
    {
        string[] parts = function.Split("::");
        if (parts.Length != 3) throw new FormatException($"Expected address::module::function, got '{function}'");
        AptosAddress.Parse(parts[0]);
        return new AptosEntryFunction(parts[0], parts[1], parts[2], typeArgs ?? [], args);
    }

    /// <summary><c>0x1::aptos_account::transfer(to, octas)</c>: sends APT and creates the recipient account if needed.</summary>
    public static AptosEntryFunction Transfer(string to, ulong octas) =>
        Create("0x1::aptos_account::transfer", null, MoveArg.Address(to), MoveArg.U64(octas));

    /// <summary><c>0x1::aptos_account::transfer_coins&lt;CoinType&gt;(to, amount)</c>.</summary>
    public static AptosEntryFunction TransferCoins(string coinType, string to, ulong amount) =>
        Create("0x1::aptos_account::transfer_coins", [MoveTypeTag.Parse(coinType)], MoveArg.Address(to), MoveArg.U64(amount));

    /// <summary><c>0x1::primary_fungible_store::transfer(metadata, to, amount)</c> for fungible assets.</summary>
    public static AptosEntryFunction TransferFungibleAsset(string metadataAddress, string to, ulong amount) =>
        Create("0x1::primary_fungible_store::transfer", [MoveTypeTag.Parse("0x1::fungible_asset::Metadata")],
            MoveArg.Address(metadataAddress), MoveArg.Address(to), MoveArg.U64(amount));

    public string FunctionId => $"{AptosAddress.Normalize(ModuleAddress)}::{Module}::{Function}";

    internal void Write(BcsWriter w)
    {
        w.Variant(2).Fixed(AptosAddress.Parse(ModuleAddress)).String(Module).String(Function);
        w.Vector(TypeArgs, (bw, t) => t.Write(bw));
        w.Vector(Args, (bw, a) => bw.Bytes(a));
    }
}

/// <summary>An Aptos <c>RawTransaction</c> with an entry function payload.</summary>
public sealed class AptosRawTransaction
{
    private static readonly byte[] RawTransactionPrefix = CryptoNative.Sha3_256("APTOS::RawTransaction"u8);
    private static readonly byte[] TransactionPrefix = CryptoNative.Sha3_256("APTOS::Transaction"u8);

    public required string Sender { get; init; }
    public required ulong SequenceNumber { get; init; }
    public required AptosEntryFunction Payload { get; init; }
    public ulong MaxGasAmount { get; init; } = 200_000;
    public ulong GasUnitPrice { get; init; } = 100;

    /// <summary>Unix time in seconds after which the transaction is discarded.</summary>
    public required ulong ExpirationTimestampSecs { get; init; }
    public required byte ChainId { get; init; }

    public byte[] Serialize()
    {
        var w = new BcsWriter().Fixed(AptosAddress.Parse(Sender)).U64(SequenceNumber);
        Payload.Write(w);
        return w.U64(MaxGasAmount).U64(GasUnitPrice).U64(ExpirationTimestampSecs).U8(ChainId).ToArray();
    }

    /// <summary>The bytes an ed25519 key signs: <c>sha3-256("APTOS::RawTransaction") ‖ BCS</c>.</summary>
    public byte[] SigningMessage() => [.. RawTransactionPrefix, .. Serialize()];

    /// <summary>Signs and returns the BCS <c>SignedTransaction</c> with an ed25519 authenticator.</summary>
    public byte[] Sign(AptosAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        byte[] raw = Serialize();
        byte[] signature = account.Sign([.. RawTransactionPrefix, .. raw]);
        return WithEd25519Authenticator(raw, account.PublicKey.Span, signature);
    }

    /// <summary>A signed transaction with a zero signature, for <c>/transactions/simulate</c>.</summary>
    public byte[] ForSimulation(ReadOnlySpan<byte> publicKey) => WithEd25519Authenticator(Serialize(), publicKey, new byte[64]);

    private static byte[] WithEd25519Authenticator(byte[] raw, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature) =>
        [.. raw, .. new BcsWriter().Variant(0).Bytes(publicKey).Bytes(signature).ToArray()];

    /// <summary>Hash of a signed user transaction: <c>sha3-256(sha3-256("APTOS::Transaction") ‖ 0x00 ‖ signed)</c>.</summary>
    public static string HashOf(ReadOnlySpan<byte> signedTransaction) =>
        "0x" + System.Convert.ToHexStringLower(CryptoNative.Sha3_256([.. TransactionPrefix, 0x00, .. signedTransaction]));

    public static ulong ExpiresIn(TimeSpan lifetime) => (ulong)DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
}
