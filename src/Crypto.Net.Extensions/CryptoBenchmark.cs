using System.Diagnostics;
using Crypto.Net.Native;

namespace Crypto.Net.Extensions;

/// <summary>One benchmark row: operations per second for each backend.</summary>
public sealed record BenchmarkResult(string Name, string Category, int Iterations, double NativeOpsPerSec, double ManagedOpsPerSec)
{
    /// <summary>How many times faster the Rust backend is (0 when native is unavailable).</summary>
    public double Speedup => NativeOpsPerSec > 0 && ManagedOpsPerSec > 0 ? NativeOpsPerSec / ManagedOpsPerSec : 0;
}

/// <summary>Quick micro-benchmark comparing the Rust core with the managed fallback.</summary>
public static class CryptoBenchmark
{
    private static readonly object Gate = new();

    /// <summary>Runs all benchmarks. <paramref name="scale"/> multiplies iteration counts (1.0 ≈ one second total).</summary>
    public static IReadOnlyList<BenchmarkResult> Run(double scale = 1.0, IProgress<string>? progress = null)
    {
        byte[] data = new byte[256];
        Random.Shared.NextBytes(data);
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)(i + 1);
        byte[] digest = CryptoNative.Sha256(data);
        byte[] edPub = CryptoNative.Ed25519GetPublicKey(key);
        byte[] edSig = CryptoNative.Ed25519Sign(key, data);
        byte[] secpPub = CryptoNative.Secp256k1GetPublicKey(key);
        var (secpSig, _) = CryptoNative.Secp256k1SignRecoverable(key, digest);

        int N(int n) => Math.Max(1, (int)(n * scale));
        var cases = new (string Name, string Category, int Iterations, Action Op)[]
        {
            ("Keccak-256 (256 B)", "Hashing", N(20_000), () => CryptoNative.Keccak256(data)),
            ("SHA-256 (256 B)", "Hashing", N(20_000), () => CryptoNative.Sha256(data)),
            ("BLAKE2b-256 (256 B)", "Hashing", N(20_000), () => CryptoNative.Blake2b256(data)),
            ("RIPEMD-160 (256 B)", "Hashing", N(20_000), () => CryptoNative.Ripemd160(data)),
            ("secp256k1 public key", "Keys", N(300), () => CryptoNative.Secp256k1GetPublicKey(key)),
            ("ECDSA sign (RFC 6979)", "Signing", N(300), () => CryptoNative.Secp256k1SignRecoverable(key, digest)),
            ("ECDSA verify", "Signing", N(200), () => CryptoNative.Secp256k1Verify(secpPub, digest, secpSig)),
            ("Schnorr sign (BIP-340)", "Signing", N(200), () => CryptoNative.SchnorrSign(key, digest)),
            ("Ed25519 sign", "Signing", N(200), () => CryptoNative.Ed25519Sign(key, data)),
            ("Ed25519 verify", "Signing", N(100), () => CryptoNative.Ed25519Verify(edPub, data, edSig)),
            ("BIP-32 child derivation", "HD wallet", N(300), () => CryptoNative.Bip32CkdPriv(key, digest, 0x80000000)),
            ("BIP-39 seed (PBKDF2 ×2048)", "HD wallet", N(20), () => CryptoNative.Bip39MnemonicToSeed("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")),
            ("Base58 encode (32 B)", "Encoding", N(20_000), () => CryptoNative.Base58Encode(digest)),
            ("Bech32 encode (20 B)", "Encoding", N(20_000), () => CryptoNative.Bech32Encode("cosmos", digest.AsSpan(0, 20))),
        };

        var results = new List<BenchmarkResult>(cases.Length);
        lock (Gate)
        {
            foreach (var c in cases)
            {
                progress?.Report(c.Name);
                double native = 0;
                if (NativeLoader.IsNativeAvailable)
                {
                    using (CryptoNative.UseBackend(CryptoBackend.Native))
                        native = Measure(c.Op, c.Iterations);
                }
                double managed;
                using (CryptoNative.UseBackend(CryptoBackend.Managed))
                    managed = Measure(c.Op, c.Iterations);
                results.Add(new BenchmarkResult(c.Name, c.Category, c.Iterations, native, managed));
            }
        }
        return results;
    }

    private static double Measure(Action op, int iterations)
    {
        for (int i = 0; i < Math.Min(10, iterations); i++) op(); // warm-up / JIT
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) op();
        sw.Stop();
        return iterations / Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
    }
}
