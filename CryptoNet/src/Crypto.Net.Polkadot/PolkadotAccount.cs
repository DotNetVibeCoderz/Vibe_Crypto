using System.Security.Cryptography;
using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Polkadot;

/// <summary>A Substrate account using sr25519 (default, as in Polkadot.js) or ed25519.</summary>
public sealed class PolkadotAccount : IAccount, IDisposable
{
    /// <summary>Substrate development phrase used by <c>//Alice</c>, <c>//Bob</c>… Never use it with real funds.</summary>
    public const string DevPhrase = "bottom drive obey lake curtain smoke basket hold race lonely fit walk";

    private readonly SecureBuffer _secret;

    private PolkadotAccount(SecureBuffer secret, byte[] publicKey, SignatureScheme scheme, PolkadotChain chain, string? derivationPath)
    {
        _secret = secret;
        PublicKey = publicKey;
        Scheme = scheme;
        Network = chain;
        Address = Ss58Address.FromPublicKey(publicKey, chain.Ss58Prefix, chain.Id);
        DerivationPath = derivationPath;
    }

    /// <summary>Creates an account from a 64-byte sr25519 secret key or a 32-byte ed25519 seed.</summary>
    public static PolkadotAccount FromSecret(ReadOnlySpan<byte> secret, SignatureScheme scheme = SignatureScheme.Sr25519, PolkadotChain? chain = null, string? derivationPath = null)
    {
        chain ??= PolkadotChain.Polkadot;
        return scheme switch
        {
            SignatureScheme.Sr25519 => new(SecureBuffer.FromBytes(secret), CryptoNative.Sr25519GetPublicKey(secret), scheme, chain, derivationPath),
            SignatureScheme.Ed25519 => new(SecureBuffer.FromBytes(secret), CryptoNative.Ed25519GetPublicKey(secret), scheme, chain, derivationPath),
            _ => throw new ArgumentException("Substrate accounts use sr25519 or ed25519", nameof(scheme)),
        };
    }

    /// <summary>
    /// Restores an account from a mnemonic (substrate-bip39) and optional derivation path such as <c>//polkadot//0</c>.
    /// The empty path gives the same root account as Polkadot.js, Talisman and SubWallet.
    /// </summary>
    public static PolkadotAccount FromMnemonic(string mnemonic, string path = "", SignatureScheme scheme = SignatureScheme.Sr25519, PolkadotChain? chain = null, string? passphrase = "")
    {
        byte[] seed = Mnemonic.ToSubstrateSeed(mnemonic, passphrase);
        try
        {
            return FromSeed(seed, path, scheme, chain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Derives from a 32-byte Substrate "secret seed" (mini secret).</summary>
    public static PolkadotAccount FromSeed(ReadOnlySpan<byte> seed, string path = "", SignatureScheme scheme = SignatureScheme.Sr25519, PolkadotChain? chain = null)
    {
        if (scheme == SignatureScheme.Sr25519)
        {
            var (root, _) = CryptoNative.Sr25519FromSeed(seed);
            try
            {
                if (string.IsNullOrEmpty(path)) return FromSecret(root, scheme, chain, path);
                var (derived, _) = CryptoNative.Sr25519Derive(root, path);
                try { return FromSecret(derived, scheme, chain, path); }
                finally { CryptographicOperations.ZeroMemory(derived); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(root);
            }
        }
        byte[] edSeed = string.IsNullOrEmpty(path) ? seed.ToArray() : CryptoNative.SubstrateEd25519Derive(seed, path);
        try { return FromSecret(edSeed, SignatureScheme.Ed25519, chain, path); }
        finally { CryptographicOperations.ZeroMemory(edSeed); }
    }

    /// <summary>
    /// Parses a secret URI: <c>"&lt;mnemonic&gt;//hard/soft"</c> or just <c>"//Alice"</c> (uses <see cref="DevPhrase"/>).
    /// </summary>
    public static PolkadotAccount FromSuri(string suri, SignatureScheme scheme = SignatureScheme.Sr25519, PolkadotChain? chain = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suri);
        suri = suri.Trim();
        int slash = suri.IndexOf('/');
        string phrase = slash < 0 ? suri : suri[..slash].Trim();
        string path = slash < 0 ? "" : suri[slash..];
        return FromMnemonic(phrase.Length == 0 ? DevPhrase : phrase, path, scheme, chain);
    }

    public PolkadotChain Network { get; }
    public IChain Chain => Network;
    public Address Address { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public SignatureScheme Scheme { get; }
    public string? DerivationPath { get; }

    /// <summary>Address of the same key on another network.</summary>
    public string AddressFor(PolkadotChain chain) => Ss58Address.Encode(PublicKey.Span, chain.Ss58Prefix);

    /// <summary>Signs raw bytes (sr25519 signatures are randomized).</summary>
    public byte[] Sign(ReadOnlySpan<byte> message) => Scheme == SignatureScheme.Sr25519
        ? CryptoNative.Sr25519Sign(_secret.Span, message)
        : CryptoNative.Ed25519Sign(_secret.Span, message);

    /// <summary>Signs a message the way the Polkadot.js extension's <c>signRaw</c> does: wrapped in <c>&lt;Bytes&gt;…&lt;/Bytes&gt;</c>.</summary>
    public byte[] SignMessage(string message) => Sign(Encoding.UTF8.GetBytes($"<Bytes>{message}</Bytes>"));

    public static bool VerifyMessage(string message, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey, SignatureScheme scheme = SignatureScheme.Sr25519)
    {
        byte[] wrapped = Encoding.UTF8.GetBytes($"<Bytes>{message}</Bytes>");
        return scheme == SignatureScheme.Sr25519
            ? CryptoNative.Sr25519Verify(publicKey, wrapped, signature)
            : CryptoNative.Ed25519Verify(publicKey, wrapped, signature);
    }

    public KeySigner CreateSigner() => new(Network, Address, _secret.Clone(), Scheme);

    public void Dispose() => _secret.Dispose();
}

/// <summary>HD wallet helpers for Substrate accounts.</summary>
public static class PolkadotWalletExtensions
{
    /// <summary>Substrate account from the wallet mnemonic; empty <paramref name="path"/> = root account (Polkadot.js default).</summary>
    public static PolkadotAccount GetPolkadotAccount(this HdWallet wallet, string path = "", PolkadotChain? chain = null, SignatureScheme scheme = SignatureScheme.Sr25519)
    {
        if (scheme == SignatureScheme.Sr25519)
        {
            var (secret, _) = wallet.DeriveSr25519(path);
            using (secret) return PolkadotAccount.FromSecret(secret.Span, scheme, chain, path);
        }
        using var seed = wallet.DeriveSubstrateEd25519(path);
        return PolkadotAccount.FromSecret(seed.Span, SignatureScheme.Ed25519, chain, path);
    }
}
