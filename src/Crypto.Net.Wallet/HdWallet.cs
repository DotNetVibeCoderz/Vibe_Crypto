using System.Security.Cryptography;
using System.Text;
using Crypto.Net.Native;

namespace Crypto.Net.Wallet;

/// <summary>
/// Multi-chain HD wallet built from a BIP-39 mnemonic. Holds the master secp256k1 (BIP-32) and
/// ed25519 (SLIP-10) roots plus the Substrate mini-secret, all in zeroizing buffers.
/// Chain packages add extension methods (e.g. <c>GetEvmAccount</c>, <c>GetSolanaAccount</c>).
/// </summary>
public sealed class HdWallet : IDisposable
{
    private readonly HdKey _secpRoot;
    private readonly HdKey _edRoot;
    private readonly SecureBuffer? _mnemonic;
    private readonly SecureBuffer? _substrateSeed;
    private bool _disposed;

    private HdWallet(ReadOnlySpan<byte> seed, string? normalizedMnemonic, byte[]? substrateSeed)
    {
        _secpRoot = HdKey.FromSeed(seed, HdCurve.Secp256k1);
        _edRoot = HdKey.FromSeed(seed, HdCurve.Ed25519);
        if (normalizedMnemonic is not null)
            _mnemonic = SecureBuffer.FromBytesAndClear(Encoding.UTF8.GetBytes(normalizedMnemonic));
        if (substrateSeed is not null)
            _substrateSeed = SecureBuffer.FromBytesAndClear(substrateSeed);
    }

    /// <summary>Restores a wallet from a mnemonic phrase (validated, whitespace-normalized).</summary>
    public static HdWallet FromMnemonic(string mnemonic, string? passphrase = "")
    {
        string normalized = Mnemonic.Normalize(mnemonic);
        byte[] seed = Mnemonic.ToSeed(normalized, passphrase);
        try
        {
            return new HdWallet(seed, normalized, Mnemonic.ToSubstrateSeed(normalized, passphrase));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Creates a wallet from a raw BIP-32 seed. Substrate (sr25519) derivation is unavailable in this mode.</summary>
    public static HdWallet FromSeed(ReadOnlySpan<byte> seed) => new(seed, null, null);

    /// <summary>Generates a brand-new wallet. Back up <see cref="RevealMnemonic"/> immediately.</summary>
    public static HdWallet Generate(int wordCount = 12, string? passphrase = "") =>
        FromMnemonic(Mnemonic.Generate(wordCount), passphrase);

    public bool HasMnemonic => _mnemonic is not null;

    /// <summary>Returns the mnemonic phrase. Avoid keeping the returned string alive longer than necessary.</summary>
    public string RevealMnemonic()
    {
        ThrowIfDisposed();
        if (_mnemonic is null) throw new InvalidOperationException("Wallet was created from a raw seed");
        return Encoding.UTF8.GetString(_mnemonic.Span);
    }

    /// <summary>The BIP-32 secp256k1 master key (m).</summary>
    public HdKey MasterKey
    {
        get
        {
            ThrowIfDisposed();
            return _secpRoot;
        }
    }

    /// <summary>Derives a secp256k1 key with BIP-32, e.g. <c>m/84'/0'/0'/0/0</c>. Dispose the result.</summary>
    public HdKey DerivePath(string path)
    {
        ThrowIfDisposed();
        return _secpRoot.Derive(path);
    }

    /// <summary>Derives an ed25519 key with SLIP-10 (every level hardened), e.g. <c>m/44'/501'/0'/0'</c>.</summary>
    public HdKey DeriveEd25519Path(string path)
    {
        ThrowIfDisposed();
        return _edRoot.Derive(path);
    }

    /// <summary>
    /// Substrate sr25519 keypair for a derivation path such as <c>//polkadot//0</c> (empty = root account,
    /// the default used by Polkadot.js / Talisman / SubWallet). Returns the 64-byte secret key and public key.
    /// </summary>
    public (SecureBuffer SecretKey, byte[] PublicKey) DeriveSr25519(string path = "")
    {
        ThrowIfDisposed();
        if (_substrateSeed is null) throw new InvalidOperationException("Substrate derivation requires a wallet created from a mnemonic");
        var (root, rootPub) = CryptoNative.Sr25519FromSeed(_substrateSeed.Span);
        if (string.IsNullOrEmpty(path))
            return (SecureBuffer.FromBytesAndClear(root), rootPub);

        try
        {
            var (sk, pk) = CryptoNative.Sr25519Derive(root, path);
            return (SecureBuffer.FromBytesAndClear(sk), pk);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(root);
        }
    }

    /// <summary>Substrate ed25519 seed for a hard-only path (e.g. <c>//0</c>); empty path = root.</summary>
    public SecureBuffer DeriveSubstrateEd25519(string path = "")
    {
        ThrowIfDisposed();
        if (_substrateSeed is null) throw new InvalidOperationException("Substrate derivation requires a wallet created from a mnemonic");
        return string.IsNullOrEmpty(path)
            ? _substrateSeed.Clone()
            : SecureBuffer.FromBytesAndClear(CryptoNative.SubstrateEd25519Derive(_substrateSeed.Span, path));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _secpRoot.Dispose();
        _edRoot.Dispose();
        _mnemonic?.Dispose();
        _substrateSeed?.Dispose();
        _disposed = true;
    }
}
