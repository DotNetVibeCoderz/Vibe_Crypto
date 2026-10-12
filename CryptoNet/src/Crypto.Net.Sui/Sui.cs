using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Sui;

/// <summary>Sui network descriptor. SUI has 9 decimals (MIST). Clients use the GraphQL API.</summary>
public sealed record SuiChain(ChainId Id, string Name, string DefaultGraphQlUrl, bool IsTestnet = false, string? ExplorerUrl = null) : IChain
{
    public string Symbol => "SUI";
    public int Decimals => 9;

    public static readonly SuiChain Mainnet = new(new ChainId("sui"), "Sui", "https://graphql.mainnet.sui.io/graphql", ExplorerUrl: "https://suiscan.xyz/mainnet");
    public static readonly SuiChain Testnet = new(new ChainId("sui-testnet"), "Sui Testnet", "https://graphql.testnet.sui.io/graphql", true, "https://suiscan.xyz/testnet");
    public static readonly SuiChain Devnet = new(new ChainId("sui-devnet"), "Sui Devnet", "https://graphql.devnet.sui.io/graphql", true, "https://suiscan.xyz/devnet");
    public static readonly SuiChain Localnet = new(new ChainId("sui-local"), "Sui Localnet", "http://127.0.0.1:9125/graphql", true);

    public static IReadOnlyList<SuiChain> All { get; } = [Mainnet, Testnet, Devnet, Localnet];
}

/// <summary>Sui addresses (32 bytes, always shown as 64 hex digits), signature flags and units.</summary>
public static class SuiAddress
{
    /// <summary>Signature scheme flag for ed25519 (the first byte of a serialized signature and of the address preimage).</summary>
    public const byte Ed25519Flag = 0x00;

    /// <summary>Parses <c>0x</c> plus 1–64 hex digits (short forms such as <c>0x2</c> are padded).</summary>
    public static byte[] Parse(string address) => MoveAddress.Parse(address, relaxed: false);

    /// <summary>Normalized form: <c>0x</c> plus 64 lowercase hex digits.</summary>
    public static string Format(ReadOnlySpan<byte> address) => MoveAddress.FormatLong(address);

    public static string Normalize(string address) => Format(Parse(address));

    public static bool IsValid(string? address) =>
        address is { Length: > 2 and <= 66 } && address.StartsWith("0x", StringComparison.Ordinal) && address[2..].All(Uri.IsHexDigit);

    /// <summary><c>blake2b-256(0x00 ‖ publicKey)</c>.</summary>
    public static byte[] FromEd25519PublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 32) throw new ArgumentException("ed25519 public keys are 32 bytes", nameof(publicKey));
        return CryptoNative.Blake2b256([Ed25519Flag, .. publicKey]);
    }

    /// <summary>1 SUI = 10^9 MIST.</summary>
    public static Amount Sui(decimal sui) => Amount.FromDecimal(sui, 9);
    public static Amount Mist(ulong mist) => new(mist, 9);
}

/// <summary>Intent prefixes: what a Sui signature commits to.</summary>
public static class SuiIntent
{
    /// <summary>Intent message digest: <c>blake2b-256(scope ‖ version 0 ‖ app id 0 ‖ bcsValue)</c>.</summary>
    public static byte[] Digest(byte scope, ReadOnlySpan<byte> bcsValue) => CryptoNative.Blake2b256([scope, 0, 0, .. bcsValue]);

    public const byte TransactionData = 0;
    public const byte PersonalMessage = 3;
}

/// <summary>A Sui ed25519 account.</summary>
public sealed class SuiAccount : IAccount, IDisposable
{
    private const string PrivateKeyHrp = "suiprivkey";
    private readonly SecureBuffer _seed;

    public SuiAccount(ReadOnlySpan<byte> seed32, SuiChain? network = null, string? derivationPath = null)
    {
        if (seed32.Length != 32) throw new ArgumentException("Sui ed25519 keys are 32-byte seeds", nameof(seed32));
        _seed = SecureBuffer.FromBytes(seed32);
        PublicKey = CryptoNative.Ed25519GetPublicKey(seed32);
        AddressBytes = SuiAddress.FromEd25519PublicKey(PublicKey.Span);
        Network = network ?? SuiChain.Mainnet;
        Address = new Address(SuiAddress.Format(AddressBytes), Network.Id);
        DerivationPath = derivationPath;
    }

    /// <summary>Imports a Bech32 <c>suiprivkey1…</c> key (Sui CLI and wallets) or 32 raw hex bytes.</summary>
    public static SuiAccount FromPrivateKey(string privateKey, SuiChain? network = null)
    {
        byte[] raw;
        if (privateKey.StartsWith(PrivateKeyHrp + "1", StringComparison.Ordinal))
        {
            var (hrp, data, _) = CryptoNative.Bech32Decode(privateKey);
            if (hrp != PrivateKeyHrp || data.Length != 33) throw new FormatException("Invalid suiprivkey");
            if (data[0] != SuiAddress.Ed25519Flag)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
                throw new NotSupportedException("Only ed25519 Sui keys are supported");
            }
            raw = data[1..];
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
        }
        else
        {
            raw = System.Convert.FromHexString(privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? privateKey[2..] : privateKey);
        }
        try
        {
            if (raw.Length != 32) throw new FormatException("A Sui ed25519 private key is 32 bytes");
            return new SuiAccount(raw, network);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }

    public SuiChain Network { get; }
    public IChain Chain => Network;
    public Address Address { get; }
    public byte[] AddressBytes { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public string? DerivationPath { get; }

    /// <summary>Bech32 <c>suiprivkey1…</c>, the format of <c>sui keytool export</c> and wallets.</summary>
    public string ExportPrivateKey()
    {
        byte[] data = [SuiAddress.Ed25519Flag, .. _seed.Span];
        try
        {
            return CryptoNative.Bech32Encode(PrivateKeyHrp, data);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>Raw ed25519 signature over <paramref name="message"/> (usually an intent digest).</summary>
    public byte[] Sign(ReadOnlySpan<byte> message) => CryptoNative.Ed25519Sign(_seed.Span, message);

    /// <summary>Serialized signature (<c>flag ‖ signature ‖ public key</c>) over transaction data BCS.</summary>
    public byte[] SignTransaction(ReadOnlySpan<byte> transactionData) => Serialize(Sign(SuiIntent.Digest(SuiIntent.TransactionData, transactionData)));

    /// <summary>Serialized signature over a personal message, as wallets produce for <c>signPersonalMessage</c>.</summary>
    public byte[] SignPersonalMessage(ReadOnlySpan<byte> message) =>
        Serialize(Sign(SuiIntent.Digest(SuiIntent.PersonalMessage, new BcsWriter().Bytes(message).ToArray())));

    /// <summary>Verifies a serialized ed25519 signature over transaction data or a personal message.</summary>
    public static bool Verify(ReadOnlySpan<byte> serializedSignature, byte intentScope, ReadOnlySpan<byte> bcsValue, out string signer)
    {
        signer = "";
        if (serializedSignature.Length != 97 || serializedSignature[0] != SuiAddress.Ed25519Flag) return false;
        var pk = serializedSignature[65..];
        signer = SuiAddress.Format(SuiAddress.FromEd25519PublicKey(pk));
        return CryptoNative.Ed25519Verify(pk, SuiIntent.Digest(intentScope, bcsValue), serializedSignature[1..65]);
    }

    private byte[] Serialize(byte[] signature) => [SuiAddress.Ed25519Flag, .. signature, .. PublicKey.Span];

    public KeySigner CreateSigner() => new(Network, Address, _seed.Clone(), SignatureScheme.Ed25519);

    public void Dispose() => _seed.Dispose();
}

public static class SuiWalletExtensions
{
    /// <summary>Sui wallet path <c>m/44'/784'/index'/0'/0'</c> (SLIP-10 ed25519), as used by Sui Wallet, Slush and the Sui CLI.</summary>
    public static SuiAccount GetSuiAccount(this HdWallet wallet, uint index = 0, SuiChain? network = null)
    {
        string path = $"m/44'/784'/{index}'/0'/0'";
        using var key = wallet.DeriveEd25519Path(path);
        return new SuiAccount(key.PrivateKey.Span, network, path);
    }
}
