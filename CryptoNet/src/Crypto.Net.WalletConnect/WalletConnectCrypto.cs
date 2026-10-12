using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crypto.Net.Native;

namespace Crypto.Net.WalletConnect;

/// <summary>
/// WalletConnect v2 cryptography: X25519 key agreement with HKDF-SHA256, ChaCha20-Poly1305 envelopes, topics
/// (<c>sha256(symKey)</c>), the relay's <c>did:key</c> JWT, and pairing URIs.
/// </summary>
public static class WalletConnectCrypto
{
    public const byte EnvelopeType0 = 0;
    public const byte EnvelopeType1 = 1;
    private const int IvLength = 12;

    /// <summary>A new X25519 key pair (private, public).</summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        byte[] priv = RandomNumberGenerator.GetBytes(32);
        return (priv, CryptoNative.X25519GetPublicKey(priv));
    }

    /// <summary>Shared symmetric key: <c>HKDF-SHA256(X25519(self, peer))</c>, 32 bytes, no salt or info.</summary>
    public static byte[] DeriveSymKey(ReadOnlySpan<byte> selfPrivateKey, ReadOnlySpan<byte> peerPublicKey)
    {
        byte[] shared = CryptoNative.X25519(selfPrivateKey, peerPublicKey);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    /// <summary>The topic of a symmetric key: <c>hex(sha256(key))</c>.</summary>
    public static string Topic(ReadOnlySpan<byte> symKey) => Convert.ToHexStringLower(CryptoNative.Sha256(symKey));

    /// <summary>
    /// Encrypts <paramref name="message"/> into a base64 envelope: type 0 is <c>type ‖ iv ‖ sealed</c>, type 1 adds the
    /// sender's public key after the type byte.
    /// </summary>
    public static string Encrypt(ReadOnlySpan<byte> symKey, string message, byte[]? iv = null, byte[]? senderPublicKey = null)
    {
        iv ??= RandomNumberGenerator.GetBytes(IvLength);
        if (iv.Length != IvLength) throw new ArgumentException("The IV is 12 bytes", nameof(iv));
        byte[] plain = Encoding.UTF8.GetBytes(message);
        byte[] sealedBox = new byte[plain.Length + 16];
        using (var aead = new ChaCha20Poly1305(symKey))
            aead.Encrypt(iv, plain, sealedBox.AsSpan(0, plain.Length), sealedBox.AsSpan(plain.Length));
        byte[] envelope = senderPublicKey is null
            ? [EnvelopeType0, .. iv, .. sealedBox]
            : [EnvelopeType1, .. senderPublicKey, .. iv, .. sealedBox];
        return Convert.ToBase64String(envelope);
    }

    /// <summary>Decrypts a type 0 or type 1 envelope; returns the message and, for type 1, the sender's public key.</summary>
    public static (string Message, byte[]? SenderPublicKey) Decrypt(ReadOnlySpan<byte> symKey, string envelope)
    {
        byte[] raw = Convert.FromBase64String(envelope);
        if (raw.Length < 1 + IvLength + 16) throw new FormatException("Envelope is too short");
        int offset = 1;
        byte[]? sender = null;
        if (raw[0] == EnvelopeType1)
        {
            if (raw.Length < 1 + 32 + IvLength + 16) throw new FormatException("Envelope is too short");
            sender = raw[1..33];
            offset = 33;
        }
        else if (raw[0] != EnvelopeType0)
        {
            throw new NotSupportedException($"Envelope type {raw[0]} is not supported");
        }
        var iv = raw.AsSpan(offset, IvLength);
        var body = raw.AsSpan(offset + IvLength);
        byte[] plain = new byte[body.Length - 16];
        using (var aead = new ChaCha20Poly1305(symKey))
            aead.Decrypt(iv, body[..^16], body[^16..], plain);
        return (Encoding.UTF8.GetString(plain), sender);
    }

    /// <summary>The <c>did:key</c> of an ed25519 public key (multicodec <c>0xed01</c>, base58btc).</summary>
    public static string DidKey(ReadOnlySpan<byte> ed25519PublicKey) => "did:key:z" + CryptoNative.Base58Encode([0xed, 0x01, .. ed25519PublicKey]);

    /// <summary>The relay's auth JWT: EdDSA over <c>base64url(header).base64url(payload)</c> with a <c>did:key</c> issuer.</summary>
    public static string RelayJwt(ReadOnlySpan<byte> ed25519Seed, string subject, string audience, long issuedAt, long ttlSeconds)
    {
        byte[] pub = CryptoNative.Ed25519GetPublicKey(ed25519Seed);
        string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "EdDSA", typ = "JWT" }));
        string payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = DidKey(pub), sub = subject, aud = audience, iat = issuedAt, exp = issuedAt + ttlSeconds }));
        string data = header + "." + payload;
        return data + "." + Base64Url(CryptoNative.Ed25519Sign(ed25519Seed, Encoding.UTF8.GetBytes(data)));
    }

    /// <summary><c>wc:{topic}@2?relay-protocol=irn&amp;symKey=…&amp;expiryTimestamp=…</c>, what wallets scan as a QR code.</summary>
    public static string PairingUri(string topic, ReadOnlySpan<byte> symKey, long expiryTimestamp) =>
        $"wc:{topic}@2?relay-protocol=irn&symKey={Convert.ToHexStringLower(symKey)}&expiryTimestamp={expiryTimestamp}";

    /// <summary>Parses a pairing URI into its topic and symmetric key.</summary>
    public static (string Topic, byte[] SymKey) ParsePairingUri(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string body = uri.StartsWith("wc:", StringComparison.Ordinal) ? uri[3..] : throw new FormatException("Not a WalletConnect URI");
        int at = body.IndexOf('@'), q = body.IndexOf('?');
        if (at < 0 || q < at || body[(at + 1)..q] != "2") throw new FormatException("Only WalletConnect v2 URIs are supported");
        string? sym = body[(q + 1)..].Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "symKey")?.ElementAtOrDefault(1);
        byte[] key = sym is { Length: 64 } ? Convert.FromHexString(sym) : throw new FormatException("Missing symKey");
        string topic = body[..at];
        if (topic != Topic(key)) throw new FormatException("Topic does not match the symmetric key");
        return (topic, key);
    }

    private static string Base64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
