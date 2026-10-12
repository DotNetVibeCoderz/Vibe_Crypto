using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Aptos;

/// <summary>Aptos network descriptor. APT has 8 decimals (octas).</summary>
public sealed record AptosChain(ChainId Id, string Name, byte ChainId, string DefaultRestUrl, bool IsTestnet = false, string? ExplorerUrl = null) : IChain
{
    public string Symbol => "APT";
    public int Decimals => 8;

    public static readonly AptosChain Mainnet = new(new ChainId("aptos"), "Aptos", 1, "https://api.mainnet.aptoslabs.com/v1", ExplorerUrl: "https://explorer.aptoslabs.com/?network=mainnet");
    public static readonly AptosChain Testnet = new(new ChainId("aptos-testnet"), "Aptos Testnet", 2, "https://api.testnet.aptoslabs.com/v1", true, "https://explorer.aptoslabs.com/?network=testnet");
    public static readonly AptosChain Localnet = new(new ChainId("aptos-local"), "Aptos Localnet", 4, "http://127.0.0.1:8080/v1", true);

    public static IReadOnlyList<AptosChain> All { get; } = [Mainnet, Testnet, Localnet];
}

/// <summary>Aptos 32-byte account addresses (AIP-40 formatting) and units.</summary>
public static class AptosAddress
{
    public const int Length = 32;

    /// <summary>
    /// Parses <c>0x</c> followed by 1–64 hex digits (short forms are left-padded with zeros). Without
    /// <paramref name="relaxed"/>, the prefix is required.
    /// </summary>
    public static byte[] Parse(string address, bool relaxed = true)
    {
        ArgumentNullException.ThrowIfNull(address);
        string hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..]
            : relaxed ? address : throw new FormatException("Aptos addresses start with 0x");
        if (hex.Length is 0 or > 64 || !hex.All(Uri.IsHexDigit)) throw new FormatException($"Invalid Aptos address '{address}'");
        return System.Convert.FromHexString(hex.PadLeft(64, '0'));
    }

    /// <summary>Strict AIP-40 check: <c>0x</c> plus 64 hex digits, or a special address <c>0x0</c>–<c>0xf</c>.</summary>
    public static bool IsValid(string? address) =>
        address is { Length: 3 or 66 } && address.StartsWith("0x", StringComparison.Ordinal) && address[2..].All(Uri.IsHexDigit);

    /// <summary>AIP-40 form: special addresses (<c>0x0</c>–<c>0xf</c>) short, every other address as 64 hex digits.</summary>
    public static string Format(ReadOnlySpan<byte> address)
    {
        if (address.Length != Length) throw new ArgumentException("Aptos addresses are 32 bytes", nameof(address));
        bool special = address[..31].IndexOfAnyExcept((byte)0) < 0 && address[31] < 16;
        return special ? "0x" + address[31].ToString("x", System.Globalization.CultureInfo.InvariantCulture) : "0x" + System.Convert.ToHexStringLower(address);
    }

    /// <summary>Normalizes any accepted form to AIP-40.</summary>
    public static string Normalize(string address) => Format(Parse(address));

    /// <summary>Address (authentication key) of a single ed25519 key: <c>sha3-256(publicKey ‖ 0x00)</c>.</summary>
    public static byte[] FromEd25519PublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 32) throw new ArgumentException("ed25519 public keys are 32 bytes", nameof(publicKey));
        return CryptoNative.Sha3_256([.. publicKey, 0x00]);
    }

    /// <summary>1 APT = 10^8 octas.</summary>
    public static Amount Apt(decimal apt) => Amount.FromDecimal(apt, 8);
    public static Amount Octas(ulong octas) => new(octas, 8);
}

/// <summary>
/// An Aptos ed25519 account. Its address is derived from the key unless an explicit address is given (for accounts
/// whose authentication key was rotated).
/// </summary>
public sealed class AptosAccount : IAccount, IDisposable
{
    private const string Aip80Prefix = "ed25519-priv-";
    private readonly SecureBuffer _seed;

    public AptosAccount(ReadOnlySpan<byte> seed32, AptosChain? network = null, string? address = null, string? derivationPath = null)
    {
        if (seed32.Length != 32) throw new ArgumentException("Aptos ed25519 keys are 32-byte seeds", nameof(seed32));
        _seed = SecureBuffer.FromBytes(seed32);
        PublicKey = CryptoNative.Ed25519GetPublicKey(seed32);
        AddressBytes = address is null ? AptosAddress.FromEd25519PublicKey(PublicKey.Span) : AptosAddress.Parse(address);
        Network = network ?? AptosChain.Mainnet;
        Address = new Address(AptosAddress.Format(AddressBytes), Network.Id);
        DerivationPath = derivationPath;
    }

    /// <summary>Imports an AIP-80 key (<c>ed25519-priv-0x…</c>) or a plain 32-byte hex key.</summary>
    public static AptosAccount FromPrivateKey(string privateKey, AptosChain? network = null, string? address = null)
    {
        string hex = privateKey.StartsWith(Aip80Prefix, StringComparison.Ordinal) ? privateKey[Aip80Prefix.Length..] : privateKey;
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        byte[] raw = System.Convert.FromHexString(hex);
        try
        {
            if (raw.Length != 32) throw new FormatException("An Aptos ed25519 private key is 32 bytes");
            return new AptosAccount(raw, network, address);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }

    public AptosChain Network { get; }
    public IChain Chain => Network;
    public Address Address { get; }
    public byte[] AddressBytes { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public string? DerivationPath { get; }

    /// <summary>AIP-80 private key string (<c>ed25519-priv-0x…</c>).</summary>
    public string ExportPrivateKey() => Aip80Prefix + "0x" + System.Convert.ToHexStringLower(_seed.Span);

    public byte[] Sign(ReadOnlySpan<byte> message) => CryptoNative.Ed25519Sign(_seed.Span, message);

    /// <summary>Signs a raw transaction and returns the BCS <c>SignedTransaction</c>.</summary>
    public byte[] SignTransaction(AptosRawTransaction transaction) => transaction.Sign(this);

    public KeySigner CreateSigner() => new(Network, Address, _seed.Clone(), SignatureScheme.Ed25519);

    public void Dispose() => _seed.Dispose();
}

public static class AptosWalletExtensions
{
    /// <summary>Aptos wallet path <c>m/44'/637'/index'/0'/0'</c> (SLIP-10 ed25519), as used by Petra and the Aptos SDKs.</summary>
    public static AptosAccount GetAptosAccount(this HdWallet wallet, uint index = 0, AptosChain? network = null)
    {
        string path = $"m/44'/637'/{index}'/0'/0'";
        using var key = wallet.DeriveEd25519Path(path);
        return new AptosAccount(key.PrivateKey.Span, network, derivationPath: path);
    }
}
