namespace Crypto.Net.Core;

/// <summary>Signature algorithm produced by an <see cref="ISigner"/>.</summary>
public enum SignatureScheme
{
    /// <summary>secp256k1 ECDSA over a 32-byte digest: 64-byte r||s plus recovery id (EVM, Bitcoin SegWit v0, Cosmos).</summary>
    Secp256k1Ecdsa,
    /// <summary>BIP-340 Schnorr over secp256k1 (Bitcoin Taproot).</summary>
    Secp256k1Schnorr,
    /// <summary>Ed25519 / EdDSA (Solana, Substrate ed25519).</summary>
    Ed25519,
    /// <summary>Schnorrkel sr25519 (Polkadot, Kusama).</summary>
    Sr25519,
}

/// <summary>
/// Signs payloads on behalf of an account. Implementations can wrap in-memory keys, HSMs, hardware wallets
/// or remote signers, keeping key material out of application logic.
/// </summary>
public interface ISigner : IAsyncDisposable
{
    IChain Chain { get; }
    Address Address { get; }
    ReadOnlyMemory<byte> PublicKey { get; }
    SignatureScheme Scheme { get; }

    /// <summary>
    /// Signs <paramref name="payload"/>. For <see cref="SignatureScheme.Secp256k1Ecdsa"/> and
    /// <see cref="SignatureScheme.Secp256k1Schnorr"/> the payload must be a 32-byte digest;
    /// Ed25519 and sr25519 sign the message bytes directly.
    /// </summary>
    ValueTask<Signature> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);
}
