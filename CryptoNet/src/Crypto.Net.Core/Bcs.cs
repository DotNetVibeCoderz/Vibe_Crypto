using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Crypto.Net.Core;

/// <summary>
/// BCS (Binary Canonical Serialization) writer, as used by Aptos and Sui: little-endian integers and ULEB128 lengths
/// for sequences, strings and enum variant indexes.
/// </summary>
public sealed class BcsWriter
{
    private readonly MemoryStream _ms = new();

    public BcsWriter U8(byte v) { _ms.WriteByte(v); return this; }
    public BcsWriter Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public BcsWriter U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _ms.Write(b); return this; }
    public BcsWriter U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _ms.Write(b); return this; }
    public BcsWriter U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _ms.Write(b); return this; }
    public BcsWriter U128(BigInteger v) => UInt(v, 16);
    public BcsWriter U256(BigInteger v) => UInt(v, 32);

    public BcsWriter Uleb128(ulong v)
    {
        do
        {
            byte b = (byte)(v & 0x7f);
            v >>= 7;
            if (v != 0) b |= 0x80;
            _ms.WriteByte(b);
        }
        while (v != 0);
        return this;
    }

    /// <summary>Raw bytes with no length (fixed-size arrays such as 32-byte addresses).</summary>
    public BcsWriter Fixed(ReadOnlySpan<byte> bytes) { _ms.Write(bytes); return this; }

    /// <summary><c>vector&lt;u8&gt;</c>: ULEB128 length then bytes.</summary>
    public BcsWriter Bytes(ReadOnlySpan<byte> bytes) => Uleb128((ulong)bytes.Length).Fixed(bytes);

    public BcsWriter String(string s) => Bytes(Encoding.UTF8.GetBytes(s));

    public BcsWriter Vector<T>(IReadOnlyCollection<T> items, Action<BcsWriter, T> write)
    {
        Uleb128((ulong)items.Count);
        foreach (var item in items) write(this, item);
        return this;
    }

    public BcsWriter Option<T>(T? value, Action<BcsWriter, T> write) where T : notnull
    {
        if (value is null) return U8(0);
        U8(1);
        write(this, value);
        return this;
    }

    /// <summary>Enum variant index (ULEB128).</summary>
    public BcsWriter Variant(uint index) => Uleb128(index);

    private BcsWriter UInt(BigInteger v, int size)
    {
        if (v.Sign < 0 || v.GetByteCount(isUnsigned: true) > size) throw new ArgumentOutOfRangeException(nameof(v), $"{v} does not fit in u{size * 8}");
        var b = new byte[size];
        v.TryWriteBytes(b, out _, isUnsigned: true, isBigEndian: false);
        return Fixed(b);
    }

    public byte[] ToArray() => _ms.ToArray();
}

/// <summary>BCS reader over a byte span.</summary>
public ref struct BcsReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public readonly int Remaining => _data.Length - _pos;
    public readonly int Position => _pos;

    public ReadOnlySpan<byte> Fixed(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new FormatException("BCS data is truncated");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Fixed(1)[0];
    public bool Bool() => U8() switch { 0 => false, 1 => true, _ => throw new FormatException("Invalid BCS bool") };
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Fixed(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Fixed(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Fixed(8));
    public BigInteger U128() => new(Fixed(16), isUnsigned: true, isBigEndian: false);
    public BigInteger U256() => new(Fixed(32), isUnsigned: true, isBigEndian: false);

    public ulong Uleb128()
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte b = U8();
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new FormatException("ULEB128 value too long");
    }

    public int Length()
    {
        ulong n = Uleb128();
        if (n > (ulong)Remaining) throw new FormatException("BCS length exceeds the data");
        return (int)n;
    }

    public byte[] Bytes() => Fixed(Length()).ToArray();
    public string String() => Encoding.UTF8.GetString(Fixed(Length()));
}
