using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Polkadot;

/// <summary>
/// SS58 address codec: Base58(prefix || public key || checksum) where the checksum is the first two bytes of
/// BLAKE2b-512("SS58PRE" || prefix || public key).
/// </summary>
public static class Ss58Address
{
    private static readonly byte[] Ss58Pre = "SS58PRE"u8.ToArray();

    public static Address FromPublicKey(ReadOnlySpan<byte> publicKey32, ushort prefix = 0, ChainId? chain = null) =>
        new(Encode(publicKey32, prefix), chain ?? ChainId.Polkadot);

    public static string Encode(ReadOnlySpan<byte> publicKey32, ushort prefix)
    {
        if (publicKey32.Length != 32) throw new ArgumentException("Substrate account ids are 32 bytes", nameof(publicKey32));
        if (prefix > 16383 || prefix is 46 or 47) throw new ArgumentOutOfRangeException(nameof(prefix), "Invalid SS58 prefix");

        byte[] payload = [.. EncodePrefix(prefix), .. publicKey32];
        byte[] checksum = CryptoNative.Blake2b512([.. Ss58Pre, .. payload]);
        return CryptoNative.Base58Encode([.. payload, checksum[0], checksum[1]]);
    }

    /// <summary>Decodes an SS58 address and verifies its checksum.</summary>
    public static (ushort Prefix, byte[] PublicKey) Decode(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        byte[] raw = CryptoNative.Base58Decode(address.Trim());
        if (raw.Length < 3) throw new FormatException("SS58 address too short");

        int prefixLen;
        ushort prefix;
        if (raw[0] < 64)
        {
            prefixLen = 1;
            prefix = raw[0];
        }
        else if (raw[0] < 128)
        {
            prefixLen = 2;
            int lower = ((raw[0] << 2) | (raw[1] >> 6)) & 0xFF;
            int upper = raw[1] & 0x3F;
            prefix = (ushort)(lower | (upper << 8));
        }
        else
        {
            throw new FormatException("Invalid SS58 prefix byte");
        }

        int keyLen = raw.Length - prefixLen - 2;
        if (keyLen != 32) throw new FormatException("Only 32-byte account ids are supported");
        byte[] checksum = CryptoNative.Blake2b512([.. Ss58Pre, .. raw.AsSpan(0, raw.Length - 2)]);
        if (raw[^2] != checksum[0] || raw[^1] != checksum[1]) throw new FormatException("Invalid SS58 checksum");
        return (prefix, raw[prefixLen..^2]);
    }

    public static bool IsValid(string? address, ushort? expectedPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        try
        {
            var (prefix, _) = Decode(address);
            return expectedPrefix is null || prefix == expectedPrefix;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Re-encodes an address for another network prefix.</summary>
    public static string Convert(string address, ushort newPrefix) => Encode(Decode(address).PublicKey, newPrefix);

    private static byte[] EncodePrefix(ushort prefix) => prefix < 64
        ? [(byte)prefix]
        : [(byte)(((prefix & 0b0000_0000_1111_1100) >> 2) | 0b0100_0000), (byte)((prefix >> 8) | ((prefix & 0b11) << 6))];
}
