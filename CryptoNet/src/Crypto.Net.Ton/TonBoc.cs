using System.Buffers.Binary;
using System.Numerics;

namespace Crypto.Net.Ton;

/// <summary>Bag-of-cells serialization (<c>b5ee9c72</c>), with the same cell order as @ton/core.</summary>
public static class TonBoc
{
    private const uint Magic = 0xb5ee9c72;
    private const int MaxCells = 1 << 20;
    private const int MaxDepth = 1024;

    public static byte[] Serialize(TonCell root, bool withIndex = false, bool withCrc = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        var (cells, refIndexes) = TopologicalSort(root);
        int count = cells.Count;
        int sizeBytes = Math.Max(1, (BitsFor(count) + 7) / 8);
        var ends = new int[count];
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            total += 2 + (cells[i].BitLength + 7) / 8 + cells[i].Refs.Count * sizeBytes;
            ends[i] = total;
        }
        int offsetBytes = Math.Max(1, (BitsFor(total) + 7) / 8);

        var o = new List<byte>(16 + total + count * offsetBytes);
        o.AddRange([0xb5, 0xee, 0x9c, 0x72]);
        o.Add((byte)((withIndex ? 0x80 : 0) | (withCrc ? 0x40 : 0) | sizeBytes));
        o.Add((byte)offsetBytes);
        WriteUInt(o, (ulong)count, sizeBytes);
        WriteUInt(o, 1, sizeBytes);           // roots
        WriteUInt(o, 0, sizeBytes);           // absent
        WriteUInt(o, (ulong)total, offsetBytes);
        WriteUInt(o, 0, sizeBytes);           // root index
        if (withIndex) foreach (int e in ends) WriteUInt(o, (ulong)e, offsetBytes);
        for (int i = 0; i < count; i++)
        {
            var c = cells[i];
            o.Add(c.Descriptor1);
            o.Add(c.Descriptor2);
            o.AddRange(c.PaddedData());
            foreach (int r in refIndexes[i]) WriteUInt(o, (ulong)r, sizeBytes);
        }
        if (withCrc)
        {
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32C([.. o]));
            o.AddRange(crc.ToArray());
        }
        return [.. o];
    }

    /// <summary>Parses a bag of cells and returns its root cells.</summary>
    public static IReadOnlyList<TonCell> Deserialize(ReadOnlySpan<byte> boc)
    {
        if (boc.Length < 6 || BinaryPrimitives.ReadUInt32BigEndian(boc) != Magic) throw new FormatException("Not a bag of cells");
        byte flags = boc[4];
        bool hasIndex = (flags & 0x80) != 0, hasCrc = (flags & 0x40) != 0;
        int sizeBytes = flags & 0x07;
        int offsetBytes = boc[5];
        if (sizeBytes is < 1 or > 4 || offsetBytes is < 1 or > 8) throw new FormatException("Invalid BoC header");
        if (hasCrc)
        {
            if (boc.Length < 10 || Crc32C(boc[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(boc[^4..])) throw new FormatException("BoC checksum mismatch");
            boc = boc[..^4];
        }

        int pos = 6;
        int count = ReadSize(boc, ref pos, sizeBytes);
        int roots = ReadSize(boc, ref pos, sizeBytes);
        int absent = ReadSize(boc, ref pos, sizeBytes);
        ulong totalSize = ReadUInt(boc, ref pos, offsetBytes);
        if (count is 0 or > MaxCells || roots < 1 || roots > count || absent != 0) throw new FormatException("Invalid BoC cell counts");
        var rootIndexes = new int[roots];
        for (int i = 0; i < roots; i++) rootIndexes[i] = ReadSize(boc, ref pos, sizeBytes);
        if (hasIndex) pos = checked(pos + count * offsetBytes);
        if (pos > boc.Length || totalSize != (ulong)(boc.Length - pos)) throw new FormatException("BoC size mismatch");

        var raw = new (byte[] Data, int Bits, int[] Refs, bool Exotic)[count];
        for (int i = 0; i < count; i++)
        {
            if (pos + 2 > boc.Length) throw new FormatException("BoC is truncated");
            byte d1 = boc[pos++], d2 = boc[pos++];
            int refCount = d1 & 7;
            bool exotic = (d1 & 8) != 0;
            if (refCount > TonCell.MaxRefs || (d1 & 0x10) != 0) throw new FormatException("Unsupported cell descriptor");
            int byteLen = (d2 + 1) / 2;
            if (pos + byteLen > boc.Length) throw new FormatException("BoC is truncated");
            byte[] data = boc.Slice(pos, byteLen).ToArray();
            pos += byteLen;
            int bits = byteLen * 8;
            if ((d2 & 1) != 0)
            {
                // Strip the completion tag: the last 1 bit and the zeros after it.
                if (byteLen == 0 || data[^1] == 0) throw new FormatException("Missing completion tag");
                int trailing = BitOperations.TrailingZeroCount(data[^1]);
                bits -= trailing + 1;
                data[^1] &= (byte)(0xff << (trailing + 1));
            }
            var refs = new int[refCount];
            for (int r = 0; r < refCount; r++)
            {
                refs[r] = ReadSize(boc, ref pos, sizeBytes);
                if (refs[r] <= i || refs[r] >= count) throw new FormatException("BoC references must point to later cells");
            }
            raw[i] = (data, bits, refs, exotic);
        }
        if (pos != boc.Length) throw new FormatException("Trailing bytes in BoC");

        var built = new TonCell[count];
        for (int i = count - 1; i >= 0; i--)
        {
            var (data, bits, refs, exotic) = raw[i];
            built[i] = new TonCell(data, bits, refs.Select(r => built[r]).ToArray(), exotic);
            // Children are built first, so Depth is computed incrementally; the limit bounds hash recursion.
            if (built[i].Depth > MaxDepth) throw new FormatException($"Cell tree is deeper than {MaxDepth}");
        }
        return rootIndexes.Select(r => r < count ? built[r] : throw new FormatException("Invalid root index")).ToArray();
    }

    // Same order as @ton/core: breadth-first discovery, then a depth-first topological sort.
    private static (List<TonCell> Cells, List<int[]> RefIndexes) TopologicalSort(TonCell root)
    {
        var all = new Dictionary<string, (TonCell Cell, string[] Refs)>(StringComparer.Ordinal);
        var notPerm = new List<string>();
        var notPermSet = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<TonCell> { root };
        while (pending.Count > 0)
        {
            var current = pending;
            pending = [];
            foreach (var cell in current)
            {
                string h = System.Convert.ToHexString(cell.Hash());
                if (all.ContainsKey(h)) continue;
                notPerm.Add(h);
                notPermSet.Add(h);
                all[h] = (cell, cell.Refs.Select(r => System.Convert.ToHexString(r.Hash())).ToArray());
                pending.AddRange(cell.Refs);
            }
        }

        var sorted = new List<string>();
        var temp = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string h)
        {
            if (!notPermSet.Contains(h)) return;
            if (!temp.Add(h)) throw new InvalidOperationException("Cell graph is not a DAG");
            var refs = all[h].Refs;
            for (int i = refs.Length - 1; i >= 0; i--) Visit(refs[i]);
            sorted.Add(h);
            temp.Remove(h);
            notPermSet.Remove(h);
        }
        foreach (string h in notPerm) Visit(h);

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < sorted.Count; i++) index[sorted[sorted.Count - 1 - i]] = i;
        var cells = new List<TonCell>(sorted.Count);
        var refIndexes = new List<int[]>(sorted.Count);
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            var e = all[sorted[i]];
            cells.Add(e.Cell);
            refIndexes.Add(e.Refs.Select(r => index[r]).ToArray());
        }
        return (cells, refIndexes);
    }

    private static int BitsFor(int n) => n == 0 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)n);

    private static void WriteUInt(List<byte> o, ulong v, int bytes)
    {
        for (int i = bytes - 1; i >= 0; i--) o.Add((byte)(v >> (8 * i)));
    }

    private static ulong ReadUInt(ReadOnlySpan<byte> b, ref int pos, int bytes)
    {
        if (pos + bytes > b.Length) throw new FormatException("BoC is truncated");
        ulong v = 0;
        for (int i = 0; i < bytes; i++) v = (v << 8) | b[pos++];
        return v;
    }

    private static int ReadSize(ReadOnlySpan<byte> b, ref int pos, int bytes)
    {
        ulong v = ReadUInt(b, ref pos, bytes);
        return v <= int.MaxValue ? (int)v : throw new FormatException("BoC value out of range");
    }

    private static readonly uint[] Crc32CTable = BuildCrc32CTable();

    private static uint[] BuildCrc32CTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0x82F63B78 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC-32C (Castagnoli), as used for BoC checksums.</summary>
    public static uint Crc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = Crc32CTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        return ~crc;
    }
}
