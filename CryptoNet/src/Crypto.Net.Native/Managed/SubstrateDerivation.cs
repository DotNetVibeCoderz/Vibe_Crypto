using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Crypto.Net.Native.Managed;

/// <summary>Managed port of the Substrate derivation-path rules (see <c>sr25519.rs</c>).</summary>
internal static class SubstrateDerivation
{
    public readonly record struct Junction(bool Hard, byte[] ChainCode);

    private static void CompactLength(int len, List<byte> output)
    {
        if (len < 1 << 6)
        {
            output.Add((byte)(len << 2));
        }
        else if (len < 1 << 14)
        {
            ushort v = (ushort)((len << 2) | 1);
            output.Add((byte)v);
            output.Add((byte)(v >> 8));
        }
        else
        {
            uint v = (uint)((len << 2) | 2);
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            output.AddRange(b.ToArray());
        }
    }

    private static byte[] ChainCode(string segment)
    {
        byte[] cc = new byte[32];
        if (ulong.TryParse(segment, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out ulong n))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(cc, n);
            return cc;
        }
        byte[] text = Encoding.UTF8.GetBytes(segment);
        var encoded = new List<byte>(text.Length + 4);
        CompactLength(text.Length, encoded);
        encoded.AddRange(text);
        if (encoded.Count > 32)
            return Blake2b256(encoded.ToArray());
        encoded.CopyTo(cc);
        return cc;
    }

    private static byte[] Blake2b256(byte[] data)
    {
        byte[] o = new byte[32];
        Blake2b.Hash(data, o);
        return o;
    }

    public static List<Junction> ParsePath(string path)
    {
        var result = new List<Junction>();
        int i = 0;
        while (i < path.Length)
        {
            bool hard;
            if (path.AsSpan(i).StartsWith("//")) { hard = true; i += 2; }
            else if (path[i] == '/') { hard = false; i += 1; }
            else throw new FormatException("Derivation path junctions must start with '/' or '//'");

            int end = path.IndexOf('/', i);
            if (end < 0) end = path.Length;
            if (end == i) throw new FormatException("Empty derivation junction");
            result.Add(new Junction(hard, ChainCode(path[i..end])));
            i = end;
        }
        return result;
    }

    public static byte[] Ed25519Derive(ReadOnlySpan<byte> seed, string path)
    {
        byte[] current = seed.ToArray();
        byte[] prefix = [0x2c, .. "Ed25519HDKD"u8]; // SCALE-encoded string
        foreach (var j in ParsePath(path))
        {
            if (!j.Hard)
            {
                CryptographicOperations.ZeroMemory(current);
                throw new FormatException("ed25519 supports only hard (//) junctions");
            }
            byte[] buf = [.. prefix, .. current, .. j.ChainCode];
            CryptographicOperations.ZeroMemory(current);
            current = Blake2b256(buf);
            CryptographicOperations.ZeroMemory(buf);
        }
        return current;
    }
}
