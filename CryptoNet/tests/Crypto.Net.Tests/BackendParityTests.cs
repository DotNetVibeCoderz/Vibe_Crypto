using System.Security.Cryptography;
using System.Text;
using Crypto.Net.Native;
using Xunit;

namespace Crypto.Net.Tests;

/// <summary>Tests that switch <see cref="CryptoNative.Backend"/> must not run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BackendCollection
{
    public const string Name = "Backend switching";
}

internal static class Backends
{
    public static T With<T>(CryptoBackend backend, Func<T> action)
    {
        using (CryptoNative.UseBackend(backend))
            return action();
    }

    public static void AssertSame<T>(Func<T> action)
    {
        T native = With(CryptoBackend.Native, action);
        T managed = With(CryptoBackend.Managed, action);
        Assert.Equal(native, managed);
    }

    public static string Hex(byte[] b) => Convert.ToHexStringLower(b);
}

[Collection(BackendCollection.Name)]
public class BackendParityTests
{
    private static readonly int[] Lengths = [0, 1, 31, 32, 55, 56, 63, 64, 111, 127, 128, 129, 135, 136, 137, 200, 1000];

    private static byte[] Data(int length, int seed = 1)
    {
        var b = new byte[length];
        new Random(seed + length).NextBytes(b);
        return b;
    }

    private static byte[] ValidKey(int seed)
    {
        var k = Data(32, seed);
        k[0] &= 0x7f; // keep below the curve order
        k[31] |= 1;
        return k;
    }

    [Fact]
    public void NativeLibrary_IsLoaded()
    {
        Assert.True(NativeLoader.IsNativeAvailable, NativeLoader.LoadError);
        Assert.True(NativeLoader.AbiVersion >= NativeLoader.RequiredAbiVersion);
    }

    [Fact]
    public void Hashes_MatchAcrossBackends()
    {
        foreach (int len in Lengths)
        {
            var d = Data(len);
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Sha256(d)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.DoubleSha256(d)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Sha512(d)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Keccak256(d)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Ripemd160(d)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Hash160(d)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Blake2b256(d)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Blake2b512(d)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Blake2b128(d)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.TaggedHash("TapTweak", d)));
        }
    }

    [Theory]
    [InlineData("", "9c1185a5c5e9fc54612808977ee8f548b2258d31")]
    [InlineData("abc", "8eb208f7e05d987a9b044a8e98c6b087f15a0bfc")]
    [InlineData("message digest", "5d0689ef49d2fae572b881b123a85ffa21595f36")]
    public void Ripemd160_OfficialVectors_BothBackends(string input, string expected)
    {
        foreach (var backend in new[] { CryptoBackend.Native, CryptoBackend.Managed })
            Assert.Equal(expected, Backends.With(backend, () => Backends.Hex(CryptoNative.Ripemd160(Encoding.ASCII.GetBytes(input)))));
    }

    [Fact]
    public void Secp256k1_MatchesAcrossBackends()
    {
        for (int i = 0; i < 12; i++)
        {
            var key = ValidKey(i);
            var digest = Data(32, 100 + i);
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Secp256k1GetPublicKey(key, true)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Secp256k1GetPublicKey(key, false)));
            Backends.AssertSame(() =>
            {
                var (sig, rec) = CryptoNative.Secp256k1SignRecoverable(key, digest);
                return Backends.Hex(sig) + rec;
            });

            var (s, r) = CryptoNative.Secp256k1SignRecoverable(key, digest);
            var pub = CryptoNative.Secp256k1GetPublicKey(key, false);
            Backends.AssertSame(() => CryptoNative.Secp256k1Verify(pub, digest, s));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Secp256k1RecoverPublicKey(digest, s, r, compressed: true)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Secp256k1ConvertPublicKey(pub, compressed: true)));

            var tampered = (byte[])s.Clone();
            tampered[5] ^= 1;
            Backends.AssertSame(() => CryptoNative.Secp256k1Verify(pub, digest, tampered));

            var aux = Data(32, 200 + i);
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Secp256k1XOnlyPublicKey(key)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.SchnorrSign(key, digest, aux)));
            var schnorr = CryptoNative.SchnorrSign(key, digest, aux);
            var xonly = CryptoNative.Secp256k1XOnlyPublicKey(key);
            Backends.AssertSame(() => CryptoNative.SchnorrVerify(xonly, digest, schnorr));
            Backends.AssertSame(() => CryptoNative.SchnorrVerify(xonly, digest, tampered));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.TaprootTweakPublicKey(xonly).OutputKey));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.TaprootTweakSecretKey(key)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.TaprootTweakPublicKey(xonly, digest).OutputKey));

            var cc = Data(32, 300 + i);
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Bip32CkdPriv(key, cc, (uint)i).Key));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Bip32CkdPriv(key, cc, 0x80000000u + (uint)i).ChainCode));
        }
    }

    [Fact]
    public void Secp256k1_InvalidKeys_RejectedByBothBackends()
    {
        byte[] zero = new byte[32];
        byte[] order = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");
        foreach (var backend in new[] { CryptoBackend.Native, CryptoBackend.Managed })
        {
            Assert.Throws<CryptographicException>(() => Backends.With(backend, () => CryptoNative.Secp256k1GetPublicKey(zero)));
            Assert.Throws<CryptographicException>(() => Backends.With(backend, () => CryptoNative.Secp256k1GetPublicKey(order)));
        }
    }

    [Fact]
    public void Ed25519_MatchesAcrossBackends()
    {
        for (int i = 0; i < 8; i++)
        {
            var key = Data(32, 400 + i);
            var msg = Data(i * 37, 500 + i);
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Ed25519GetPublicKey(key)));
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Ed25519Sign(key, msg)));
            var pk = CryptoNative.Ed25519GetPublicKey(key);
            var sig = CryptoNative.Ed25519Sign(key, msg);
            Backends.AssertSame(() => CryptoNative.Ed25519Verify(pk, msg, sig));
            sig[10] ^= 4;
            Backends.AssertSame(() => CryptoNative.Ed25519Verify(pk, msg, sig));
            var cc = Data(32, 600 + i);
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Slip10Ed25519CkdPriv(key, cc, (uint)i).Key));
        }
    }

    [Fact]
    public void Kdf_MatchesAcrossBackends()
    {
        Backends.AssertSame(() => Backends.Hex(CryptoNative.Bip39MnemonicToSeed("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about", "TREZOR")));
        Backends.AssertSame(() => Backends.Hex(CryptoNative.HmacSha512(Data(17), Data(300))));
        Backends.AssertSame(() => Backends.Hex(CryptoNative.Pbkdf2Sha256("pw"u8, "salt"u8, 1000, 48)));
        Backends.AssertSame(() => Backends.Hex(CryptoNative.Pbkdf2Sha512("pw"u8, "salt"u8, 10, 80)));
        Backends.AssertSame(() => Backends.Hex(CryptoNative.Scrypt("password"u8, "NaCl"u8, 10, 8, 16, 64)));
        Backends.AssertSame(() => Backends.Hex(CryptoNative.SubstrateEd25519Derive(Data(32), "//polkadot//0//a-very-long-junction-name-beyond-32-bytes")));
    }

    [Fact]
    public void Scrypt_Rfc7914Vector()
    {
        const string expected = "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640";
        Assert.Equal(expected, Backends.With(CryptoBackend.Managed, () => Backends.Hex(CryptoNative.Scrypt("password"u8, "NaCl"u8, 10, 8, 16, 64))));
    }

    [Fact]
    public void Codecs_MatchAcrossBackends()
    {
        foreach (int len in new[] { 1, 20, 21, 25, 32, 33, 64 })
        {
            var d = Data(len, 700);
            d[0] = 0; // leading zero handling
            Backends.AssertSame(() => CryptoNative.Base58Encode(d));
            string b58 = CryptoNative.Base58Encode(d);
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Base58Decode(b58)));
            Backends.AssertSame(() => CryptoNative.Base58CheckEncode(d));
            string chk = CryptoNative.Base58CheckEncode(d);
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Base58CheckDecode(chk)));
            Backends.AssertSame(() => CryptoNative.Bech32Encode("cosmos", d));
            Backends.AssertSame(() => CryptoNative.Bech32Encode("tb", d, isBech32m: true));
            string b32 = CryptoNative.Bech32Encode("osmo", d);
            Backends.AssertSame(() => Backends.Hex(CryptoNative.Bech32Decode(b32).Data));
        }

        foreach (var (version, size) in new (byte, int)[] { (0, 20), (0, 32), (1, 32), (2, 40) })
        {
            var program = Data(size, 800);
            Backends.AssertSame(() => CryptoNative.SegwitEncode("bc", version, program));
            string addr = CryptoNative.SegwitEncode("tb", version, program);
            Backends.AssertSame(() =>
            {
                var r = CryptoNative.SegwitDecode(addr);
                return $"{r.Hrp}:{r.Version}:{Backends.Hex(r.Program)}";
            });
        }
    }

    [Fact]
    public void Codecs_RejectInvalidInputOnBothBackends()
    {
        foreach (var backend in new[] { CryptoBackend.Native, CryptoBackend.Managed })
        {
            Assert.ThrowsAny<FormatException>(() => Backends.With(backend, () => CryptoNative.Base58Decode("0OIl")));
            Assert.ThrowsAny<FormatException>(() => Backends.With(backend, () => CryptoNative.Base58CheckDecode("1BgGZ9tcN4rm9KBzDn7KprQz87SZ26SAMJ")));
            Assert.ThrowsAny<FormatException>(() => Backends.With(backend, () => CryptoNative.SegwitDecode("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5")));
            // v1 program with a Bech32 (not Bech32m) checksum
            string wrongVariant = CryptoNative.Bech32Encode("bc", [], false);
            Assert.ThrowsAny<FormatException>(() => Backends.With(backend, () => CryptoNative.SegwitDecode(wrongVariant)));
        }
    }

    [Fact]
    public void ManagedBackend_Sr25519_ThrowsPlatformNotSupported()
    {
        Assert.Throws<PlatformNotSupportedException>(() => Backends.With(CryptoBackend.Managed, () => CryptoNative.Sr25519FromSeed(new byte[32])));
    }
}
