using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Wallet;

/// <summary>
/// <see cref="ISigner"/> backed by an in-memory private key held in a <see cref="SecureBuffer"/>.
/// The signer owns the buffer and zeroes it on dispose.
/// </summary>
public sealed class KeySigner : ISigner
{
    private readonly SecureBuffer _secret;
    private bool _disposed;

    public KeySigner(IChain chain, Address address, SecureBuffer secretKey, SignatureScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(secretKey);
        Chain = chain;
        Address = address;
        Scheme = scheme;
        _secret = secretKey;
        PublicKey = scheme switch
        {
            SignatureScheme.Secp256k1Ecdsa => CryptoNative.Secp256k1GetPublicKey(secretKey.Span, compressed: true),
            SignatureScheme.Secp256k1Schnorr => CryptoNative.Secp256k1XOnlyPublicKey(secretKey.Span),
            SignatureScheme.Ed25519 => CryptoNative.Ed25519GetPublicKey(secretKey.Span),
            SignatureScheme.Sr25519 => CryptoNative.Sr25519GetPublicKey(secretKey.Span),
            _ => throw new ArgumentOutOfRangeException(nameof(scheme)),
        };
    }

    public IChain Chain { get; }
    public Address Address { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public SignatureScheme Scheme { get; }

    public ValueTask<Signature> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Sign(payload.Span));
    }

    /// <summary>Synchronous variant of <see cref="SignAsync"/>.</summary>
    public Signature Sign(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (Scheme)
        {
            case SignatureScheme.Secp256k1Ecdsa:
                if (payload.Length != 32) throw new ArgumentException("ECDSA signers sign 32-byte digests; hash the message first", nameof(payload));
                var (sig, recId) = CryptoNative.Secp256k1SignRecoverable(_secret.Span, payload);
                return new Signature(sig, recId);
            case SignatureScheme.Secp256k1Schnorr:
                if (payload.Length != 32) throw new ArgumentException("Schnorr signers sign 32-byte digests", nameof(payload));
                return new Signature(CryptoNative.SchnorrSign(_secret.Span, payload, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            case SignatureScheme.Ed25519:
                return new Signature(CryptoNative.Ed25519Sign(_secret.Span, payload));
            case SignatureScheme.Sr25519:
                return new Signature(CryptoNative.Sr25519Sign(_secret.Span, payload));
            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>Gives temporary access to the key for chain-specific builders (e.g. Taproot tweaking).</summary>
    internal ReadOnlySpan<byte> SecretSpan => _secret.Span;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _secret.Dispose();
        _disposed = true;
    }
}
