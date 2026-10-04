using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Evm;

/// <summary>A Solidity ABI type (see the Solidity ABI specification).</summary>
public abstract record AbiType
{
    /// <summary>Canonical type string, e.g. <c>uint256</c>, <c>(address,uint256)[]</c>.</summary>
    public abstract string Name { get; }

    /// <summary>True for <c>bytes</c>, <c>string</c>, <c>T[]</c> and containers of dynamic types.</summary>
    public abstract bool IsDynamic { get; }

    /// <summary>Head size in bytes for static types.</summary>
    internal virtual int StaticSize => 32;

    public override string ToString() => Name;

    public sealed record UInt(int Bits) : AbiType
    {
        public override string Name => $"uint{Bits}";
        public override bool IsDynamic => false;
    }

    public sealed record Int(int Bits) : AbiType
    {
        public override string Name => $"int{Bits}";
        public override bool IsDynamic => false;
    }

    public sealed record AddressType : AbiType
    {
        public override string Name => "address";
        public override bool IsDynamic => false;
    }

    public sealed record Bool : AbiType
    {
        public override string Name => "bool";
        public override bool IsDynamic => false;
    }

    public sealed record FixedBytes(int Size) : AbiType
    {
        public override string Name => $"bytes{Size}";
        public override bool IsDynamic => false;
    }

    public sealed record Bytes : AbiType
    {
        public override string Name => "bytes";
        public override bool IsDynamic => true;
    }

    public sealed record String : AbiType
    {
        public override string Name => "string";
        public override bool IsDynamic => true;
    }

    /// <summary><c>T[]</c> when <paramref name="Length"/> is null, otherwise <c>T[k]</c>.</summary>
    public sealed record Array(AbiType Element, int? Length) : AbiType
    {
        public override string Name => $"{Element.Name}[{Length}]";
        public override bool IsDynamic => Length is null || Element.IsDynamic;
        internal override int StaticSize => IsDynamic ? 32 : Length!.Value * Element.StaticSize;
    }

    public sealed record Tuple(IReadOnlyList<AbiType> Components) : AbiType
    {
        public override string Name => "(" + string.Join(",", Components.Select(c => c.Name)) + ")";
        public override bool IsDynamic => Components.Any(c => c.IsDynamic);
        internal override int StaticSize => IsDynamic ? 32 : Components.Sum(c => c.StaticSize);
        public bool Equals(Tuple? other) => other is not null && Components.SequenceEqual(other.Components);
        public override int GetHashCode() => Components.Aggregate(17, (h, c) => HashCode.Combine(h, c));
    }

    /// <summary>Parses a type string such as <c>uint256</c>, <c>bytes32[2]</c> or <c>(address,(uint8,bool)[])</c>.</summary>
    public static AbiType Parse(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        type = type.Replace(" ", "");

        if (type.EndsWith(']'))
        {
            int open = type.LastIndexOf('[');
            string inner = type[(open + 1)..^1];
            var element = Parse(type[..open]);
            return new Array(element, inner.Length == 0 ? null : int.Parse(inner, CultureInfo.InvariantCulture));
        }

        if (type.StartsWith('(') && type.EndsWith(')'))
            return new Tuple(SplitTopLevel(type[1..^1]).Select(Parse).ToList());

        switch (type)
        {
            case "address": return new AddressType();
            case "bool": return new Bool();
            case "string": return new String();
            case "bytes": return new Bytes();
            case "uint": return new UInt(256);
            case "int": return new Int(256);
            case "function": return new FixedBytes(24);
        }

        if (type.StartsWith("uint") && int.TryParse(type.AsSpan(4), out int ub) && ub is >= 8 and <= 256 && ub % 8 == 0)
            return new UInt(ub);
        if (type.StartsWith("int") && int.TryParse(type.AsSpan(3), out int ib) && ib is >= 8 and <= 256 && ib % 8 == 0)
            return new Int(ib);
        if (type.StartsWith("bytes") && int.TryParse(type.AsSpan(5), out int bn) && bn is >= 1 and <= 32)
            return new FixedBytes(bn);

        throw new FormatException($"Unsupported ABI type '{type}'");
    }

    /// <summary>Splits a comma-separated list, ignoring commas inside parentheses.</summary>
    internal static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        if (s.Length == 0) return parts;
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }
}

/// <summary>
/// Solidity ABI encoder/decoder. Values map to .NET types as follows:
/// integers → <see cref="BigInteger"/> (any integral type or decimal string accepted on input),
/// address → <see cref="string"/> (EIP-55) or <see cref="Address"/>, bool → <see cref="bool"/>,
/// bytes/bytesN → <see cref="byte"/>[] (hex string accepted), string → <see cref="string"/>,
/// arrays and tuples → <see cref="IList"/> / <c>object?[]</c>.
/// </summary>
public static class EvmAbi
{
    /// <summary>First 4 bytes of keccak256 of the canonical signature, e.g. <c>transfer(address,uint256)</c>.</summary>
    public static byte[] GetFunctionSelector(string signature) => CryptoNative.Keccak256(Encoding.UTF8.GetBytes(Canonicalize(signature)))[..4];

    /// <summary>keccak256 of an event signature: topic[0] of its logs.</summary>
    public static byte[] GetEventTopic(string signature) => CryptoNative.Keccak256(Encoding.UTF8.GetBytes(Canonicalize(signature)));

    /// <summary>Normalizes <c>transfer(address to, uint amount)</c> to <c>transfer(address,uint256)</c>.</summary>
    public static string Canonicalize(string signature)
    {
        var (name, types) = ParseSignature(signature);
        return $"{name}({string.Join(",", types.Select(t => t.Name))})";
    }

    /// <summary>Parses <c>name(type1,type2)</c>; parameter names after the types are ignored.</summary>
    public static (string Name, IReadOnlyList<AbiType> Types) ParseSignature(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        int open = signature.IndexOf('(');
        if (open < 0 || !signature.TrimEnd().EndsWith(')')) throw new FormatException($"Invalid signature '{signature}'");
        string name = signature[..open].Trim();
        string args = signature.Trim()[(open + 1)..^1];
        var types = AbiType.SplitTopLevel(args)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Select(a =>
            {
                // drop parameter names / data locations ("uint256 amount", "bytes memory data")
                int depth = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] == '(') depth++;
                    else if (a[i] == ')') depth--;
                    else if (a[i] == ' ' && depth == 0) return a[..i];
                }
                return a;
            })
            .Select(AbiType.Parse)
            .ToList();
        return (name, types);
    }

    /// <summary>Encodes a function call: selector followed by the ABI-encoded arguments.</summary>
    public static byte[] EncodeFunctionCall(string signature, params object?[] args)
    {
        var (_, types) = ParseSignature(signature);
        byte[] encoded = Encode(types, args);
        byte[] selector = GetFunctionSelector(signature);
        return [.. selector, .. encoded];
    }

    /// <summary>Encodes a list of values according to <paramref name="types"/> (as a tuple).</summary>
    public static byte[] Encode(IReadOnlyList<AbiType> types, IReadOnlyList<object?> values)
    {
        if (types.Count != values.Count) throw new ArgumentException($"Expected {types.Count} values, got {values.Count}");
        using var ms = new MemoryStream();
        EncodeTuple(ms, types, values);
        return ms.ToArray();
    }

    public static byte[] Encode(string types, params object?[] values) =>
        Encode(AbiType.SplitTopLevel(types.Replace(" ", "")).Where(t => t.Length > 0).Select(AbiType.Parse).ToList(), values);

    /// <summary>Decodes ABI data into .NET values.</summary>
    public static object?[] Decode(IReadOnlyList<AbiType> types, ReadOnlySpan<byte> data) => DecodeTuple(types, data, 0);

    public static object?[] Decode(string types, ReadOnlySpan<byte> data) =>
        Decode(AbiType.SplitTopLevel(types.Replace(" ", "")).Where(t => t.Length > 0).Select(AbiType.Parse).ToList(), data);

    #region Word helpers

    public static byte[] EncodeUint256(BigInteger value) => EncodeWord(new AbiType.UInt(256), value);

    public static BigInteger DecodeUint256(ReadOnlySpan<byte> word)
    {
        if (word.Length < 32) throw new ArgumentException("Word must be 32 bytes", nameof(word));
        return new BigInteger(word[..32], isUnsigned: true, isBigEndian: true);
    }

    public static byte[] EncodeAddress(Address address) => EncodeWord(new AbiType.AddressType(), address.Value);

    public static Address DecodeAddress(ReadOnlySpan<byte> word, ChainId? chain = null) =>
        new(EvmAddress.ToChecksumAddress(word.Slice(12, 20)), chain ?? ChainId.Ethereum);

    #endregion

    private static void EncodeTuple(Stream output, IReadOnlyList<AbiType> types, IReadOnlyList<object?> values)
    {
        int headSize = types.Sum(t => t.StaticSize);
        using var tail = new MemoryStream();
        for (int i = 0; i < types.Count; i++)
        {
            var type = types[i];
            if (type.IsDynamic)
            {
                output.Write(EncodeWord(new AbiType.UInt(256), new BigInteger(headSize + tail.Length)));
                EncodeValue(tail, type, values[i]);
            }
            else
            {
                EncodeValue(output, type, values[i]);
            }
        }
        tail.Position = 0;
        tail.CopyTo(output);
    }

    private static void EncodeValue(Stream output, AbiType type, object? value)
    {
        switch (type)
        {
            case AbiType.Bytes:
                WriteDynamicBytes(output, ToBytes(value));
                break;
            case AbiType.String:
                WriteDynamicBytes(output, Encoding.UTF8.GetBytes(value as string ?? throw new ArgumentException("Expected string")));
                break;
            case AbiType.Array arr:
                var items = ToList(value);
                if (arr.Length is int fixedLen)
                {
                    if (items.Count != fixedLen) throw new ArgumentException($"{arr.Name} expects {fixedLen} items, got {items.Count}");
                }
                else
                {
                    output.Write(EncodeWord(new AbiType.UInt(256), new BigInteger(items.Count)));
                }
                EncodeTuple(output, Enumerable.Repeat(arr.Element, items.Count).ToList(), items);
                break;
            case AbiType.Tuple tuple:
                EncodeTuple(output, tuple.Components, ToList(value));
                break;
            default:
                output.Write(EncodeWord(type, value));
                break;
        }
    }

    private static void WriteDynamicBytes(Stream output, byte[] data)
    {
        output.Write(EncodeWord(new AbiType.UInt(256), new BigInteger(data.Length)));
        output.Write(data);
        int pad = (32 - data.Length % 32) % 32;
        if (pad > 0) output.Write(new byte[pad]);
    }

    private static byte[] EncodeWord(AbiType type, object? value)
    {
        var word = new byte[32];
        switch (type)
        {
            case AbiType.UInt u:
            {
                BigInteger v = ToBigInteger(value);
                if (v.Sign < 0 || v >= BigInteger.One << u.Bits) throw new ArgumentOutOfRangeException(nameof(value), $"{v} does not fit in {u.Name}");
                WriteBigEndian(v, word);
                break;
            }
            case AbiType.Int s:
            {
                BigInteger v = ToBigInteger(value);
                BigInteger limit = BigInteger.One << (s.Bits - 1);
                if (v < -limit || v >= limit) throw new ArgumentOutOfRangeException(nameof(value), $"{v} does not fit in {s.Name}");
                if (v.Sign < 0) v += BigInteger.One << 256;
                WriteBigEndian(v, word);
                break;
            }
            case AbiType.AddressType:
            {
                string a = value switch
                {
                    Address addr => addr.Value,
                    string str => str,
                    byte[] raw when raw.Length == 20 => "0x" + Convert.ToHexString(raw),
                    _ => throw new ArgumentException("Expected an address"),
                };
                if (!EvmAddress.IsValid(a)) throw new FormatException($"Invalid EVM address '{a}'");
                HexUtil.Decode(a).CopyTo(word, 12);
                break;
            }
            case AbiType.Bool:
                word[31] = value switch { bool b => b ? (byte)1 : (byte)0, _ => throw new ArgumentException("Expected bool") };
                break;
            case AbiType.FixedBytes fb:
            {
                byte[] b = ToBytes(value);
                if (b.Length != fb.Size) throw new ArgumentException($"{fb.Name} expects {fb.Size} bytes, got {b.Length}");
                b.CopyTo(word, 0);
                break;
            }
            default:
                throw new NotSupportedException(type.Name);
        }
        return word;
    }

    private static void WriteBigEndian(BigInteger v, Span<byte> word)
    {
        int count = v.GetByteCount(isUnsigned: true);
        v.TryWriteBytes(word[(32 - count)..], out _, isUnsigned: true, isBigEndian: true);
    }

    private static object?[] DecodeTuple(IReadOnlyList<AbiType> types, ReadOnlySpan<byte> data, int start)
    {
        var result = new object?[types.Count];
        int head = start;
        for (int i = 0; i < types.Count; i++)
        {
            var type = types[i];
            if (type.IsDynamic)
            {
                int offset = ReadInt(data, head);
                result[i] = DecodeValue(type, data, start + offset);
                head += 32;
            }
            else
            {
                result[i] = DecodeValue(type, data, head);
                head += type.StaticSize;
            }
        }
        return result;
    }

    private static object? DecodeValue(AbiType type, ReadOnlySpan<byte> data, int pos)
    {
        switch (type)
        {
            case AbiType.UInt:
                return new BigInteger(Slice(data, pos, 32), isUnsigned: true, isBigEndian: true);
            case AbiType.Int:
                return new BigInteger(Slice(data, pos, 32), isUnsigned: false, isBigEndian: true);
            case AbiType.AddressType:
                return EvmAddress.ToChecksumAddress(Slice(data, pos + 12, 20));
            case AbiType.Bool:
                return Slice(data, pos, 32)[31] != 0;
            case AbiType.FixedBytes fb:
                return Slice(data, pos, 32)[..fb.Size].ToArray();
            case AbiType.Bytes:
            {
                int len = ReadInt(data, pos);
                return Slice(data, pos + 32, len).ToArray();
            }
            case AbiType.String:
            {
                int len = ReadInt(data, pos);
                return Encoding.UTF8.GetString(Slice(data, pos + 32, len));
            }
            case AbiType.Array arr:
            {
                int count = arr.Length ?? ReadInt(data, pos);
                int start = arr.Length is null ? pos + 32 : pos;
                return DecodeTuple(Enumerable.Repeat(arr.Element, count).ToList(), data, start);
            }
            case AbiType.Tuple tuple:
                return DecodeTuple(tuple.Components, data, pos);
            default:
                throw new NotSupportedException(type.Name);
        }
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int pos, int len)
    {
        if (pos < 0 || len < 0 || pos + len > data.Length) throw new FormatException("ABI data is truncated");
        return data.Slice(pos, len);
    }

    private static int ReadInt(ReadOnlySpan<byte> data, int pos)
    {
        var v = new BigInteger(Slice(data, pos, 32), isUnsigned: true, isBigEndian: true);
        if (v > data.Length) throw new FormatException("ABI offset/length out of range");
        return (int)v;
    }

    private static BigInteger ToBigInteger(object? value) => value switch
    {
        BigInteger b => b,
        int i => i,
        long l => l,
        uint u => u,
        ulong ul => ul,
        short s => s,
        ushort us => us,
        byte by => by,
        sbyte sb => sb,
        decimal d when d == decimal.Truncate(d) => new BigInteger(d),
        Amount a => a.BaseUnits,
        string s when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => HexUtil.ParseQuantity(s),
        string s => BigInteger.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"Cannot convert {value?.GetType().Name ?? "null"} to an integer"),
    };

    private static byte[] ToBytes(object? value) => value switch
    {
        byte[] b => b,
        ReadOnlyMemory<byte> m => m.ToArray(),
        Memory<byte> m => m.ToArray(),
        string s => HexUtil.Decode(s),
        _ => throw new ArgumentException($"Cannot convert {value?.GetType().Name ?? "null"} to bytes"),
    };

    private static IReadOnlyList<object?> ToList(object? value) => value switch
    {
        IReadOnlyList<object?> list => list,
        IEnumerable e and not string and not byte[] => e.Cast<object?>().ToList(),
        _ => throw new ArgumentException("Expected an array or tuple"),
    };
}
