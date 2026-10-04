using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Evm;

/// <summary>EVM address derivation and EIP-55 checksum handling.</summary>
public static class EvmAddress
{
    public const string Zero = "0x0000000000000000000000000000000000000000";

    public static Address FromPrivateKey(ReadOnlySpan<byte> privateKey, ChainId? chain = null) =>
        FromPublicKey(CryptoNative.Secp256k1GetPublicKey(privateKey, compressed: false), chain);

    /// <summary>Address = last 20 bytes of keccak256(uncompressed public key without the 0x04 prefix).</summary>
    public static Address FromPublicKey(ReadOnlySpan<byte> publicKey, ChainId? chain = null)
    {
        byte[] uncompressed = publicKey.Length == 65 ? publicKey.ToArray() : CryptoNative.Secp256k1ConvertPublicKey(publicKey, compressed: false);
        byte[] hash = CryptoNative.Keccak256(uncompressed.AsSpan(1));
        return new Address(ToChecksumAddress(hash.AsSpan(12, 20)), chain ?? ChainId.Ethereum);
    }

    public static string ToChecksumAddress(ReadOnlySpan<byte> addressBytes)
    {
        if (addressBytes.Length != 20) throw new ArgumentException("Address must be 20 bytes", nameof(addressBytes));
        return ToChecksumAddress(Convert.ToHexStringLower(addressBytes));
    }

    /// <summary>Applies EIP-55 mixed-case checksum encoding.</summary>
    public static string ToChecksumAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        string hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        if (hex.Length != 40 || !hex.All(Uri.IsHexDigit))
            throw new FormatException($"Invalid EVM address '{address}'");

        string lower = hex.ToLowerInvariant();
        byte[] hash = CryptoNative.Keccak256(Encoding.ASCII.GetBytes(lower));
        var sb = new StringBuilder("0x", 42);
        for (int i = 0; i < 40; i++)
        {
            char c = lower[i];
            int nibble = (i % 2 == 0) ? hash[i / 2] >> 4 : hash[i / 2] & 0x0F;
            sb.Append(c is >= 'a' and <= 'f' && nibble >= 8 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Validates format. All-lowercase and all-uppercase addresses are accepted; mixed-case addresses must
    /// carry a correct EIP-55 checksum.
    /// </summary>
    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        string hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        if (hex.Length != 40 || !hex.All(Uri.IsHexDigit)) return false;

        bool hasLower = hex.Any(char.IsLower);
        bool hasUpper = hex.Any(char.IsUpper);
        if (!(hasLower && hasUpper)) return true;
        return string.Equals(ToChecksumAddress(hex), "0x" + hex, StringComparison.Ordinal);
    }

    /// <summary>Parses and normalizes to checksum form; throws for invalid input.</summary>
    public static Address Parse(string address, ChainId? chain = null)
    {
        if (!IsValid(address)) throw new FormatException($"Invalid EVM address (or EIP-55 checksum) '{address}'");
        return new Address(ToChecksumAddress(address), chain ?? ChainId.Ethereum);
    }

    /// <summary>Address of a contract created by <paramref name="sender"/> with <paramref name="nonce"/> (CREATE).</summary>
    public static string GetContractAddress(string sender, ulong nonce)
    {
        byte[] rlp = EvmRlp.EncodeList(EvmRlp.EncodeBytes(HexUtil.Decode(sender)), EvmRlp.EncodeUInt(nonce));
        return ToChecksumAddress(CryptoNative.Keccak256(rlp).AsSpan(12, 20));
    }
}
