using System.Security.Cryptography;
using System.Text;
using Crypto.Net.Native.Managed;

namespace Crypto.Net.Native;

/// <summary>Selects which implementation <see cref="CryptoNative"/> uses.</summary>
public enum CryptoBackend
{
    /// <summary>Use the Rust library when it loads, otherwise the managed implementation.</summary>
    Auto,
    /// <summary>Always use the Rust library; throws <see cref="DllNotFoundException"/> when it is missing.</summary>
    Native,
    /// <summary>Always use the portable managed implementation (not constant-time).</summary>
    Managed,
}

/// <summary>
/// Unified gateway to every cryptographic primitive and codec in Crypto.Net. Calls go to the Rust
/// core (<c>cryptonet</c>) when available and transparently fall back to managed C# otherwise.
/// Both backends produce byte-identical output; sr25519 is native-only.
/// </summary>
public static unsafe class CryptoNative
{
    private static volatile CryptoBackend _backend = ReadBackendFromEnvironment();
    private static readonly AsyncLocal<CryptoBackend?> _scoped = new();

    /// <summary>
    /// The preferred backend. Defaults to <see cref="CryptoBackend.Auto"/>, or to the value of the
    /// <c>CRYPTONET_BACKEND</c> environment variable (<c>auto</c>, <c>native</c>, <c>managed</c>).
    /// </summary>
    public static CryptoBackend Backend
    {
        get => _backend;
        set => _backend = value;
    }

    /// <summary>
    /// Overrides the backend for the current async flow only (other threads are unaffected) until the
    /// returned scope is disposed. Useful for benchmarks and tests.
    /// </summary>
    public static IDisposable UseBackend(CryptoBackend backend)
    {
        var previous = _scoped.Value;
        _scoped.Value = backend;
        return new BackendScope(previous);
    }

    private sealed class BackendScope(CryptoBackend? previous) : IDisposable
    {
        public void Dispose() => _scoped.Value = previous;
    }

    /// <summary>The backend in effect for the current async flow.</summary>
    public static CryptoBackend EffectiveBackend => _scoped.Value ?? _backend;

    /// <summary>True when calls are currently served by the Rust library.</summary>
    public static bool IsNativeActive => (_scoped.Value ?? _backend) switch
    {
        CryptoBackend.Managed => false,
        CryptoBackend.Native => NativeLoader.IsNativeAvailable
            ? true
            : throw new DllNotFoundException($"Native backend requested but the cryptonet library could not be loaded. {NativeLoader.LoadError}"),
        _ => NativeLoader.IsNativeAvailable,
    };

    /// <summary>
    /// SHA-2, HMAC and PBKDF2 are hardware-accelerated by the OS crypto stack (SHA-NI / ARMv8 SHA), which beats
    /// crossing the FFI boundary. In <see cref="CryptoBackend.Auto"/> they therefore use the platform; only an explicit
    /// <see cref="CryptoBackend.Native"/> forces Rust.
    /// </summary>
    private static bool UseRustForSha2 => EffectiveBackend == CryptoBackend.Native && IsNativeActive;

    private static CryptoBackend ReadBackendFromEnvironment() =>
        Environment.GetEnvironmentVariable("CRYPTONET_BACKEND")?.Trim().ToLowerInvariant() switch
        {
            "native" => CryptoBackend.Native,
            "managed" => CryptoBackend.Managed,
            _ => CryptoBackend.Auto,
        };

    private static void Check(int rc, string operation)
    {
        if (rc == NativeMethods.CN_SUCCESS) return;
        throw rc switch
        {
            NativeMethods.CN_ERR_NULL_POINTER => new ArgumentNullException(operation, "Null pointer passed to native code"),
            NativeMethods.CN_ERR_INVALID_LENGTH => new ArgumentException($"{operation}: invalid length"),
            NativeMethods.CN_ERR_INVALID_INPUT => new FormatException($"{operation}: invalid input"),
            NativeMethods.CN_ERR_PANIC => new CryptographicException($"{operation}: internal error in native library"),
            _ => new CryptographicException($"{operation} failed (code {rc})"),
        };
    }

    private static int CheckBool(int rc, string operation)
    {
        if (rc is 0 or 1) return rc;
        Check(rc, operation);
        return 0;
    }

    private static void RequireLength(ReadOnlySpan<byte> span, int length, string name)
    {
        if (span.Length != length) throw new ArgumentException($"{name} must be {length} bytes", name);
    }

    private static void RequireDestination(Span<byte> destination, int length)
    {
        if (destination.Length < length) throw new ArgumentException($"Destination must be at least {length} bytes", nameof(destination));
    }

    #region Hashing

    public static byte[] Sha256(ReadOnlySpan<byte> data) { var o = new byte[32]; Sha256(data, o); return o; }

    public static void Sha256(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        RequireDestination(destination, 32);
        if (UseRustForSha2)
        {
            fixed (byte* d = data) fixed (byte* o = destination) Check(NativeMethods.cn_sha256(d, (nuint)data.Length, o), "SHA-256");
            return;
        }
        SHA256.HashData(data, destination);
    }

    public static byte[] DoubleSha256(ReadOnlySpan<byte> data) { var o = new byte[32]; DoubleSha256(data, o); return o; }

    public static void DoubleSha256(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        RequireDestination(destination, 32);
        if (UseRustForSha2)
        {
            fixed (byte* d = data) fixed (byte* o = destination) Check(NativeMethods.cn_double_sha256(d, (nuint)data.Length, o), "SHA-256d");
            return;
        }
        Span<byte> tmp = stackalloc byte[32];
        SHA256.HashData(data, tmp);
        SHA256.HashData(tmp, destination);
    }

    public static byte[] Sha512(ReadOnlySpan<byte> data) { var o = new byte[64]; Sha512(data, o); return o; }

    public static void Sha512(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        RequireDestination(destination, 64);
        if (UseRustForSha2)
        {
            fixed (byte* d = data) fixed (byte* o = destination) Check(NativeMethods.cn_sha512(d, (nuint)data.Length, o), "SHA-512");
            return;
        }
        SHA512.HashData(data, destination);
    }

    public static byte[] Keccak256(ReadOnlySpan<byte> data) { var o = new byte[32]; Keccak256(data, o); return o; }

    public static void Keccak256(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        RequireDestination(destination, 32);
        if (IsNativeActive)
        {
            fixed (byte* d = data) fixed (byte* o = destination) Check(NativeMethods.cn_keccak256(d, (nuint)data.Length, o), "Keccak-256");
            return;
        }
        Keccak.Hash256(data, destination);
    }

    public static byte[] Ripemd160(ReadOnlySpan<byte> data) { var o = new byte[20]; Ripemd160(data, o); return o; }

    public static void Ripemd160(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        RequireDestination(destination, 20);
        if (IsNativeActive)
        {
            fixed (byte* d = data) fixed (byte* o = destination) Check(NativeMethods.cn_ripemd160(d, (nuint)data.Length, o), "RIPEMD-160");
            return;
        }
        Managed.Ripemd160.Hash(data, destination);
    }

    /// <summary>RIPEMD-160(SHA-256(data)), used for Bitcoin and Cosmos addresses.</summary>
    public static byte[] Hash160(ReadOnlySpan<byte> data) { var o = new byte[20]; Hash160(data, o); return o; }

    public static void Hash160(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        RequireDestination(destination, 20);
        if (IsNativeActive)
        {
            fixed (byte* d = data) fixed (byte* o = destination) Check(NativeMethods.cn_hash160(d, (nuint)data.Length, o), "HASH160");
            return;
        }
        Span<byte> sha = stackalloc byte[32];
        SHA256.HashData(data, sha);
        Managed.Ripemd160.Hash(sha, destination);
    }

    /// <summary>Unkeyed BLAKE2b with an output length equal to <paramref name="destination"/>'s length (1..64).</summary>
    public static void Blake2b(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (destination.Length is < 1 or > 64) throw new ArgumentException("BLAKE2b output must be 1..64 bytes", nameof(destination));
        if (IsNativeActive)
        {
            fixed (byte* d = data) fixed (byte* o = destination) Check(NativeMethods.cn_blake2b(d, (nuint)data.Length, o, (nuint)destination.Length), "BLAKE2b");
            return;
        }
        Managed.Blake2b.Hash(data, destination);
    }

    public static byte[] Blake2b(ReadOnlySpan<byte> data, int outputLength) { var o = new byte[outputLength]; Blake2b(data, o); return o; }
    public static byte[] Blake2b128(ReadOnlySpan<byte> data) => Blake2b(data, 16);
    public static byte[] Blake2b256(ReadOnlySpan<byte> data) => Blake2b(data, 32);
    public static byte[] Blake2b512(ReadOnlySpan<byte> data) => Blake2b(data, 64);

    /// <summary>BIP-340 tagged hash: SHA256(SHA256(tag) || SHA256(tag) || msg).</summary>
    public static byte[] TaggedHash(string tag, ReadOnlySpan<byte> message)
    {
        ArgumentNullException.ThrowIfNull(tag);
        byte[] tagBytes = Encoding.UTF8.GetBytes(tag);
        byte[] output = new byte[32];
        if (IsNativeActive)
        {
            fixed (byte* t = tagBytes) fixed (byte* m = message) fixed (byte* o = output)
                Check(NativeMethods.cn_tagged_hash(t, (nuint)tagBytes.Length, m, (nuint)message.Length, o), "Tagged hash");
            return output;
        }
        byte[] th = SHA256.HashData(tagBytes);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(th);
        h.AppendData(th);
        h.AppendData(message);
        h.GetHashAndReset(output);
        return output;
    }

    #endregion

    #region secp256k1

    public static byte[] Secp256k1GetPublicKey(ReadOnlySpan<byte> secretKey, bool compressed = true)
    {
        RequireLength(secretKey, 32, nameof(secretKey));
        if (!IsNativeActive) return Secp256k1Managed.GetPublicKey(secretKey, compressed);

        byte[] output = new byte[compressed ? 33 : 65];
        nuint len = (nuint)output.Length;
        fixed (byte* s = secretKey) fixed (byte* o = output)
            Check(NativeMethods.cn_secp256k1_pubkey(s, compressed ? 1 : 0, o, &len), "secp256k1 public key");
        return output;
    }

    /// <summary>Converts a SEC1 public key between compressed (33 bytes) and uncompressed (65 bytes) form.</summary>
    public static byte[] Secp256k1ConvertPublicKey(ReadOnlySpan<byte> publicKey, bool compressed)
    {
        if (!IsNativeActive) return Secp256k1Managed.ConvertPublicKey(publicKey, compressed);

        byte[] output = new byte[compressed ? 33 : 65];
        nuint len = (nuint)output.Length;
        fixed (byte* p = publicKey) fixed (byte* o = output)
            Check(NativeMethods.cn_secp256k1_convert_pubkey(p, (nuint)publicKey.Length, compressed ? 1 : 0, o, &len), "secp256k1 public key conversion");
        return output;
    }

    /// <summary>Deterministic (RFC 6979), low-S ECDSA signature over a 32-byte digest, with recovery id.</summary>
    public static (byte[] Signature, byte RecoveryId) Secp256k1SignRecoverable(ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> digest)
    {
        RequireLength(secretKey, 32, nameof(secretKey));
        RequireLength(digest, 32, nameof(digest));
        if (!IsNativeActive) return Secp256k1Managed.SignRecoverable(secretKey, digest);

        byte[] sig = new byte[64];
        byte recId;
        fixed (byte* s = secretKey) fixed (byte* d = digest) fixed (byte* o = sig)
            Check(NativeMethods.cn_secp256k1_sign_recoverable(s, d, o, &recId), "secp256k1 sign");
        return (sig, recId);
    }

    /// <summary>Verifies a 64-byte (r || s) low-S ECDSA signature.</summary>
    public static bool Secp256k1Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> digest, ReadOnlySpan<byte> signature)
    {
        if (digest.Length != 32 || signature.Length != 64) return false;
        if (!IsNativeActive) return Secp256k1Managed.Verify(publicKey, digest, signature);

        fixed (byte* p = publicKey) fixed (byte* d = digest) fixed (byte* s = signature)
            return CheckBool(NativeMethods.cn_secp256k1_verify(p, (nuint)publicKey.Length, d, s), "secp256k1 verify") == 1;
    }

    public static byte[] Secp256k1RecoverPublicKey(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> signature, byte recoveryId, bool compressed = false)
    {
        RequireLength(digest, 32, nameof(digest));
        RequireLength(signature, 64, nameof(signature));
        if (!IsNativeActive) return Secp256k1Managed.Recover(digest, signature, recoveryId, compressed);

        byte[] output = new byte[compressed ? 33 : 65];
        nuint len = (nuint)output.Length;
        fixed (byte* d = digest) fixed (byte* s = signature) fixed (byte* o = output)
            Check(NativeMethods.cn_secp256k1_recover_pubkey(d, s, recoveryId, compressed ? 1 : 0, o, &len), "secp256k1 recover");
        return output;
    }

    /// <summary>BIP-340 x-only (32-byte) public key.</summary>
    public static byte[] Secp256k1XOnlyPublicKey(ReadOnlySpan<byte> secretKey)
    {
        RequireLength(secretKey, 32, nameof(secretKey));
        if (!IsNativeActive) return Secp256k1Managed.XOnlyPublicKey(secretKey);

        byte[] output = new byte[32];
        fixed (byte* s = secretKey) fixed (byte* o = output)
            Check(NativeMethods.cn_secp256k1_xonly_pubkey(s, o), "x-only public key");
        return output;
    }

    /// <summary>BIP-340 Schnorr signature. <paramref name="auxRand"/> should be 32 fresh random bytes (empty = zeros).</summary>
    public static byte[] SchnorrSign(ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> auxRand = default)
    {
        RequireLength(secretKey, 32, nameof(secretKey));
        if (!auxRand.IsEmpty) RequireLength(auxRand, 32, nameof(auxRand));
        if (!IsNativeActive) return Secp256k1Managed.SchnorrSign(secretKey, message, auxRand);

        byte[] sig = new byte[64];
        fixed (byte* s = secretKey) fixed (byte* m = message) fixed (byte* a = auxRand) fixed (byte* o = sig)
            Check(NativeMethods.cn_schnorr_sign(s, m, (nuint)message.Length, auxRand.IsEmpty ? null : a, o), "Schnorr sign");
        return sig;
    }

    public static bool SchnorrVerify(ReadOnlySpan<byte> xOnlyPublicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (xOnlyPublicKey.Length != 32 || signature.Length != 64) return false;
        if (!IsNativeActive) return Secp256k1Managed.SchnorrVerify(xOnlyPublicKey, message, signature);

        fixed (byte* p = xOnlyPublicKey) fixed (byte* m = message) fixed (byte* s = signature)
            return CheckBool(NativeMethods.cn_schnorr_verify(p, m, (nuint)message.Length, s), "Schnorr verify") == 1;
    }

    /// <summary>BIP-341 output key Q = P + H_TapTweak(P || merkleRoot)·G. Returns the x-only key and y parity.</summary>
    public static (byte[] OutputKey, byte Parity) TaprootTweakPublicKey(ReadOnlySpan<byte> internalXOnlyKey, ReadOnlySpan<byte> merkleRoot = default)
    {
        RequireLength(internalXOnlyKey, 32, nameof(internalXOnlyKey));
        if (!IsNativeActive) return Secp256k1Managed.TaprootTweakPublicKey(internalXOnlyKey, merkleRoot);

        byte[] output = new byte[32];
        byte parity;
        fixed (byte* p = internalXOnlyKey) fixed (byte* m = merkleRoot) fixed (byte* o = output)
            Check(NativeMethods.cn_taproot_tweak_pubkey(p, m, (nuint)merkleRoot.Length, o, &parity), "Taproot tweak");
        return (output, parity);
    }

    /// <summary>BIP-341 tweaked secret key for key-path spending.</summary>
    public static byte[] TaprootTweakSecretKey(ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> merkleRoot = default)
    {
        RequireLength(secretKey, 32, nameof(secretKey));
        if (!IsNativeActive) return Secp256k1Managed.TaprootTweakSecretKey(secretKey, merkleRoot);

        byte[] output = new byte[32];
        fixed (byte* s = secretKey) fixed (byte* m = merkleRoot) fixed (byte* o = output)
            Check(NativeMethods.cn_taproot_tweak_seckey(s, m, (nuint)merkleRoot.Length, o), "Taproot secret tweak");
        return output;
    }

    /// <summary>BIP-32 private child key derivation (CKDpriv). Index ≥ 2^31 is hardened.</summary>
    public static (byte[] Key, byte[] ChainCode) Bip32CkdPriv(ReadOnlySpan<byte> parentKey, ReadOnlySpan<byte> parentChainCode, uint index)
    {
        RequireLength(parentKey, 32, nameof(parentKey));
        RequireLength(parentChainCode, 32, nameof(parentChainCode));
        if (!IsNativeActive) return Secp256k1Managed.Bip32CkdPriv(parentKey, parentChainCode, index);

        byte[] key = new byte[32], cc = new byte[32];
        fixed (byte* k = parentKey) fixed (byte* c = parentChainCode) fixed (byte* ok = key) fixed (byte* oc = cc)
            Check(NativeMethods.cn_bip32_ckd_priv(k, c, index, ok, oc), "BIP-32 derivation");
        return (key, cc);
    }

    #endregion

    #region Ed25519

    public static byte[] Ed25519GetPublicKey(ReadOnlySpan<byte> secretKey)
    {
        RequireLength(secretKey, 32, nameof(secretKey));
        if (!IsNativeActive) return Ed25519Managed.GetPublicKey(secretKey);

        byte[] pk = new byte[32];
        fixed (byte* s = secretKey) fixed (byte* o = pk) Check(NativeMethods.cn_ed25519_pubkey(s, o), "Ed25519 public key");
        return pk;
    }

    public static byte[] Ed25519Sign(ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> message)
    {
        RequireLength(secretKey, 32, nameof(secretKey));
        if (!IsNativeActive) return Ed25519Managed.Sign(secretKey, message);

        byte[] sig = new byte[64];
        fixed (byte* s = secretKey) fixed (byte* m = message) fixed (byte* o = sig)
            Check(NativeMethods.cn_ed25519_sign(s, m, (nuint)message.Length, o), "Ed25519 sign");
        return sig;
    }

    public static bool Ed25519Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        if (!IsNativeActive) return Ed25519Managed.Verify(publicKey, message, signature);

        fixed (byte* p = publicKey) fixed (byte* m = message) fixed (byte* s = signature)
            return CheckBool(NativeMethods.cn_ed25519_verify(p, m, (nuint)message.Length, s), "Ed25519 verify") == 1;
    }

    /// <summary>True when <paramref name="point"/> decodes to a valid ed25519 curve point (used for Solana PDAs).</summary>
    public static bool Ed25519IsOnCurve(ReadOnlySpan<byte> point) => point.Length == 32 && Ed25519Managed.IsOnCurve(point);

    /// <summary>SLIP-10 ed25519 child derivation (always hardened).</summary>
    public static (byte[] Key, byte[] ChainCode) Slip10Ed25519CkdPriv(ReadOnlySpan<byte> parentKey, ReadOnlySpan<byte> parentChainCode, uint index)
    {
        RequireLength(parentKey, 32, nameof(parentKey));
        RequireLength(parentChainCode, 32, nameof(parentChainCode));
        index |= 0x80000000;

        if (IsNativeActive)
        {
            byte[] key = new byte[32], cc = new byte[32];
            fixed (byte* k = parentKey) fixed (byte* c = parentChainCode) fixed (byte* ok = key) fixed (byte* oc = cc)
                Check(NativeMethods.cn_slip10_ed25519_ckd_priv(k, c, index, ok, oc), "SLIP-10 derivation");
            return (key, cc);
        }

        byte[] data = new byte[37];
        parentKey.CopyTo(data.AsSpan(1));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(33), index);
        byte[] i = HMACSHA512.HashData(parentChainCode, data);
        CryptographicOperations.ZeroMemory(data);
        var result = (i[..32], i[32..]);
        CryptographicOperations.ZeroMemory(i);
        return result;
    }

    /// <summary>Substrate ed25519 hard derivation of a 32-byte seed along a path such as <c>//Alice</c>.</summary>
    public static byte[] SubstrateEd25519Derive(ReadOnlySpan<byte> seed, string path)
    {
        RequireLength(seed, 32, nameof(seed));
        ArgumentNullException.ThrowIfNull(path);
        if (!IsNativeActive) return SubstrateDerivation.Ed25519Derive(seed, path);

        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        byte[] output = new byte[32];
        fixed (byte* s = seed) fixed (byte* p = pathBytes) fixed (byte* o = output)
            Check(NativeMethods.cn_substrate_ed25519_derive(s, p, (nuint)pathBytes.Length, o), "Substrate ed25519 derivation");
        return output;
    }

    #endregion

    #region sr25519 (native only)

    private static void RequireNativeFor(string feature)
    {
        if (!IsNativeActive)
            throw new PlatformNotSupportedException($"{feature} requires the native cryptonet library (no managed fallback). {NativeLoader.LoadError}");
    }

    /// <summary>Expands a 32-byte Substrate mini-secret into a 64-byte sr25519 secret key and 32-byte public key.</summary>
    public static (byte[] SecretKey, byte[] PublicKey) Sr25519FromSeed(ReadOnlySpan<byte> seed)
    {
        RequireLength(seed, 32, nameof(seed));
        RequireNativeFor("sr25519");
        byte[] sk = new byte[64], pk = new byte[32];
        fixed (byte* s = seed) fixed (byte* os = sk) fixed (byte* op = pk)
            Check(NativeMethods.cn_sr25519_from_seed(s, os, op), "sr25519 keypair");
        return (sk, pk);
    }

    /// <summary>Applies a Substrate derivation path (<c>//hard</c>, <c>/soft</c>) to a 64-byte sr25519 secret key.</summary>
    public static (byte[] SecretKey, byte[] PublicKey) Sr25519Derive(ReadOnlySpan<byte> secretKey, string path)
    {
        RequireLength(secretKey, 64, nameof(secretKey));
        ArgumentNullException.ThrowIfNull(path);
        RequireNativeFor("sr25519");
        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        byte[] sk = new byte[64], pk = new byte[32];
        fixed (byte* s = secretKey) fixed (byte* p = pathBytes) fixed (byte* os = sk) fixed (byte* op = pk)
            Check(NativeMethods.cn_sr25519_derive(s, p, (nuint)pathBytes.Length, os, op), "sr25519 derivation");
        return (sk, pk);
    }

    public static byte[] Sr25519GetPublicKey(ReadOnlySpan<byte> secretKey)
    {
        RequireLength(secretKey, 64, nameof(secretKey));
        RequireNativeFor("sr25519");
        byte[] pk = new byte[32];
        fixed (byte* s = secretKey) fixed (byte* o = pk) Check(NativeMethods.cn_sr25519_public(s, o), "sr25519 public key");
        return pk;
    }

    /// <summary>sr25519 signature with the Substrate signing context. Signatures are randomized.</summary>
    public static byte[] Sr25519Sign(ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> message)
    {
        RequireLength(secretKey, 64, nameof(secretKey));
        RequireNativeFor("sr25519");
        byte[] sig = new byte[64];
        fixed (byte* s = secretKey) fixed (byte* m = message) fixed (byte* o = sig)
            Check(NativeMethods.cn_sr25519_sign(s, m, (nuint)message.Length, o), "sr25519 sign");
        return sig;
    }

    public static bool Sr25519Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        RequireNativeFor("sr25519");
        fixed (byte* p = publicKey) fixed (byte* m = message) fixed (byte* s = signature)
            return CheckBool(NativeMethods.cn_sr25519_verify(p, m, (nuint)message.Length, s), "sr25519 verify") == 1;
    }

    #endregion

    #region KDF

    /// <summary>BIP-39 seed (PBKDF2-HMAC-SHA512, 2048 rounds). Inputs are NFKD-normalized as the spec requires.</summary>
    public static byte[] Bip39MnemonicToSeed(string mnemonic, string? passphrase = "")
    {
        ArgumentNullException.ThrowIfNull(mnemonic);
        byte[] m = Encoding.UTF8.GetBytes(mnemonic.Normalize(NormalizationForm.FormKD));
        byte[] p = Encoding.UTF8.GetBytes(("mnemonic" + (passphrase ?? "")).Normalize(NormalizationForm.FormKD));
        try
        {
            if (!UseRustForSha2)
                return Rfc2898DeriveBytes.Pbkdf2(m, p, 2048, HashAlgorithmName.SHA512, 64);

            // Native API prepends "mnemonic" itself.
            byte[] seed = new byte[64];
            fixed (byte* pm = m) fixed (byte* pp = p) fixed (byte* o = seed)
                Check(NativeMethods.cn_bip39_mnemonic_to_seed(pm, (nuint)m.Length, pp + 8, (nuint)(p.Length - 8), o), "BIP-39 seed");
            return seed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(m);
            CryptographicOperations.ZeroMemory(p);
        }
    }

    public static byte[] HmacSha512(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (!UseRustForSha2) return HMACSHA512.HashData(key, data);
        byte[] output = new byte[64];
        fixed (byte* k = key) fixed (byte* d = data) fixed (byte* o = output)
            Check(NativeMethods.cn_hmac_sha512(k, (nuint)key.Length, d, (nuint)data.Length, o), "HMAC-SHA512");
        return output;
    }

    public static byte[] Pbkdf2Sha256(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int outputLength) =>
        Pbkdf2(0, password, salt, iterations, outputLength);

    public static byte[] Pbkdf2Sha512(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int outputLength) =>
        Pbkdf2(1, password, salt, iterations, outputLength);

    private static byte[] Pbkdf2(int prf, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int outputLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputLength, 1);
        if (!UseRustForSha2)
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, prf == 0 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA512, outputLength);

        byte[] output = new byte[outputLength];
        fixed (byte* p = password) fixed (byte* s = salt) fixed (byte* o = output)
            Check(NativeMethods.cn_pbkdf2(prf, p, (nuint)password.Length, s, (nuint)salt.Length, (uint)iterations, o, (nuint)output.Length), "PBKDF2");
        return output;
    }

    /// <summary>scrypt (RFC 7914) with N = 2^<paramref name="logN"/>.</summary>
    public static byte[] Scrypt(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int logN, int r, int p, int outputLength)
    {
        if (logN is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(logN));
        ArgumentOutOfRangeException.ThrowIfLessThan(r, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(p, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputLength, 1);

        byte[] output = new byte[outputLength];
        // RFC 7914 requires N < 2^(16r) and the Rust crate enforces it, but geth-era keystores
        // (e.g. N=2^18, r=1) violate it; the managed implementation accepts them.
        if (!IsNativeActive || logN >= 16 * r)
        {
            Managed.Scrypt.Derive(password, salt, logN, r, p, output);
            return output;
        }
        fixed (byte* pw = password) fixed (byte* s = salt) fixed (byte* o = output)
            Check(NativeMethods.cn_scrypt(pw, (nuint)password.Length, s, (nuint)salt.Length, (byte)logN, (uint)r, (uint)p, o, (nuint)output.Length), "scrypt");
        return output;
    }

    #endregion

    #region Codecs

    private delegate int VarEncoder(byte* output, nuint cap, nuint* written);

    private static byte[] CallVariable(VarEncoder call, int initialCapacity, string operation)
    {
        byte[] buffer = new byte[Math.Max(initialCapacity, 1)];
        nuint written = 0;
        int rc;
        fixed (byte* o = buffer) rc = call(o, (nuint)buffer.Length, &written);
        if (rc == NativeMethods.CN_ERR_BUFFER_TOO_SMALL)
        {
            buffer = new byte[(int)written];
            fixed (byte* o = buffer) rc = call(o, (nuint)buffer.Length, &written);
        }
        Check(rc, operation);
        return written == (nuint)buffer.Length ? buffer : buffer.AsSpan(0, (int)written).ToArray();
    }

    public static string Base58Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        if (!IsNativeActive) return Base58Managed.Encode(data);
        fixed (byte* d = data)
        {
            byte* dp = d;
            int len = data.Length;
            return Encoding.ASCII.GetString(CallVariable((o, c, w) => NativeMethods.cn_base58_encode(dp, (nuint)len, o, c, w), len * 2 + 8, "Base58 encode"));
        }
    }

    public static byte[] Base58Decode(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length == 0) return [];
        if (!IsNativeActive) return Base58Managed.Decode(s);
        byte[] input = Encoding.UTF8.GetBytes(s);
        fixed (byte* i = input)
        {
            byte* ip = i;
            return CallVariable((o, c, w) => NativeMethods.cn_base58_decode(ip, (nuint)input.Length, o, c, w), input.Length, "Base58 decode");
        }
    }

    public static string Base58CheckEncode(ReadOnlySpan<byte> data)
    {
        if (!IsNativeActive) return Base58Managed.EncodeCheck(data);
        fixed (byte* d = data)
        {
            byte* dp = d;
            int len = data.Length;
            return Encoding.ASCII.GetString(CallVariable((o, c, w) => NativeMethods.cn_base58check_encode(dp, (nuint)len, o, c, w), len * 2 + 16, "Base58Check encode"));
        }
    }

    /// <summary>Decodes Base58Check and verifies the 4-byte double-SHA256 checksum.</summary>
    public static byte[] Base58CheckDecode(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!IsNativeActive) return Base58Managed.DecodeCheck(s);
        byte[] input = Encoding.UTF8.GetBytes(s);
        fixed (byte* i = input)
        {
            byte* ip = i;
            return CallVariable((o, c, w) => NativeMethods.cn_base58check_decode(ip, (nuint)input.Length, o, c, w), input.Length, "Base58Check decode");
        }
    }

    /// <summary>Bech32/Bech32m encoding of 8-bit data (e.g. Cosmos addresses).</summary>
    public static string Bech32Encode(string hrp, ReadOnlySpan<byte> data, bool isBech32m = false)
    {
        ArgumentNullException.ThrowIfNull(hrp);
        if (!IsNativeActive) return Bech32Managed.Encode(hrp, data, isBech32m);
        byte[] h = Encoding.UTF8.GetBytes(hrp);
        fixed (byte* hp0 = h) fixed (byte* d = data)
        {
            byte* hp = hp0, dp = d;
            int len = data.Length;
            return Encoding.ASCII.GetString(CallVariable((o, c, w) => NativeMethods.cn_bech32_encode(hp, (nuint)h.Length, dp, (nuint)len, isBech32m ? 1 : 0, o, c, w),
                h.Length + len * 2 + 8, "Bech32 encode"));
        }
    }

    public static (string Hrp, byte[] Data, bool IsBech32m) Bech32Decode(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!IsNativeActive) return Bech32Managed.Decode(s);
        byte[] input = Encoding.UTF8.GetBytes(s);
        byte[] hrp = new byte[input.Length];
        byte[] data = new byte[input.Length];
        nuint hrpLen = 0, dataLen = 0;
        int variant = 0;
        fixed (byte* i = input) fixed (byte* hp = hrp) fixed (byte* dp = data)
            Check(NativeMethods.cn_bech32_decode(i, (nuint)input.Length, hp, (nuint)hrp.Length, &hrpLen, dp, (nuint)data.Length, &dataLen, &variant), "Bech32 decode");
        return (Encoding.ASCII.GetString(hrp, 0, (int)hrpLen), data[..(int)dataLen], variant == 1);
    }

    /// <summary>Encodes a SegWit address (BIP-173 for v0, BIP-350 Bech32m for v1+).</summary>
    public static string SegwitEncode(string hrp, byte witnessVersion, ReadOnlySpan<byte> program)
    {
        ArgumentNullException.ThrowIfNull(hrp);
        if (!IsNativeActive) return Bech32Managed.SegwitEncode(hrp, witnessVersion, program);
        byte[] h = Encoding.UTF8.GetBytes(hrp);
        fixed (byte* hp0 = h) fixed (byte* p = program)
        {
            byte* hp = hp0, pp = p;
            int len = program.Length;
            return Encoding.ASCII.GetString(CallVariable((o, c, w) => NativeMethods.cn_segwit_encode(hp, (nuint)h.Length, witnessVersion, pp, (nuint)len, o, c, w),
                h.Length + len * 2 + 8, "SegWit encode"));
        }
    }

    public static (string Hrp, byte Version, byte[] Program) SegwitDecode(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsNativeActive) return Bech32Managed.SegwitDecode(address);
        if (address.Length > 90) throw new FormatException("SegWit address too long");
        byte[] input = Encoding.UTF8.GetBytes(address);
        byte[] hrp = new byte[input.Length];
        byte[] program = new byte[64];
        nuint hrpLen = 0, progLen = 0;
        byte version = 0;
        fixed (byte* i = input) fixed (byte* hp = hrp) fixed (byte* pp = program)
            Check(NativeMethods.cn_segwit_decode(i, (nuint)input.Length, hp, (nuint)hrp.Length, &hrpLen, &version, pp, (nuint)program.Length, &progLen), "SegWit decode");
        return (Encoding.ASCII.GetString(hrp, 0, (int)hrpLen), version, program[..(int)progLen]);
    }

    #endregion
}
