using System.Numerics;

namespace Crypto.Net.Core;

/// <summary>32-byte Move account addresses (Aptos and Sui), formatted per AIP-40.</summary>
public static class MoveAddress
{
    public const int Length = 32;

    /// <summary>
    /// Parses <c>0x</c> followed by 1–64 hex digits (short forms are left-padded with zeros). Without
    /// <paramref name="relaxed"/>, the prefix is required.
    /// </summary>
    public static byte[] Parse(string address, bool relaxed = true)
    {
        ArgumentNullException.ThrowIfNull(address);
        string hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..]
            : relaxed ? address : throw new FormatException("Move addresses start with 0x");
        if (hex.Length is 0 or > 64 || !hex.All(Uri.IsHexDigit)) throw new FormatException($"Invalid Move address '{address}'");
        return System.Convert.FromHexString(hex.PadLeft(64, '0'));
    }

    /// <summary>AIP-40 form: special addresses (<c>0x0</c>–<c>0xf</c>) short, every other address as 64 hex digits.</summary>
    public static string Format(ReadOnlySpan<byte> address)
    {
        if (address.Length != Length) throw new ArgumentException("Move addresses are 32 bytes", nameof(address));
        bool special = address[..31].IndexOfAnyExcept((byte)0) < 0 && address[31] < 16;
        return special ? "0x" + address[31].ToString("x", System.Globalization.CultureInfo.InvariantCulture) : "0x" + System.Convert.ToHexStringLower(address);
    }

    /// <summary>Full form: <c>0x</c> plus 64 hex digits, even for special addresses.</summary>
    public static string FormatLong(ReadOnlySpan<byte> address) => "0x" + System.Convert.ToHexStringLower(address);

    public static string Normalize(string address) => Format(Parse(address));
}

/// <summary>A Move type tag, e.g. <c>u64</c>, <c>vector&lt;u8&gt;</c> or <c>0x1::aptos_coin::AptosCoin</c>.</summary>
public abstract record MoveTypeTag
{
    /// <summary>Writes the BCS encoding of this type tag.</summary>
    public abstract void Write(BcsWriter w);

    /// <summary>The BCS encoding of this type tag.</summary>
    public byte[] ToBcs()
    {
        var w = new BcsWriter();
        Write(w);
        return w.ToArray();
    }

    public sealed record Primitive(string Name, uint Variant) : MoveTypeTag
    {
        public override void Write(BcsWriter w) => w.Variant(Variant);
        public override string ToString() => Name;
    }

    public sealed record Vector(MoveTypeTag Element) : MoveTypeTag
    {
        public override void Write(BcsWriter w)
        {
            w.Variant(6);
            Element.Write(w);
        }

        public override string ToString() => $"vector<{Element}>";
    }

    public sealed record Struct(string Address, string Module, string Name, IReadOnlyList<MoveTypeTag> TypeArgs) : MoveTypeTag
    {
        public override void Write(BcsWriter w)
        {
            w.Variant(7).Fixed(MoveAddress.Parse(Address)).String(Module).String(Name);
            w.Vector(TypeArgs, (bw, t) => t.Write(bw));
        }

        public override string ToString() =>
            $"{MoveAddress.Normalize(Address)}::{Module}::{Name}" + (TypeArgs.Count == 0 ? "" : $"<{string.Join(", ", TypeArgs)}>");
    }

    public static readonly MoveTypeTag Bool = new Primitive("bool", 0);
    public static readonly MoveTypeTag U8 = new Primitive("u8", 1);
    public static readonly MoveTypeTag U64 = new Primitive("u64", 2);
    public static readonly MoveTypeTag U128 = new Primitive("u128", 3);
    public static readonly MoveTypeTag AddressTag = new Primitive("address", 4);
    public static readonly MoveTypeTag Signer = new Primitive("signer", 5);
    public static readonly MoveTypeTag U16 = new Primitive("u16", 8);
    public static readonly MoveTypeTag U32 = new Primitive("u32", 9);
    public static readonly MoveTypeTag U256 = new Primitive("u256", 10);

    /// <summary>Reads a BCS-encoded type tag.</summary>
    public static MoveTypeTag Read(ref BcsReader r, int depth = 0)
    {
        if (depth > 32) throw new FormatException("Type tag nesting is too deep");
        ulong variant = r.Uleb128();
        switch (variant)
        {
            case 6:
                return new Vector(Read(ref r, depth + 1));
            case 7:
            {
                string address = MoveAddress.Format(r.Fixed(MoveAddress.Length));
                string module = r.String();
                string name = r.String();
                int n = r.Length();
                var args = new List<MoveTypeTag>(n);
                for (int i = 0; i < n; i++) args.Add(Read(ref r, depth + 1));
                return new Struct(address, module, name, args);
            }
            default:
                return variant switch
                {
                    0 => Bool, 1 => U8, 2 => U64, 3 => U128, 4 => AddressTag, 5 => Signer, 8 => U16, 9 => U32, 10 => U256,
                    _ => throw new FormatException($"Unknown type tag variant {variant}"),
                };
        }
    }

    /// <summary>Parses the textual form, e.g. <c>0x2::coin::Coin&lt;0x2::sui::SUI&gt;</c>.</summary>
    public static MoveTypeTag Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int pos = 0;
        var tag = ParseTag(text, ref pos);
        SkipSpaces(text, ref pos);
        if (pos != text.Length) throw new FormatException($"Unexpected '{text[pos..]}' in type tag '{text}'");
        return tag;
    }

    private static MoveTypeTag ParseTag(string s, ref int pos)
    {
        SkipSpaces(s, ref pos);
        int start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] is '_' or ':')) pos++;
        string head = s[start..pos];
        if (head.Length == 0) throw new FormatException($"Missing type in '{s}'");
        var args = new List<MoveTypeTag>();
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
        MoveAddress.Parse(parts[0]);
        return new Struct(parts[0], parts[1], parts[2], args);
    }

    private static void SkipSpaces(string s, ref int pos)
    {
        while (pos < s.Length && s[pos] == ' ') pos++;
    }
}

/// <summary>BCS encoders for Move call arguments (Aptos entry-function arguments, Sui pure inputs).</summary>
public static class MoveArg
{
    public static byte[] Address(string address) => MoveAddress.Parse(address);
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
