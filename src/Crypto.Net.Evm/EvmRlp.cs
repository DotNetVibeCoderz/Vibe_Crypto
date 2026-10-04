using System.Numerics;

namespace Crypto.Net.Evm;

/// <summary>A decoded RLP item: either a byte string or a list of items.</summary>
public sealed class RlpItem
{
    private RlpItem(byte[]? bytes, IReadOnlyList<RlpItem>? items)
    {
        Bytes = bytes;
        Items = items;
    }

    public byte[]? Bytes { get; }
    public IReadOnlyList<RlpItem>? Items { get; }
    public bool IsList => Items is not null;

    internal static RlpItem String(byte[] b) => new(b, null);
    internal static RlpItem List(IReadOnlyList<RlpItem> items) => new(null, items);

    public BigInteger ToBigInteger() => Bytes is null ? throw new InvalidOperationException("Item is a list") : new BigInteger(Bytes, isUnsigned: true, isBigEndian: true);
    public ulong ToUInt64() => (ulong)ToBigInteger();
    public RlpItem this[int index] => Items is null ? throw new InvalidOperationException("Item is not a list") : Items[index];
}

/// <summary>Recursive Length Prefix encoding and decoding (Ethereum Yellow Paper, appendix B).</summary>
public static class EvmRlp
{
    public static byte[] EncodeBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length == 1 && data[0] < 0x80) return [data[0]];
        byte[] header = Header(0x80, data.Length);
        byte[] result = new byte[header.Length + data.Length];
        header.CopyTo(result, 0);
        data.CopyTo(result.AsSpan(header.Length));
        return result;
    }

    /// <summary>Encodes an unsigned integer with no leading zeros (zero encodes as 0x80).</summary>
    public static byte[] EncodeUInt(BigInteger value)
    {
        if (value.Sign < 0) throw new ArgumentOutOfRangeException(nameof(value), "RLP integers must be non-negative");
        return value.IsZero ? [0x80] : EncodeBytes(value.ToByteArray(isUnsigned: true, isBigEndian: true));
    }

    [Obsolete("Use EncodeUInt")]
    public static byte[] EncodeBigInteger(BigInteger value) => EncodeUInt(value);

    /// <summary>Encodes a list of already-encoded items.</summary>
    public static byte[] EncodeList(params byte[][] items)
    {
        int total = 0;
        foreach (var item in items) total += item.Length;
        byte[] header = Header(0xc0, total);
        byte[] result = new byte[header.Length + total];
        header.CopyTo(result, 0);
        int offset = header.Length;
        foreach (var item in items)
        {
            item.CopyTo(result, offset);
            offset += item.Length;
        }
        return result;
    }

    public static byte[] EncodeList(IEnumerable<byte[]> items) => EncodeList(items.ToArray());

    private static byte[] Header(byte offset, int length)
    {
        if (length <= 55) return [(byte)(offset + length)];
        byte[] len = new BigInteger(length).ToByteArray(isUnsigned: true, isBigEndian: true);
        return [(byte)(offset + 55 + len.Length), .. len];
    }

    /// <summary>Decodes exactly one RLP item spanning the whole input.</summary>
    public static RlpItem Decode(ReadOnlySpan<byte> data)
    {
        var item = DecodeItem(data, out int consumed);
        if (consumed != data.Length) throw new FormatException("Trailing bytes after RLP item");
        return item;
    }

    private static RlpItem DecodeItem(ReadOnlySpan<byte> data, out int consumed)
    {
        if (data.IsEmpty) throw new FormatException("Unexpected end of RLP data");
        byte prefix = data[0];

        if (prefix < 0x80)
        {
            consumed = 1;
            return RlpItem.String([prefix]);
        }
        if (prefix <= 0xb7)
        {
            int len = prefix - 0x80;
            Ensure(data, 1 + len);
            if (len == 1 && data[1] < 0x80) throw new FormatException("Non-canonical RLP single byte");
            consumed = 1 + len;
            return RlpItem.String(data.Slice(1, len).ToArray());
        }
        if (prefix <= 0xbf)
        {
            int lenOfLen = prefix - 0xb7;
            int len = ReadLength(data, lenOfLen);
            Ensure(data, 1 + lenOfLen + len);
            consumed = 1 + lenOfLen + len;
            return RlpItem.String(data.Slice(1 + lenOfLen, len).ToArray());
        }

        int listLen, headerLen;
        if (prefix <= 0xf7)
        {
            listLen = prefix - 0xc0;
            headerLen = 1;
        }
        else
        {
            int lenOfLen = prefix - 0xf7;
            listLen = ReadLength(data, lenOfLen);
            headerLen = 1 + lenOfLen;
        }
        Ensure(data, headerLen + listLen);
        var items = new List<RlpItem>();
        var payload = data.Slice(headerLen, listLen);
        while (!payload.IsEmpty)
        {
            items.Add(DecodeItem(payload, out int used));
            payload = payload[used..];
        }
        consumed = headerLen + listLen;
        return RlpItem.List(items);
    }

    private static int ReadLength(ReadOnlySpan<byte> data, int lenOfLen)
    {
        Ensure(data, 1 + lenOfLen);
        if (lenOfLen > 4 || data[1] == 0) throw new FormatException("Invalid RLP length prefix");
        int len = 0;
        for (int i = 0; i < lenOfLen; i++) len = (len << 8) | data[1 + i];
        if (len <= 55) throw new FormatException("Non-canonical RLP length");
        return len;
    }

    private static void Ensure(ReadOnlySpan<byte> data, int needed)
    {
        if (needed > data.Length || needed < 0) throw new FormatException("RLP data is truncated");
    }
}
