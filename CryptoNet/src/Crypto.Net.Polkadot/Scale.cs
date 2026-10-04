using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Crypto.Net.Polkadot;

/// <summary>SCALE codec writer (Substrate's binary encoding).</summary>
public sealed class ScaleWriter
{
    private readonly MemoryStream _ms = new();

    public ScaleWriter U8(byte v) { _ms.WriteByte(v); return this; }
    public ScaleWriter Bool(bool v) => U8(v ? (byte)1 : (byte)0);

    public ScaleWriter U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _ms.Write(b); return this; }
    public ScaleWriter U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _ms.Write(b); return this; }
    public ScaleWriter U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _ms.Write(b); return this; }

    public ScaleWriter U128(BigInteger v)
    {
        if (v.Sign < 0 || v >= BigInteger.One << 128) throw new ArgumentOutOfRangeException(nameof(v));
        Span<byte> b = stackalloc byte[16];
        b.Clear();
        v.TryWriteBytes(b, out _, isUnsigned: true, isBigEndian: false);
        _ms.Write(b);
        return this;
    }

    /// <summary>Compact (variable-length) unsigned integer.</summary>
    public ScaleWriter Compact(BigInteger v)
    {
        _ms.Write(Scale.EncodeCompact(v));
        return this;
    }

    public ScaleWriter Bytes(ReadOnlySpan<byte> raw) { _ms.Write(raw); return this; }

    /// <summary>Length-prefixed byte vector (<c>Vec&lt;u8&gt;</c>).</summary>
    public ScaleWriter VecU8(ReadOnlySpan<byte> raw) => Compact(raw.Length).Bytes(raw);

    public ScaleWriter String(string s) => VecU8(Encoding.UTF8.GetBytes(s));

    public byte[] ToArray() => _ms.ToArray();
}

/// <summary>SCALE codec reader.</summary>
public ref struct ScaleReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ScaleReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly int Position => _pos;
    public readonly int Remaining => _data.Length - _pos;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new FormatException("SCALE data is truncated");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public bool Bool() => U8() switch { 0 => false, 1 => true, _ => throw new FormatException("Invalid bool") };
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public BigInteger U128() => new(Take(16), isUnsigned: true, isBigEndian: false);
    public ReadOnlySpan<byte> Bytes(int n) => Take(n);

    public BigInteger Compact()
    {
        byte b0 = U8();
        switch (b0 & 0b11)
        {
            case 0: return b0 >> 2;
            case 1: return (b0 | (U8() << 8)) >> 2;
            case 2: return (uint)(b0 | (U8() << 8) | (U8() << 16) | (U8() << 24)) >> 2;
            default:
                int len = (b0 >> 2) + 4;
                return new BigInteger(Take(len), isUnsigned: true, isBigEndian: false);
        }
    }

    public int CompactInt()
    {
        var v = Compact();
        return v <= int.MaxValue ? (int)v : throw new FormatException("Compact value too large");
    }

    public byte[] VecU8() => Take(CompactInt()).ToArray();
    public string String() => Encoding.UTF8.GetString(Take(CompactInt()));

    public bool Option() => U8() switch { 0 => false, 1 => true, _ => throw new FormatException("Invalid Option tag") };

    public string? OptionString() => Option() ? String() : null;

    public void SkipStrings()
    {
        int n = CompactInt();
        for (int i = 0; i < n; i++) String();
    }
}

/// <summary>SCALE helpers.</summary>
public static class Scale
{
    public static byte[] EncodeCompact(BigInteger v)
    {
        if (v.Sign < 0) throw new ArgumentOutOfRangeException(nameof(v));
        if (v < 1 << 6) return [(byte)((int)v << 2)];
        if (v < 1 << 14)
        {
            ushort x = (ushort)(((int)v << 2) | 1);
            return [(byte)x, (byte)(x >> 8)];
        }
        if (v < 1 << 30)
        {
            uint x = ((uint)v << 2) | 2;
            return [(byte)x, (byte)(x >> 8), (byte)(x >> 16), (byte)(x >> 24)];
        }
        byte[] raw = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (raw.Length > 67) throw new ArgumentOutOfRangeException(nameof(v));
        return [(byte)(((raw.Length - 4) << 2) | 3), .. raw];
    }
}
