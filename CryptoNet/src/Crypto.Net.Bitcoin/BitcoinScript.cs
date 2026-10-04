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
