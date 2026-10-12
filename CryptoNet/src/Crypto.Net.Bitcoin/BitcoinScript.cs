namespace Crypto.Net.Bitcoin;

/// <summary>Standard output scripts and push-data helpers.</summary>
public static class BitcoinScript
{
    public const byte OP_0 = 0x00;
    public const byte OP_1 = 0x51;
    public const byte OP_RETURN = 0x6a;
    public const byte OP_DUP = 0x76;
    public const byte OP_EQUAL = 0x87;
    public const byte OP_EQUALVERIFY = 0x88;
    public const byte OP_HASH160 = 0xa9;
    public const byte OP_CHECKSIG = 0xac;
    public const byte OP_CHECKMULTISIG = 0xae;

    public static byte[] P2PKH(ReadOnlySpan<byte> pubKeyHash20) =>
        [OP_DUP, OP_HASH160, 0x14, .. Require(pubKeyHash20, 20), OP_EQUALVERIFY, OP_CHECKSIG];

    public static byte[] P2SH(ReadOnlySpan<byte> scriptHash20) =>
        [OP_HASH160, 0x14, .. Require(scriptHash20, 20), OP_EQUAL];

    public static byte[] P2WPKH(ReadOnlySpan<byte> pubKeyHash20) => [OP_0, 0x14, .. Require(pubKeyHash20, 20)];

    public static byte[] P2WSH(ReadOnlySpan<byte> scriptHash32) => [OP_0, 0x20, .. Require(scriptHash32, 32)];

    public static byte[] P2TR(ReadOnlySpan<byte> outputKey32) => [OP_1, 0x20, .. Require(outputKey32, 32)];

    /// <summary>Witness program script for any version (0..16).</summary>
    public static byte[] Witness(byte version, ReadOnlySpan<byte> program) =>
        [version == 0 ? OP_0 : (byte)(OP_1 + version - 1), (byte)program.Length, .. program];

    /// <summary>OP_RETURN data carrier output (max 80 bytes is standard).</summary>
    public static byte[] OpReturn(ReadOnlySpan<byte> data)
    {
        if (data.Length > 80) throw new ArgumentException("OP_RETURN data above 80 bytes is non-standard", nameof(data));
        return [OP_RETURN, .. PushData(data)];
    }

    /// <summary>
    /// Bare m-of-n <c>OP_CHECKMULTISIG</c> script. With <paramref name="sorted"/> the keys are ordered
    /// lexicographically (BIP-67, the <c>sortedmulti</c> descriptor), so every cosigner derives the same script.
    /// </summary>
    public static byte[] Multisig(int required, IEnumerable<byte[]> publicKeys, bool sorted = true)
    {
        var keys = publicKeys.ToList();
        if (keys.Count is < 1 or > 16) throw new ArgumentException("Multisig needs 1 to 16 public keys", nameof(publicKeys));
        if (required < 1 || required > keys.Count) throw new ArgumentOutOfRangeException(nameof(required));
        if (keys.Any(k => k.Length != 33)) throw new ArgumentException("Multisig keys must be 33-byte compressed public keys", nameof(publicKeys));
        if (sorted) keys.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        var script = new List<byte> { (byte)(OP_1 + required - 1) };
        foreach (var key in keys) script.AddRange(PushData(key));
        script.Add((byte)(OP_1 + keys.Count - 1));
        script.Add(OP_CHECKMULTISIG);
        return [.. script];
    }

    /// <summary>Parses an m-of-n <c>OP_CHECKMULTISIG</c> script; returns false for anything else.</summary>
    public static bool TryParseMultisig(ReadOnlySpan<byte> script, out int required, out IReadOnlyList<byte[]> publicKeys)
    {
        required = 0;
        publicKeys = [];
        if (script.Length < 3 || script[^1] != OP_CHECKMULTISIG) return false;
        int m = script[0] - OP_1 + 1, n = script[^2] - OP_1 + 1;
        if (m is < 1 or > 16 || n is < 1 or > 16 || m > n) return false;
        var keys = new List<byte[]>();
        int pos = 1;
        while (pos < script.Length - 2)
        {
            int len = script[pos];
            if (len is not (33 or 65) || pos + 1 + len > script.Length - 2) return false;
            keys.Add(script.Slice(pos + 1, len).ToArray());
            pos += 1 + len;
        }
        if (pos != script.Length - 2 || keys.Count != n) return false;
        required = m;
        publicKeys = keys;
        return true;
    }

    /// <summary>Minimal push of <paramref name="data"/>.</summary>
    public static byte[] PushData(ReadOnlySpan<byte> data) => data.Length switch
    {
        < 0x4c => [(byte)data.Length, .. data],
        <= 0xff => [0x4c, (byte)data.Length, .. data],
        <= 0xffff => [0x4d, (byte)data.Length, (byte)(data.Length >> 8), .. data],
        _ => [0x4e, (byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16), (byte)(data.Length >> 24), .. data],
    };

    private static ReadOnlySpan<byte> Require(ReadOnlySpan<byte> data, int length)
    {
        if (data.Length != length) throw new ArgumentException($"Expected {length} bytes, got {data.Length}");
        return data;
    }
}
