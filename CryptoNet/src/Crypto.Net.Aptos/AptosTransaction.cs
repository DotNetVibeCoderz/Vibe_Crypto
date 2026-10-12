using System.Numerics;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Aptos;

/// <summary>A Move type tag, e.g. <c>u64</c>, <c>vector&lt;u8&gt;</c> or <c>0x1::aptos_coin::AptosCoin</c>.</summary>
public abstract record AptosTypeTag
{
    internal abstract void Write(BcsWriter w);

    /// <summary>The BCS encoding of this type tag.</summary>
    public byte[] ToBcs()
    {
        var w = new BcsWriter();
        Write(w);
        return w.ToArray();
    }

    public sealed record Primitive(string Name, uint Variant) : AptosTypeTag
    {
        internal override void Write(BcsWriter w) => w.Variant(Variant);
        public override string ToString() => Name;
    }

    public sealed record Vector(AptosTypeTag Element) : AptosTypeTag
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(6);
            Element.Write(w);
        }

        public override string ToString() => $"vector<{Element}>";
    }

    public sealed record Struct(string Address, string Module, string Name, IReadOnlyList<AptosTypeTag> TypeArgs) : AptosTypeTag
    {
        internal override void Write(BcsWriter w)
        {
            w.Variant(7).Fixed(AptosAddress.Parse(Address)).String(Module).String(Name);
            w.Vector(TypeArgs, (bw, t) => t.Write(bw));
        }

        public override string ToString() =>
            $"{AptosAddress.Normalize(Address)}::{Module}::{Name}" + (TypeArgs.Count == 0 ? "" : $"<{string.Join(", ", TypeArgs)}>");
    }

    public static readonly AptosTypeTag Bool = new Primitive("bool", 0);
    public static readonly AptosTypeTag U8 = new Primitive("u8", 1);
    public static readonly AptosTypeTag U64 = new Primitive("u64", 2);
    public static readonly AptosTypeTag U128 = new Primitive("u128", 3);
    public static readonly AptosTypeTag AddressTag = new Primitive("address", 4);
    public static readonly AptosTypeTag Signer = new Primitive("signer", 5);
    public static readonly AptosTypeTag U16 = new Primitive("u16", 8);
    public static readonly AptosTypeTag U32 = new Primitive("u32", 9);
    public static readonly AptosTypeTag U256 = new Primitive("u256", 10);

    /// <summary>The APT coin type <c>0x1::aptos_coin::AptosCoin</c>.</summary>
    public static readonly AptosTypeTag AptosCoin = Parse("0x1::aptos_coin::AptosCoin");

    public static AptosTypeTag Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int pos = 0;
        var tag = ParseTag(text, ref pos);
        SkipSpaces(text, ref pos);
        if (pos != text.Length) throw new FormatException($"Unexpected '{text[pos..]}' in type tag '{text}'");
        return tag;
    }

    private static AptosTypeTag ParseTag(string s, ref int pos)
    {
        SkipSpaces(s, ref pos);
        int start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] is '_' or ':')) pos++;
        string head = s[start..pos];
        if (head.Length == 0) throw new FormatException($"Missing type in '{s}'");
        var args = new List<AptosTypeTag>();
        SkipSpaces(s, ref pos);
        if (pos < s.Length && s[pos] == '<')
        {
            pos++;
            while (true)
            {
                args.Add(ParseTag(s, ref pos));
                SkipSpaces(s, ref pos);
                if (pos >= s.Length) throw new FormatException($"Unclosed '<' in '{s}'");
                char c = s[pos++];
                if (c == '>') break;
                if (c != ',') throw new FormatException($"Expected ',' or '>' in '{s}'");
            }
        }

        switch (head)
        {
            case "bool": case "u8": case "u16": case "u32": case "u64": case "u128": case "u256": case "address": case "signer":
                if (args.Count != 0) throw new FormatException($"'{head}' takes no type arguments");
                return head switch
                {
                    "bool" => Bool, "u8" => U8, "u16" => U16, "u32" => U32, "u64" => U64,
                    "u128" => U128, "u256" => U256, "address" => AddressTag, _ => Signer,
                };
            case "vector":
                if (args.Count != 1) throw new FormatException("vector takes exactly one type argument");
                return new Vector(args[0]);
        }

        string[] parts = head.Split("::");
        if (parts.Length != 3 || parts.Any(p => p.Length == 0)) throw new FormatException($"Invalid struct type '{head}'");
        AptosAddress.Parse(parts[0]);
        return new Struct(parts[0], parts[1], parts[2], args);
    }

    private static void SkipSpaces(string s, ref int pos)
    {
        while (pos < s.Length && s[pos] == ' ') pos++;
    }
}

/// <summary>BCS encoders for entry function arguments (each argument is passed as its own BCS bytes).</summary>
public static class AptosArg
{
    public static byte[] Address(string address) => AptosAddress.Parse(address);
    public static byte[] Bool(bool v) => new BcsWriter().Bool(v).ToArray();
    public static byte[] U8(byte v) => [v];
    public static byte[] U16(ushort v) => new BcsWriter().U16(v).ToArray();
    public static byte[] U32(uint v) => new BcsWriter().U32(v).ToArray();
    public static byte[] U64(ulong v) => new BcsWriter().U64(v).ToArray();
    public static byte[] U128(BigInteger v) => new BcsWriter().U128(v).ToArray();
    public static byte[] U256(BigInteger v) => new BcsWriter().U256(v).ToArray();

    /// <summary><c>0x1::string::String</c>.</summary>
    public static byte[] String(string v) => new BcsWriter().String(v).ToArray();

    /// <summary><c>vector&lt;u8&gt;</c>.</summary>
    public static byte[] Bytes(ReadOnlySpan<byte> v) => new BcsWriter().Bytes(v).ToArray();

    /// <summary><c>vector&lt;T&gt;</c> from already-encoded elements.</summary>
    public static byte[] Vector(IReadOnlyList<byte[]> encodedElements)
    {
        var w = new BcsWriter();
        return w.Vector(encodedElements, (bw, e) => bw.Fixed(e)).ToArray();
    }

    /// <summary><c>0x1::option::Option&lt;T&gt;</c> (a vector of zero or one element).</summary>
    public static byte[] Option(byte[]? encoded) => encoded is null ? [0] : [1, .. encoded];
}

/// <summary>An entry function call, the usual transaction payload.</summary>
public sealed record AptosEntryFunction(string ModuleAddress, string Module, string Function, IReadOnlyList<AptosTypeTag> TypeArgs, IReadOnlyList<byte[]> Args)
{
    /// <summary>Builds a call from <c>"0x1::module::function"</c>.</summary>
    public static AptosEntryFunction Create(string function, IReadOnlyList<AptosTypeTag>? typeArgs = null, params byte[][] args)
    {
        string[] parts = function.Split("::");
        if (parts.Length != 3) throw new FormatException($"Expected address::module::function, got '{function}'");
        AptosAddress.Parse(parts[0]);
        return new AptosEntryFunction(parts[0], parts[1], parts[2], typeArgs ?? [], args);
    }

    /// <summary><c>0x1::aptos_account::transfer(to, octas)</c>: sends APT and creates the recipient account if needed.</summary>
    public static AptosEntryFunction Transfer(string to, ulong octas) =>
        Create("0x1::aptos_account::transfer", null, AptosArg.Address(to), AptosArg.U64(octas));

    /// <summary><c>0x1::aptos_account::transfer_coins&lt;CoinType&gt;(to, amount)</c>.</summary>
    public static AptosEntryFunction TransferCoins(string coinType, string to, ulong amount) =>
        Create("0x1::aptos_account::transfer_coins", [AptosTypeTag.Parse(coinType)], AptosArg.Address(to), AptosArg.U64(amount));

    /// <summary><c>0x1::primary_fungible_store::transfer(metadata, to, amount)</c> for fungible assets.</summary>
    public static AptosEntryFunction TransferFungibleAsset(string metadataAddress, string to, ulong amount) =>
        Create("0x1::primary_fungible_store::transfer", [AptosTypeTag.Parse("0x1::fungible_asset::Metadata")],
            AptosArg.Address(metadataAddress), AptosArg.Address(to), AptosArg.U64(amount));

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
