using System.Text.Json;
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Solana;

/// <summary>A Solana keypair (ed25519).</summary>
public sealed class SolanaAccount : IAccount, IDisposable
{
    private readonly SecureBuffer _seed;

    public SolanaAccount(ReadOnlySpan<byte> privateKey32, SolanaChain? cluster = null, string? derivationPath = null)
    {
        if (privateKey32.Length != 32) throw new ArgumentException("Solana private keys are 32-byte ed25519 seeds", nameof(privateKey32));
        _seed = SecureBuffer.FromBytes(privateKey32);
        Cluster = cluster ?? SolanaChain.MainnetBeta;
        PublicKey = CryptoNative.Ed25519GetPublicKey(privateKey32);
        Address = SolanaAddress.FromPublicKey(PublicKey.Span, Cluster.Id);
        DerivationPath = derivationPath;
    }

    /// <summary>Creates a fresh random keypair.</summary>
    public static SolanaAccount Generate(SolanaChain? cluster = null)
    {
        using var seed = SecureBuffer.FromBytesAndClear(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        return new SolanaAccount(seed.Span, cluster);
    }

    /// <summary>Imports a Phantom-style Base58 secret key (64 bytes: seed || public key) or a bare 32-byte seed.</summary>
    public static SolanaAccount FromBase58SecretKey(string secret, SolanaChain? cluster = null)
    {
        byte[] raw = CryptoNative.Base58Decode(secret);
        try
        {
            return FromSecretKeyBytes(raw, cluster);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>Imports a solana-cli keypair file (JSON array of 64 numbers).</summary>
    public static SolanaAccount FromKeypairJson(string json, SolanaChain? cluster = null)
    {
        int[] values = JsonSerializer.Deserialize<int[]>(json) ?? throw new FormatException("Invalid keypair JSON");
        if (values.Any(v => v is < 0 or > 255)) throw new FormatException("Keypair values must be bytes");
        byte[] raw = values.Select(v => (byte)v).ToArray();
        Array.Clear(values);
        try
        {
            return FromSecretKeyBytes(raw, cluster);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static SolanaAccount FromSecretKeyBytes(byte[] raw, SolanaChain? cluster)
    {
        if (raw.Length is not (32 or 64)) throw new FormatException("Solana secret keys are 32 or 64 bytes");
        var account = new SolanaAccount(raw.AsSpan(0, 32), cluster);
        if (raw.Length == 64 && !raw.AsSpan(32).SequenceEqual(account.PublicKey.Span))
            throw new FormatException("Secret key does not match its embedded public key");
        return account;
    }

    public SolanaChain Cluster { get; }
    public IChain Chain => Cluster;
    public Address Address { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public string? DerivationPath { get; }

    /// <summary>64-byte secret (seed || public key) in Base58, the format Phantom exports.</summary>
    public string ExportBase58SecretKey() => CryptoNative.Base58Encode([.. _seed.Span, .. PublicKey.Span]);

    /// <summary>solana-cli keypair JSON.</summary>
    public string ExportKeypairJson() => JsonSerializer.Serialize(_seed.Span.ToArray().Concat(PublicKey.ToArray()).Select(b => (int)b));

    public byte[] SignMessage(ReadOnlySpan<byte> message) => CryptoNative.Ed25519Sign(_seed.Span, message);

    public void Sign(SolanaTransaction transaction) => transaction.Sign(_seed.Span.ToArray());

    public KeySigner CreateSigner() => new(Cluster, Address, _seed.Clone(), SignatureScheme.Ed25519);

    public void Dispose() => _seed.Dispose();
}

/// <summary>HD wallet helpers using the Phantom/Solflare path m/44'/501'/{index}'/0'.</summary>
public static class SolanaWalletExtensions
{
    public static SolanaAccount GetSolanaAccount(this HdWallet wallet, uint index = 0, SolanaChain? cluster = null)
    {
        string path = DerivationPath.Standard.Solana(index);
        using var key = wallet.DeriveEd25519Path(path);
        return new SolanaAccount(key.PrivateKey.Span, cluster, path);
    }
}
