using System.Numerics;
using System.Text;
using Crypto.Net.Native;

namespace Crypto.Net.Ton;

/// <summary>
/// A TON cell: up to 1023 data bits and up to 4 references. Cells are immutable; build them with
/// <see cref="TonCellBuilder"/> and read them with <see cref="TonCellSlice"/>.
/// </summary>
public sealed class TonCell : IEquatable<TonCell>
{
    public const int MaxBits = 1023;
    public const int MaxRefs = 4;

    private readonly byte[] _data;
    private byte[]? _hash;
    private int _depth = -1;

    internal TonCell(byte[] data, int bitLength, IReadOnlyList<TonCell> refs, bool isExotic = false)
    {
        if (bitLength > MaxBits) throw new ArgumentException($"A cell holds at most {MaxBits} bits");
        if (refs.Count > MaxRefs) throw new ArgumentException($"A cell holds at most {MaxRefs} references");
        _data = data;
        BitLength = bitLength;
        Refs = refs;
        IsExotic = isExotic;
    }

    public static TonCell Empty { get; } = new([], 0, []);

    public int BitLength { get; }
    public IReadOnlyList<TonCell> Refs { get; }
    public bool IsExotic { get; }

    /// <summary>The data bits, packed big-endian (the last byte is zero-padded).</summary>
    public ReadOnlySpan<byte> Data => _data;

    public TonCellSlice BeginParse() => new(this);

    /// <summary>Maximum depth of the reference tree below this cell.</summary>
    public int Depth
    {
        get
        {
            if (_depth < 0) _depth = Refs.Count == 0 ? 0 : Refs.Max(r => r.Depth) + 1;
            return _depth;
        }
    }

    /// <summary>The representation hash (sha256), which identifies the cell and, for a state init, the contract address.</summary>
    public byte[] Hash()
    {
        if (_hash is not null) return (byte[])_hash.Clone();
        if (IsExotic) throw new NotSupportedException("Hashing exotic (pruned, library or Merkle) cells is not supported");
        var repr = new List<byte>(2 + _data.Length + Refs.Count * 34) { Descriptor1, Descriptor2 };
        repr.AddRange(PaddedData());
        foreach (var r in Refs)
        {
            int d = r.Depth;
            repr.Add((byte)(d >> 8));
            repr.Add((byte)d);
        }
        foreach (var r in Refs) repr.AddRange(r.Hash());
        _hash = CryptoNative.Sha256([.. repr]);
        return (byte[])_hash.Clone();
    }

    internal byte Descriptor1 => (byte)(Refs.Count + (IsExotic ? 8 : 0));
    internal byte Descriptor2 => (byte)(BitLength / 8 + (BitLength + 7) / 8);

    /// <summary>Data bytes with the completion tag: a 1 bit then zeros when the length is not a multiple of 8.</summary>
    internal byte[] PaddedData()
    {
        int bytes = (BitLength + 7) / 8;
        byte[] d = _data.AsSpan(0, bytes).ToArray();
        if (BitLength % 8 != 0) d[bytes - 1] |= (byte)(0x80 >> (BitLength % 8));
        return d;
    }

    /// <summary>Serializes this cell as a bag of cells (with a CRC32-C checksum by default, like most TON tools).</summary>
    public byte[] ToBoc(bool withIndex = false, bool withCrc = true) => TonBoc.Serialize(this, withIndex, withCrc);

    public string ToBocBase64(bool withIndex = false, bool withCrc = true) => System.Convert.ToBase64String(ToBoc(withIndex, withCrc));

    public static TonCell FromBoc(ReadOnlySpan<byte> boc) => TonBoc.Deserialize(boc)[0];

    public static TonCell FromBocBase64(string boc) => FromBoc(System.Convert.FromBase64String(boc));

    public bool Equals(TonCell? other) => other is not null && Hash().AsSpan().SequenceEqual(other.Hash());
    public override bool Equals(object? obj) => Equals(obj as TonCell);
    public override int GetHashCode() => BitConverter.ToInt32(Hash(), 0);

    /// <summary>Fift-style hex of the data bits, with <c>_</c> marking a completion tag.</summary>
    public override string ToString()
    {
        if (BitLength % 4 == 0) return "x{" + System.Convert.ToHexString(_data)[..(BitLength / 4)] + "}";
        return "x{" + System.Convert.ToHexString(PaddedData())[..((BitLength + 4) / 4)] + "_}";
    }
}

/// <summary>Builds a <see cref="TonCell"/> bit by bit.</summary>
public sealed class TonCellBuilder
{
    private readonly byte[] _data = new byte[128];
    private readonly List<TonCell> _refs = [];

    public int BitLength { get; private set; }
    public int RemainingBits => TonCell.MaxBits - BitLength;
    public int RefCount => _refs.Count;

    public TonCellBuilder StoreBit(bool bit)
    {
        if (BitLength >= TonCell.MaxBits) throw new InvalidOperationException("Cell overflow: more than 1023 bits");
        if (bit) _data[BitLength / 8] |= (byte)(0x80 >> (BitLength % 8));
        BitLength++;
        return this;
    }

    public TonCellBuilder StoreBits(ReadOnlySpan<byte> data, int bitLength)
    {
        for (int i = 0; i < bitLength; i++) StoreBit((data[i / 8] & (0x80 >> (i % 8))) != 0);
        return this;
    }

    public TonCellBuilder StoreUInt(ulong value, int bits) => StoreUInt(new BigInteger(value), bits);

    public TonCellBuilder StoreUInt(BigInteger value, int bits)
    {
        if (bits < 0 || bits > 256) throw new ArgumentOutOfRangeException(nameof(bits));
        if (value.Sign < 0 || (bits < 256 ? value >= BigInteger.One << bits : value.GetBitLength() > 256))
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit in {bits} unsigned bits");
        for (int i = bits - 1; i >= 0; i--) StoreBit(!((value >> i) & 1).IsZero);
        return this;
    }

    public TonCellBuilder StoreInt(long value, int bits) => StoreInt(new BigInteger(value), bits);

    public TonCellBuilder StoreInt(BigInteger value, int bits)
    {
        if (bits < 1 || bits > 257) throw new ArgumentOutOfRangeException(nameof(bits));
        var limit = BigInteger.One << (bits - 1);
        if (value < -limit || value >= limit) throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit in {bits} signed bits");
        if (value.Sign < 0) value += BigInteger.One << bits;
        for (int i = bits - 1; i >= 0; i--) StoreBit(!((value >> i) & 1).IsZero);
        return this;
    }

    public TonCellBuilder StoreBytes(ReadOnlySpan<byte> bytes) => StoreBits(bytes, bytes.Length * 8);

    /// <summary><c>VarUInteger 16</c> (Grams/Coins): a 4-bit byte length then the value.</summary>
    public TonCellBuilder StoreCoins(BigInteger nanotons)
    {
        if (nanotons.Sign < 0) throw new ArgumentOutOfRangeException(nameof(nanotons));
        int len = nanotons.IsZero ? 0 : nanotons.GetByteCount(isUnsigned: true);
        if (len > 15) throw new ArgumentOutOfRangeException(nameof(nanotons), "Coins value exceeds 120 bits");
        StoreUInt((ulong)len, 4);
        return len == 0 ? this : StoreUInt(nanotons, len * 8);
    }

    /// <summary><c>addr_std</c> without anycast, or <c>addr_none</c> for null.</summary>
    public TonCellBuilder StoreAddress(TonAddress? address)
    {
        if (address is null) return StoreUInt(0, 2);
        return StoreUInt(2, 2).StoreBit(false).StoreInt(address.Workchain, 8).StoreBytes(address.Hash);
    }

    public TonCellBuilder StoreRef(TonCell cell)
    {
        if (_refs.Count >= TonCell.MaxRefs) throw new InvalidOperationException("Cell overflow: more than 4 references");
        _refs.Add(cell ?? throw new ArgumentNullException(nameof(cell)));
        return this;
    }

    public TonCellBuilder StoreMaybeRef(TonCell? cell) => cell is null ? StoreBit(false) : StoreBit(true).StoreRef(cell);

    /// <summary>Appends another cell's bits and references.</summary>
    public TonCellBuilder StoreSlice(TonCell cell)
    {
        StoreBits(cell.Data, cell.BitLength);
        foreach (var r in cell.Refs) StoreRef(r);
        return this;
    }

    /// <summary>UTF-8 text as a snake: as many bytes as fit here, the rest in a chain of referenced cells.</summary>
    public TonCellBuilder StoreStringTail(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int here = Math.Min(bytes.Length, RemainingBits / 8);
        StoreBytes(bytes.AsSpan(0, here));
        if (here < bytes.Length) StoreRef(new TonCellBuilder().StoreStringTail(Encoding.UTF8.GetString(bytes, here, bytes.Length - here)).EndCell());
        return this;
    }

    public TonCell EndCell() => new(_data.AsSpan(0, (BitLength + 7) / 8).ToArray(), BitLength, [.. _refs]);
}

/// <summary>Reads a cell's bits and references in order.</summary>
public sealed class TonCellSlice(TonCell cell)
{
    private int _bit;
    private int _ref;

    public int RemainingBits => cell.BitLength - _bit;
    public int RemainingRefs => cell.Refs.Count - _ref;

    public bool LoadBit()
    {
        if (_bit >= cell.BitLength) throw new FormatException("Cell underflow");
        bool b = (cell.Data[_bit / 8] & (0x80 >> (_bit % 8))) != 0;
        _bit++;
        return b;
    }

    public BigInteger LoadUIntBig(int bits)
    {
        if (bits > RemainingBits) throw new FormatException("Cell underflow");
        BigInteger v = BigInteger.Zero;
        for (int i = 0; i < bits; i++) v = (v << 1) | (LoadBit() ? BigInteger.One : BigInteger.Zero);
        return v;
    }

    public ulong LoadUInt(int bits) => bits <= 64 ? (ulong)LoadUIntBig(bits) : throw new ArgumentOutOfRangeException(nameof(bits));

    public long LoadInt(int bits)
    {
        var v = LoadUIntBig(bits);
        if (bits > 0 && !((v >> (bits - 1)) & 1).IsZero) v -= BigInteger.One << bits;
        return (long)v;
    }

    public byte[] LoadBytes(int count)
    {
        if (count * 8 > RemainingBits) throw new FormatException("Cell underflow");
        var b = new byte[count];
        for (int i = 0; i < count; i++) b[i] = (byte)LoadUInt(8);
        return b;
    }

    public BigInteger LoadCoins()
    {
        int len = (int)LoadUInt(4);
        return len == 0 ? BigInteger.Zero : LoadUIntBig(len * 8);
    }

    /// <summary>Reads <c>addr_none</c> (null) or <c>addr_std</c>.</summary>
    public TonAddress? LoadAddress()
    {
        switch (LoadUInt(2))
        {
            case 0:
                return null;
            case 2:
                if (LoadBit()) throw new NotSupportedException("Anycast addresses are not supported");
                int wc = (int)LoadInt(8);
                return new TonAddress(wc, LoadBytes(32));
            default:
                throw new NotSupportedException("Only addr_none and addr_std are supported");
        }
    }

    public TonCell LoadRef()
    {
        if (_ref >= cell.Refs.Count) throw new FormatException("No more references in the cell");
        return cell.Refs[_ref++];
    }

    public TonCell? LoadMaybeRef() => LoadBit() ? LoadRef() : null;

    /// <summary>Reads a snake string: the remaining bytes here, then the first reference chain.</summary>
    public string LoadStringTail()
    {
        var bytes = new List<byte>();
        var s = this;
        int guard = 0;
        while (true)
        {
            if (s.RemainingBits % 8 != 0) throw new FormatException("A string tail must be whole bytes");
            bytes.AddRange(s.LoadBytes(s.RemainingBits / 8));
            if (s.RemainingRefs == 0) break;
            if (++guard > 1024) throw new FormatException("String tail is too long");
            s = s.LoadRef().BeginParse();
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }
}
