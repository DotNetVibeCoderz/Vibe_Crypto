using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Bitcoin;

/// <summary>Result of parsing a Bitcoin address.</summary>
public sealed record BitcoinAddressInfo(string Address, BitcoinAddressType Type, BitcoinNetwork Network, byte[] Payload, byte[] ScriptPubKey);

/// <summary>
/// Bitcoin address encoding/decoding: Legacy P2PKH, P2SH, native SegWit (P2WPKH/P2WSH, BIP-173)
/// and Taproot (P2TR, BIP-341/350/86).
/// </summary>
public static class BitcoinAddress
{
    public static Address FromPrivateKey(ReadOnlySpan<byte> privateKey, BitcoinAddressType type = BitcoinAddressType.SegWitP2WPKH, BitcoinNetwork? network = null) =>
        FromPublicKey(CryptoNative.Secp256k1GetPublicKey(privateKey, compressed: true), type, network);

    /// <summary>Builds an address from a compressed (33-byte) public key.</summary>
    public static Address FromPublicKey(ReadOnlySpan<byte> compressedPublicKey, BitcoinAddressType type = BitcoinAddressType.SegWitP2WPKH, BitcoinNetwork? network = null)
    {
        network ??= BitcoinNetwork.Mainnet;
        if (compressedPublicKey.Length != 33) throw new ArgumentException("A 33-byte compressed public key is required", nameof(compressedPublicKey));

        string address = type switch
        {
            BitcoinAddressType.LegacyP2PKH => Base58Address(network.P2PkhPrefix, CryptoNative.Hash160(compressedPublicKey)),
            BitcoinAddressType.SegWitP2WPKH => CryptoNative.SegwitEncode(network.Bech32Hrp, 0, CryptoNative.Hash160(compressedPublicKey)),
            BitcoinAddressType.TaprootP2TR => FromTaprootOutputKey(TaprootOutputKey(compressedPublicKey[1..]), network),
            BitcoinAddressType.P2SH => throw new ArgumentException("Use FromScript for P2SH"),
            BitcoinAddressType.SegWitP2WSH => throw new ArgumentException("Use FromScript for P2WSH"),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        return new Address(address, network.Id);
    }

    /// <summary>P2SH or P2WSH address paying to <paramref name="redeemScript"/>.</summary>
    public static Address FromScript(ReadOnlySpan<byte> redeemScript, BitcoinAddressType type, BitcoinNetwork? network = null)
    {
        network ??= BitcoinNetwork.Mainnet;
        string address = type switch
        {
            BitcoinAddressType.P2SH => Base58Address(network.P2ShPrefix, CryptoNative.Hash160(redeemScript)),
            BitcoinAddressType.SegWitP2WSH => CryptoNative.SegwitEncode(network.Bech32Hrp, 0, CryptoNative.Sha256(redeemScript)),
            _ => throw new ArgumentException("FromScript supports P2SH and P2WSH"),
        };
        return new Address(address, network.Id);
    }

    /// <summary>BIP-86 output key for a key-path-only Taproot output (no script tree).</summary>
    public static byte[] TaprootOutputKey(ReadOnlySpan<byte> internalXOnlyKey) => CryptoNative.TaprootTweakPublicKey(internalXOnlyKey).OutputKey;

    public static string FromTaprootOutputKey(ReadOnlySpan<byte> outputKey, BitcoinNetwork? network = null) =>
        CryptoNative.SegwitEncode((network ?? BitcoinNetwork.Mainnet).Bech32Hrp, 1, outputKey);

    private static string Base58Address(byte prefix, ReadOnlySpan<byte> hash) => CryptoNative.Base58CheckEncode([prefix, .. hash]);

    /// <summary>Parses any supported address for <paramref name="network"/>; throws <see cref="FormatException"/> otherwise.</summary>
    public static BitcoinAddressInfo Parse(string address, BitcoinNetwork? network = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        network ??= BitcoinNetwork.Mainnet;
        address = address.Trim();

        if (address.StartsWith(network.Bech32Hrp + "1", StringComparison.OrdinalIgnoreCase))
        {
            var (hrp, version, program) = CryptoNative.SegwitDecode(address);
            if (!string.Equals(hrp, network.Bech32Hrp, StringComparison.Ordinal)) throw new FormatException("Address is for a different network");
            var type = (version, program.Length) switch
            {
                (0, 20) => BitcoinAddressType.SegWitP2WPKH,
                (0, 32) => BitcoinAddressType.SegWitP2WSH,
                (1, 32) => BitcoinAddressType.TaprootP2TR,
                _ => throw new FormatException($"Unsupported witness version {version}"),
            };
            return new BitcoinAddressInfo(address, type, network, program, BitcoinScript.Witness(version, program));
        }

        byte[] decoded;
        try
        {
            decoded = CryptoNative.Base58CheckDecode(address);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            throw new FormatException($"Invalid Bitcoin address '{address}'", ex);
        }
        if (decoded.Length != 21) throw new FormatException("Base58 address must contain 21 bytes");
        byte[] hash = decoded[1..];
        if (decoded[0] == network.P2PkhPrefix)
            return new BitcoinAddressInfo(address, BitcoinAddressType.LegacyP2PKH, network, hash, BitcoinScript.P2PKH(hash));
        if (decoded[0] == network.P2ShPrefix)
            return new BitcoinAddressInfo(address, BitcoinAddressType.P2SH, network, hash, BitcoinScript.P2SH(hash));
        throw new FormatException("Address version byte does not match the network");
    }

    public static bool TryParse(string? address, BitcoinNetwork? network, out BitcoinAddressInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(address)) return false;
        try
        {
            info = Parse(address, network);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsValid(string? address, BitcoinNetwork? network = null) => TryParse(address, network, out _);

    /// <summary>The output script (scriptPubKey) that pays to <paramref name="address"/>.</summary>
    public static byte[] GetScriptPubKey(string address, BitcoinNetwork? network = null) => Parse(address, network).ScriptPubKey;

    /// <summary>Wallet Import Format for a compressed key.</summary>
    public static string WifFromPrivateKey(ReadOnlySpan<byte> privateKey, BitcoinNetwork? network = null)
    {
        if (privateKey.Length != 32) throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey));
        byte[] payload = [(network ?? BitcoinNetwork.Mainnet).WifPrefix, .. privateKey, 0x01];
        try
        {
            return CryptoNative.Base58CheckEncode(payload);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Decodes WIF into a private key; reports whether it was flagged as compressed.</summary>
    public static SecureBuffer PrivateKeyFromWif(string wif, out bool compressed, BitcoinNetwork? network = null)
    {
        byte[] raw = CryptoNative.Base58CheckDecode(wif);
        try
        {
            if (raw[0] != (network ?? BitcoinNetwork.Mainnet).WifPrefix) throw new FormatException("WIF prefix does not match the network");
            compressed = raw.Length == 34 && raw[33] == 0x01;
            if (raw.Length != 33 && !compressed) throw new FormatException("Invalid WIF length");
            return SecureBuffer.FromBytes(raw.AsSpan(1, 32));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }
}
