using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Crypto.Net.Native;

namespace Crypto.Net.Wallet;

/// <summary>Elliptic curve used by an <see cref="HdKey"/>.</summary>
public enum HdCurve
{
    /// <summary>BIP-32 over secp256k1 (Bitcoin, EVM, Cosmos).</summary>
    Secp256k1,
    /// <summary>SLIP-10 over ed25519, hardened-only (Solana, Stellar, ...).</summary>
    Ed25519,
}

/// <summary>Version bytes for extended key serialization.</summary>
public readonly record struct ExtendedKeyVersion(uint Private, uint Public)
{
    public static readonly ExtendedKeyVersion Mainnet = new(0x0488ADE4, 0x0488B21E);   // xprv / xpub
    public static readonly ExtendedKeyVersion Testnet = new(0x04358394, 0x043587CF);   // tprv / tpub
    public static readonly ExtendedKeyVersion SegWit = new(0x04B2430C, 0x04B24746);    // zprv / zpub (BIP-84)
    public static readonly ExtendedKeyVersion SegWitTestnet = new(0x045F18BC, 0x045F1CF6); // vprv / vpub
}

/// <summary>
/// A node of a hierarchical-deterministic key tree: private key, chain code and position metadata.
/// Dispose it when done; the private key lives in a <see cref="SecureBuffer"/>.
/// </summary>
public sealed class HdKey : IDisposable
{
    public const uint HardenedOffset = 0x80000000;

    private readonly SecureBuffer _privateKey;
    private readonly byte[] _chainCode;
    private byte[]? _publicKey;

    private HdKey(HdCurve curve, SecureBuffer privateKey, byte[] chainCode, byte depth, uint childIndex, uint parentFingerprint)
    {
        Curve = curve;
        _privateKey = privateKey;
        _chainCode = chainCode;
        Depth = depth;
        ChildIndex = childIndex;
        ParentFingerprint = parentFingerprint;
    }

    public HdCurve Curve { get; }
    public byte Depth { get; }
    public uint ChildIndex { get; }
    public uint ParentFingerprint { get; }
    public bool IsHardened => ChildIndex >= HardenedOffset;

    /// <summary>The 32-byte private key. Valid until the key is disposed.</summary>
    public SecureBuffer PrivateKey => _privateKey;

    public ReadOnlySpan<byte> ChainCode => _chainCode;

    /// <summary>Compressed secp256k1 public key (33 bytes) or ed25519 public key (32 bytes).</summary>
    public ReadOnlySpan<byte> PublicKey => _publicKey ??= Curve == HdCurve.Secp256k1
        ? CryptoNative.Secp256k1GetPublicKey(_privateKey.Span, compressed: true)
        : CryptoNative.Ed25519GetPublicKey(_privateKey.Span);

    /// <summary>BIP-32 fingerprint: first 4 bytes of HASH160(public key), big-endian.</summary>
    public uint Fingerprint => BinaryPrimitives.ReadUInt32BigEndian(CryptoNative.Hash160(PublicKey));

    /// <summary>Creates the master key from a BIP-39 (or arbitrary 16..64 byte) seed.</summary>
    public static HdKey FromSeed(ReadOnlySpan<byte> seed, HdCurve curve = HdCurve.Secp256k1)
    {
        if (seed.Length is < 16 or > 64) throw new ArgumentException("Seed must be 16..64 bytes", nameof(seed));
        string hmacKey = curve == HdCurve.Secp256k1 ? "Bitcoin seed" : "ed25519 seed";
        byte[] i = CryptoNative.HmacSha512(Encoding.ASCII.GetBytes(hmacKey), seed);
        try
        {
            var key = new HdKey(curve, SecureBuffer.FromBytes(i.AsSpan(0, 32)), i[32..], 0, 0, 0);
            if (curve == HdCurve.Secp256k1)
                _ = key.PublicKey; // throws for an invalid master key (probability ~2^-127)
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(i);
        }
    }

    /// <summary>Derives a direct child. For ed25519 the index is always hardened (SLIP-10).</summary>
    public HdKey Derive(uint index)
    {
        if (Depth == byte.MaxValue) throw new InvalidOperationException("Maximum derivation depth reached");
        var (key, cc) = Curve == HdCurve.Secp256k1
            ? CryptoNative.Bip32CkdPriv(_privateKey.Span, _chainCode, index)
            : CryptoNative.Slip10Ed25519CkdPriv(_privateKey.Span, _chainCode, index);
        if (Curve == HdCurve.Ed25519) index |= HardenedOffset;
        return new HdKey(Curve, SecureBuffer.FromBytesAndClear(key), cc, (byte)(Depth + 1), index, Fingerprint);
    }

    /// <summary>Derives along a path relative to this key, e.g. <c>m/44'/60'/0'/0/0</c> or <c>0/1</c>.</summary>
    public HdKey Derive(string path)
    {
        uint[] indices = DerivationPath.Parse(path);
        if (indices.Length == 0) return Clone();

        HdKey current = this;
        foreach (uint idx in indices)
        {
            HdKey next = current.Derive(idx);
            if (!ReferenceEquals(current, this)) current.Dispose();
            current = next;
        }
        return current;
    }

    public HdKey Clone() =>
        new(Curve, _privateKey.Clone(), (byte[])_chainCode.Clone(), Depth, ChildIndex, ParentFingerprint);

    /// <summary>Serializes as a Base58Check extended private key (xprv/tprv/zprv...). secp256k1 only.</summary>
    public string ToExtendedPrivateKey(ExtendedKeyVersion? version = null)
    {
        RequireSecp256k1();
        Span<byte> payload = stackalloc byte[78];
        WriteHeader(payload, (version ?? ExtendedKeyVersion.Mainnet).Private);
        payload[45] = 0;
        _privateKey.Span.CopyTo(payload[46..]);
        try
        {
            return CryptoNative.Base58CheckEncode(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Serializes as a Base58Check extended public key (xpub/tpub/zpub...). secp256k1 only.</summary>
    public string ToExtendedPublicKey(ExtendedKeyVersion? version = null)
    {
        RequireSecp256k1();
        Span<byte> payload = stackalloc byte[78];
        WriteHeader(payload, (version ?? ExtendedKeyVersion.Mainnet).Public);
        PublicKey.CopyTo(payload[45..]);
        return CryptoNative.Base58CheckEncode(payload);
    }

    /// <summary>Parses an extended private key (xprv/tprv/zprv/vprv).</summary>
    public static HdKey ParseExtendedPrivateKey(string extendedKey)
    {
        byte[] raw = CryptoNative.Base58CheckDecode(extendedKey);
        try
        {
            if (raw.Length != 78) throw new FormatException("Extended key must decode to 78 bytes");
            if (raw[45] != 0) throw new FormatException("Not an extended private key");
            var key = new HdKey(
                HdCurve.Secp256k1,
                SecureBuffer.FromBytes(raw.AsSpan(46, 32)),
                raw[13..45],
                raw[4],
                BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(9)),
                BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(5)));
            _ = key.PublicKey; // validates the scalar
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private void WriteHeader(Span<byte> payload, uint version)
    {
        BinaryPrimitives.WriteUInt32BigEndian(payload, version);
        payload[4] = Depth;
        BinaryPrimitives.WriteUInt32BigEndian(payload[5..], ParentFingerprint);
        BinaryPrimitives.WriteUInt32BigEndian(payload[9..], ChildIndex);
        _chainCode.CopyTo(payload[13..]);
    }

    private void RequireSecp256k1()
    {
        if (Curve != HdCurve.Secp256k1) throw new NotSupportedException("Extended key serialization is defined for secp256k1 only");
    }

    public void Dispose()
    {
        _privateKey.Dispose();
        CryptographicOperations.ZeroMemory(_chainCode);
    }
}

/// <summary>BIP-32 path parsing (<c>m/44'/0'/0'/0/0</c>, with <c>'</c>, <c>h</c> or <c>H</c> for hardened).</summary>
public static class DerivationPath
{
    public static uint[] Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        int start = segments.Length > 0 && segments[0] is "m" or "M" ? 1 : 0;

        var result = new uint[segments.Length - start];
        for (int i = start; i < segments.Length; i++)
        {
            string s = segments[i].Trim();
            bool hardened = s.EndsWith('\'') || s.EndsWith('h') || s.EndsWith('H');
            if (hardened) s = s[..^1];
            if (!uint.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint index) || index >= HdKey.HardenedOffset)
                throw new FormatException($"Invalid derivation path segment '{segments[i]}'");
            result[i - start] = hardened ? index | HdKey.HardenedOffset : index;
        }
        return result;
    }

    public static string Format(ReadOnlySpan<uint> indices)
    {
        var sb = new StringBuilder("m");
        foreach (uint i in indices)
            sb.Append('/').Append(i & ~HdKey.HardenedOffset).Append(i >= HdKey.HardenedOffset ? "'" : "");
        return sb.ToString();
    }

    /// <summary>Standard paths used by popular wallets.</summary>
    public static class Standard
    {
        public static string Bip44(uint coinType, uint account = 0, uint change = 0, uint index = 0) => $"m/44'/{coinType}'/{account}'/{change}/{index}";
        public static string Bip49(uint account = 0, uint change = 0, uint index = 0, bool testnet = false) => $"m/49'/{(testnet ? 1 : 0)}'/{account}'/{change}/{index}";
        public static string Bip84(uint account = 0, uint change = 0, uint index = 0, bool testnet = false) => $"m/84'/{(testnet ? 1 : 0)}'/{account}'/{change}/{index}";
        public static string Bip86(uint account = 0, uint change = 0, uint index = 0, bool testnet = false) => $"m/86'/{(testnet ? 1 : 0)}'/{account}'/{change}/{index}";
        public static string Ethereum(uint index = 0, uint account = 0) => $"m/44'/60'/{account}'/0/{index}";
        public static string Cosmos(uint index = 0, uint account = 0) => $"m/44'/118'/{account}'/0/{index}";
        /// <summary>Phantom / Solflare default: m/44'/501'/{account}'/0'.</summary>
        public static string Solana(uint account = 0) => $"m/44'/501'/{account}'/0'";
    }
}
