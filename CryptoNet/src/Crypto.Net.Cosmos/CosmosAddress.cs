using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Cosmos;

/// <summary>Cosmos Bech32 account addresses: bech32(hrp, RIPEMD160(SHA256(compressed secp256k1 key))).</summary>
public static class CosmosAddress
{
    public static Address FromPrivateKey(ReadOnlySpan<byte> privateKey32, string hrp = "cosmos", ChainId? chain = null) =>
        FromPublicKey(CryptoNative.Secp256k1GetPublicKey(privateKey32, compressed: true), hrp, chain);

    public static Address FromPublicKey(ReadOnlySpan<byte> compressedPublicKey, string hrp = "cosmos", ChainId? chain = null)
    {
        if (compressedPublicKey.Length != 33) throw new ArgumentException("Cosmos uses 33-byte compressed secp256k1 keys", nameof(compressedPublicKey));
        return new Address(CryptoNative.Bech32Encode(hrp, CryptoNative.Hash160(compressedPublicKey)), chain ?? ChainId.Cosmos);
    }

    /// <summary>Decodes to (hrp, 20/32-byte address bytes).</summary>
    public static (string Hrp, byte[] Bytes) Decode(string address)
    {
        var (hrp, data, isM) = CryptoNative.Bech32Decode(address);
        if (isM) throw new FormatException("Cosmos addresses use Bech32, not Bech32m");
        if (data.Length is not (20 or 32)) throw new FormatException("Cosmos addresses are 20 or 32 bytes");
        return (hrp, data);
    }

    public static bool IsValid(string? address, string? hrp = "cosmos")
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        try
        {
            var (decodedHrp, _) = Decode(address);
            return hrp is null || decodedHrp == hrp;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Same account on another Cosmos chain (e.g. cosmos1… → osmo1…).</summary>
    public static string ConvertPrefix(string address, string newHrp) => CryptoNative.Bech32Encode(newHrp, Decode(address).Bytes);
}
