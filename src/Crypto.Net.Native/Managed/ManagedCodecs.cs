using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Crypto.Net.Native.Managed;

internal static class Base58Managed
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private static readonly sbyte[] Map = BuildMap();

    private static sbyte[] BuildMap()
    {
        var map = new sbyte[128];
        Array.Fill(map, (sbyte)-1);
        for (int i = 0; i < Alphabet.Length; i++) map[Alphabet[i]] = (sbyte)i;
        return map;
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        int zeros = 0;
        while (zeros < data.Length && data[zeros] == 0) zeros++;

        BigInteger value = new(data, isUnsigned: true, isBigEndian: true);
        var sb = new StringBuilder(data.Length * 138 / 100 + 1);
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out BigInteger rem);
            sb.Append(Alphabet[(int)rem]);
        }
        sb.Append('1', zeros);

        var chars = sb.ToString().ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    public static byte[] Decode(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length == 0) return [];
        BigInteger value = BigInteger.Zero;
        foreach (char c in s)
        {
            int digit = c < 128 ? Map[c] : -1;
            if (digit < 0) throw new FormatException($"Invalid Base58 character '{c}'");
            value = value * 58 + digit;
        }

        int zeros = 0;
        while (zeros < s.Length && s[zeros] == '1') zeros++;

        byte[] body = value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] result = new byte[zeros + body.Length];
        body.CopyTo(result, zeros);
        return result;
    }

    public static string EncodeCheck(ReadOnlySpan<byte> data)
    {
        byte[] payload = new byte[data.Length + 4];
        data.CopyTo(payload);
        byte[] checksum = SHA256.HashData(SHA256.HashData(data));
        checksum.AsSpan(0, 4).CopyTo(payload.AsSpan(data.Length));
        return Encode(payload);
    }

    public static byte[] DecodeCheck(string s)
    {
        byte[] raw = Decode(s);
        if (raw.Length < 4) throw new FormatException("Base58Check payload too short");
        ReadOnlySpan<byte> body = raw.AsSpan(0, raw.Length - 4);
        byte[] checksum = SHA256.HashData(SHA256.HashData(body));
        if (!CryptographicOperations.FixedTimeEquals(checksum.AsSpan(0, 4), raw.AsSpan(raw.Length - 4)))
            throw new FormatException("Base58Check checksum mismatch");
        return body.ToArray();
    }
}

internal static class Bech32Managed
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32Const = 1;
    private const uint Bech32mConst = 0x2bc830a3;

    private static uint Polymod(ReadOnlySpan<byte> values)
    {
        ReadOnlySpan<uint> gen = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        uint chk = 1;
        foreach (byte v in values)
        {
            uint b = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (int i = 0; i < 5; i++)
                if (((b >> i) & 1) != 0) chk ^= gen[i];
        }
        return chk;
    }

    private static byte[] HrpExpand(string hrp)
    {
        var ret = new byte[hrp.Length * 2 + 1];
        for (int i = 0; i < hrp.Length; i++)
        {
            ret[i] = (byte)(hrp[i] >> 5);
            ret[i + hrp.Length + 1] = (byte)(hrp[i] & 31);
        }
        return ret;
    }

    internal static byte[] ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        int acc = 0, bits = 0;
        int maxv = (1 << toBits) - 1;
        int maxAcc = (1 << (fromBits + toBits - 1)) - 1;
        var ret = new List<byte>(data.Length * fromBits / toBits + 1);
        foreach (byte value in data)
        {
            if ((value >> fromBits) != 0) throw new FormatException("Invalid data for bit conversion");
            acc = ((acc << fromBits) | value) & maxAcc;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                ret.Add((byte)((acc >> bits) & maxv));
            }
        }
        if (pad)
        {
            if (bits > 0) ret.Add((byte)((acc << (toBits - bits)) & maxv));
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
        {
            throw new FormatException("Invalid padding in bit conversion");
        }
        return [.. ret];
    }

    private static string EncodeRaw(string hrp, ReadOnlySpan<byte> values5, bool bech32m)
    {
        if (hrp.Length is < 1 or > 83) throw new ArgumentException("Invalid HRP length");
        foreach (char c in hrp)
            if (c is < (char)33 or > (char)126) throw new ArgumentException("Invalid HRP character");
        hrp = hrp.ToLowerInvariant();

        byte[] expanded = HrpExpand(hrp);
        byte[] enc = new byte[expanded.Length + values5.Length + 6];
        expanded.CopyTo(enc, 0);
        values5.CopyTo(enc.AsSpan(expanded.Length));
        uint mod = Polymod(enc) ^ (bech32m ? Bech32mConst : Bech32Const);

        var sb = new StringBuilder(hrp.Length + 1 + values5.Length + 6);
        sb.Append(hrp).Append('1');
        foreach (byte b in values5) sb.Append(Charset[b]);
        for (int i = 0; i < 6; i++) sb.Append(Charset[(int)((mod >> (5 * (5 - i))) & 31)]);
        return sb.ToString();
    }

    private static (string Hrp, byte[] Values5, bool IsBech32m) DecodeRaw(string s, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length < 8 || s.Length > maxLength) throw new FormatException("Invalid Bech32 length");
        bool hasLower = false, hasUpper = false;
        foreach (char c in s)
        {
            if (c is < (char)33 or > (char)126) throw new FormatException("Invalid Bech32 character");
            hasLower |= char.IsLower(c);
            hasUpper |= char.IsUpper(c);
        }
        if (hasLower && hasUpper) throw new FormatException("Mixed-case Bech32 string");

        s = s.ToLowerInvariant();
        int pos = s.LastIndexOf('1');
        if (pos < 1 || pos + 7 > s.Length) throw new FormatException("Invalid Bech32 separator position");

        string hrp = s[..pos];
        byte[] values = new byte[s.Length - pos - 1];
        for (int i = 0; i < values.Length; i++)
        {
            int idx = Charset.IndexOf(s[pos + 1 + i]);
            if (idx < 0) throw new FormatException($"Invalid Bech32 character '{s[pos + 1 + i]}'");
            values[i] = (byte)idx;
        }

        byte[] expanded = HrpExpand(hrp);
        byte[] check = new byte[expanded.Length + values.Length];
        expanded.CopyTo(check, 0);
        values.CopyTo(check, expanded.Length);
        uint pm = Polymod(check);
        bool isM = pm == Bech32mConst;
        if (pm != Bech32Const && !isM) throw new FormatException("Bech32 checksum mismatch");
        return (hrp, values[..^6], isM);
    }

    public static string Encode(string hrp, ReadOnlySpan<byte> data, bool bech32m) =>
        EncodeRaw(hrp, ConvertBits(data, 8, 5, true), bech32m);

    public static (string Hrp, byte[] Data, bool IsBech32m) Decode(string s)
    {
        // Cosmos addresses (e.g. 32-byte module accounts, long HRPs) can exceed BIP-173's 90 chars.
        var (hrp, values, isM) = DecodeRaw(s, 1023);
        return (hrp, ConvertBits(values, 5, 8, false), isM);
    }

    public static string SegwitEncode(string hrp, byte version, ReadOnlySpan<byte> program)
    {
        ValidateProgram(version, program.Length);
        byte[] conv = ConvertBits(program, 8, 5, true);
        byte[] values = new byte[conv.Length + 1];
        values[0] = version;
        conv.CopyTo(values, 1);
        return EncodeRaw(hrp, values, bech32m: version != 0);
    }

    public static (string Hrp, byte Version, byte[] Program) SegwitDecode(string s)
    {
        var (hrp, values, isM) = DecodeRaw(s, 90);
        if (values.Length < 1) throw new FormatException("Empty witness data");
        byte version = values[0];
        byte[] program = ConvertBits(values.AsSpan(1), 5, 8, false);
        if ((version == 0) == isM) throw new FormatException("Wrong Bech32 variant for witness version");
        ValidateProgram(version, program.Length);
        return (hrp, version, program);
    }

    private static void ValidateProgram(byte version, int length)
    {
        if (version > 16) throw new FormatException("Witness version must be 0..16");
        if (length is < 2 or > 40) throw new FormatException("Witness program must be 2..40 bytes");
        if (version == 0 && length != 20 && length != 32) throw new FormatException("Witness v0 program must be 20 or 32 bytes");
    }
}
