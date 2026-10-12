using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Crypto.Net.Native;

namespace Crypto.Net.Solana;

/// <summary>Borsh serializer (little-endian integers, u32 length prefixes), as used by Anchor and most Solana programs.</summary>
public sealed class BorshWriter
{
    private readonly MemoryStream _ms = new();

    public BorshWriter U8(byte v) { _ms.WriteByte(v); return this; }
    public BorshWriter I8(sbyte v) => U8((byte)v);
    public BorshWriter Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public BorshWriter U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _ms.Write(b); return this; }
    public BorshWriter I16(short v) => U16((ushort)v);
    public BorshWriter U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _ms.Write(b); return this; }
    public BorshWriter I32(int v) => U32((uint)v);
    public BorshWriter U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _ms.Write(b); return this; }
    public BorshWriter I64(long v) => U64((ulong)v);
    public BorshWriter F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));
    public BorshWriter F64(double v) => U64(BitConverter.DoubleToUInt64Bits(v));

    public BorshWriter U128(BigInteger v) => Integer(v, 16, signed: false);
    public BorshWriter I128(BigInteger v) => Integer(v, 16, signed: true);
    public BorshWriter U256(BigInteger v) => Integer(v, 32, signed: false);

    public BorshWriter Bytes(ReadOnlySpan<byte> raw) { _ms.Write(raw); return this; }
    public BorshWriter VecU8(ReadOnlySpan<byte> raw) => U32(checked((uint)raw.Length)).Bytes(raw);
    public BorshWriter String(string s) => VecU8(Encoding.UTF8.GetBytes(s));
    public BorshWriter PublicKey(string address) => Bytes(SolanaAddress.Decode(address));

    public BorshWriter Option<T>(T? value, Action<BorshWriter, T> write) where T : notnull
    {
        if (value is null) return U8(0);
        U8(1);
        write(this, value);
        return this;
    }

    public BorshWriter Vec<T>(IReadOnlyCollection<T> items, Action<BorshWriter, T> write)
    {
        U32(checked((uint)items.Count));
        foreach (var item in items) write(this, item);
        return this;
    }

    /// <summary>Fixed-size array: no length prefix.</summary>
    public BorshWriter Array<T>(IReadOnlyCollection<T> items, int length, Action<BorshWriter, T> write)
    {
        if (items.Count != length) throw new ArgumentException($"Expected {length} items, got {items.Count}", nameof(items));
        foreach (var item in items) write(this, item);
        return this;
    }

    private BorshWriter Integer(BigInteger v, int size, bool signed)
    {
        var buffer = new byte[size];
        if (!signed && v.Sign < 0) throw new ArgumentOutOfRangeException(nameof(v));
        if (!v.TryWriteBytes(buffer, out int written, isUnsigned: !signed, isBigEndian: false))
            throw new ArgumentOutOfRangeException(nameof(v), $"{v} does not fit in {size * 8} bits");
        if (signed && v.Sign < 0) buffer.AsSpan(written).Fill(0xFF);
        return Bytes(buffer);
    }

    public byte[] ToArray() => _ms.ToArray();
}

/// <summary>Borsh deserializer over a byte span.</summary>
public ref struct BorshReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public readonly int Position => _pos;
    public readonly int Remaining => _data.Length - _pos;

    public ReadOnlySpan<byte> Bytes(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new FormatException("Borsh data is truncated");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Bytes(1)[0];
    public sbyte I8() => (sbyte)U8();
    public bool Bool() => U8() switch { 0 => false, 1 => true, _ => throw new FormatException("Invalid Borsh bool") };
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
    public short I16() => (short)U16();
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
    public int I32() => (int)U32();
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
    public long I64() => (long)U64();
    public float F32() => BitConverter.UInt32BitsToSingle(U32());
    public double F64() => BitConverter.UInt64BitsToDouble(U64());
    public BigInteger U128() => new(Bytes(16), isUnsigned: true, isBigEndian: false);
    public BigInteger I128() => new(Bytes(16), isUnsigned: false, isBigEndian: false);
    public BigInteger U256() => new(Bytes(32), isUnsigned: true, isBigEndian: false);

    public byte[] VecU8()
    {
        uint n = U32();
        if (n > (uint)Remaining) throw new FormatException("Borsh length exceeds the data");
        return Bytes((int)n).ToArray();
    }

    public string String() => Encoding.UTF8.GetString(VecU8());
    public string PublicKey() => CryptoNative.Base58Encode(Bytes(32));
    public bool Option() => Bool();

    /// <summary>Reads a vector length and checks it against the remaining data (each element is at least one byte).</summary>
    public int VecLength()
    {
        uint n = U32();
        if (n > (uint)Remaining) throw new FormatException("Borsh vector length exceeds the data");
        return (int)n;
    }
}

/// <summary>Anchor conventions: 8-byte instruction and account discriminators.</summary>
public static class Anchor
{
    /// <summary><c>sha256("global:" + name)[..8]</c> for instruction <paramref name="name"/> (snake_case as in Rust).</summary>
    public static byte[] InstructionDiscriminator(string name) => CryptoNative.Sha256(Encoding.UTF8.GetBytes("global:" + name))[..8];

    /// <summary><c>sha256("account:" + Name)[..8]</c> for account type <paramref name="name"/> (PascalCase).</summary>
    public static byte[] AccountDiscriminator(string name) => CryptoNative.Sha256(Encoding.UTF8.GetBytes("account:" + name))[..8];

    /// <summary>Checks the discriminator of account data and returns the Borsh payload after it.</summary>
    public static ReadOnlySpan<byte> AccountPayload(ReadOnlySpan<byte> data, ReadOnlySpan<byte> discriminator)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual(discriminator)) throw new FormatException("Account discriminator does not match");
        return data[8..];
    }
}
