using System.Numerics;
using System.Security.Cryptography;

namespace Crypto.Net.Native.Managed;

/// <summary>
/// Portable secp256k1 implementation used when the native library is unavailable.
/// Produces byte-identical results to the Rust core (RFC 6979 + low-S ECDSA, BIP-340, BIP-341, BIP-32).
/// It is NOT constant-time; prefer the native backend for production signing.
/// </summary>
internal static class Secp256k1Managed
{
    internal static readonly BigInteger P = BigInteger.Pow(2, 256) - BigInteger.Pow(2, 32) - 977;
    internal static readonly BigInteger N = Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");
    private static readonly BigInteger HalfN = N >> 1;
    private static readonly BigInteger Gx = Hex("79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798");
    private static readonly BigInteger Gy = Hex("483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8");
    private static readonly Jacobian G = new(Gx, Gy, BigInteger.One);

    private static BigInteger Hex(string h) => BigInteger.Parse("0" + h, System.Globalization.NumberStyles.HexNumber);

    internal static BigInteger ToInt(ReadOnlySpan<byte> be) => new(be, isUnsigned: true, isBigEndian: true);

    internal static void ToBytes32(BigInteger v, Span<byte> dest)
    {
        dest[..32].Clear();
        int count = v.GetByteCount(isUnsigned: true);
        v.TryWriteBytes(dest.Slice(32 - count, count), out _, isUnsigned: true, isBigEndian: true);
    }

    internal static byte[] ToBytes32(BigInteger v)
    {
        var b = new byte[32];
        ToBytes32(v, b);
        return b;
    }

    private static BigInteger Mod(BigInteger a, BigInteger m)
    {
        var r = a % m;
        return r.Sign < 0 ? r + m : r;
    }

    private static BigInteger Inv(BigInteger a, BigInteger m) => BigInteger.ModPow(Mod(a, m), m - 2, m);

    // ------------------------------------------------------------------ point arithmetic

    private readonly record struct Jacobian(BigInteger X, BigInteger Y, BigInteger Z)
    {
        public bool IsInfinity => Z.IsZero;
    }

    private static readonly Jacobian Infinity = new(BigInteger.One, BigInteger.One, BigInteger.Zero);

    private static Jacobian Double(Jacobian p)
    {
        if (p.IsInfinity || p.Y.IsZero) return Infinity;
        BigInteger ysq = Mod(p.Y * p.Y, P);
        BigInteger s = Mod(4 * p.X * ysq, P);
        BigInteger m = Mod(3 * p.X * p.X, P);
        BigInteger nx = Mod(m * m - 2 * s, P);
        BigInteger ny = Mod(m * (s - nx) - 8 * ysq * ysq, P);
        BigInteger nz = Mod(2 * p.Y * p.Z, P);
        return new Jacobian(nx, ny, nz);
    }

    private static Jacobian Add(Jacobian p, Jacobian q)
    {
        if (p.IsInfinity) return q;
        if (q.IsInfinity) return p;
        BigInteger z1z1 = Mod(p.Z * p.Z, P);
        BigInteger z2z2 = Mod(q.Z * q.Z, P);
        BigInteger u1 = Mod(p.X * z2z2, P);
        BigInteger u2 = Mod(q.X * z1z1, P);
        BigInteger s1 = Mod(p.Y * q.Z * z2z2, P);
        BigInteger s2 = Mod(q.Y * p.Z * z1z1, P);
        if (u1 == u2)
            return s1 == s2 ? Double(p) : Infinity;
        BigInteger h = Mod(u2 - u1, P);
        BigInteger r = Mod(s2 - s1, P);
        BigInteger h2 = Mod(h * h, P);
        BigInteger h3 = Mod(h * h2, P);
        BigInteger u1h2 = Mod(u1 * h2, P);
        BigInteger nx = Mod(r * r - h3 - 2 * u1h2, P);
        BigInteger ny = Mod(r * (u1h2 - nx) - s1 * h3, P);
        BigInteger nz = Mod(h * p.Z * q.Z, P);
        return new Jacobian(nx, ny, nz);
    }

    private static Jacobian Multiply(Jacobian p, BigInteger k)
    {
        Jacobian result = Infinity;
        Jacobian addend = p;
        while (!k.IsZero)
        {
            if (!k.IsEven) result = Add(result, addend);
            addend = Double(addend);
            k >>= 1;
        }
        return result;
    }

    private static (BigInteger X, BigInteger Y) ToAffine(Jacobian p)
    {
        if (p.IsInfinity) throw new CryptographicException("Point at infinity");
        BigInteger zi = Inv(p.Z, P);
        BigInteger zi2 = Mod(zi * zi, P);
        return (Mod(p.X * zi2, P), Mod(p.Y * zi2 * zi, P));
    }

    private static Jacobian FromAffine(BigInteger x, BigInteger y) => new(x, y, BigInteger.One);

    private static BigInteger? LiftY(BigInteger x, bool odd)
    {
        if (x.Sign < 0 || x >= P) return null;
        BigInteger c = Mod(BigInteger.ModPow(x, 3, P) + 7, P);
        BigInteger y = BigInteger.ModPow(c, (P + 1) / 4, P);
        if (Mod(y * y, P) != c) return null;
        if (y.IsEven == odd) y = P - y;
        return y;
    }

    private static Jacobian DecodePoint(ReadOnlySpan<byte> pub)
    {
        if (pub.Length == 33 && (pub[0] == 2 || pub[0] == 3))
        {
            BigInteger x = ToInt(pub[1..]);
            BigInteger? y = LiftY(x, pub[0] == 3) ?? throw new CryptographicException("Invalid secp256k1 public key");
            return FromAffine(x, y.Value);
        }
        if (pub.Length == 65 && pub[0] == 4)
        {
            BigInteger x = ToInt(pub[1..33]);
            BigInteger y = ToInt(pub[33..]);
            if (x >= P || y >= P || Mod(y * y - BigInteger.ModPow(x, 3, P) - 7, P) != 0)
                throw new CryptographicException("Invalid secp256k1 public key");
            return FromAffine(x, y);
        }
        throw new CryptographicException("Invalid secp256k1 public key encoding");
    }

    private static byte[] EncodePoint(Jacobian p, bool compressed)
    {
        var (x, y) = ToAffine(p);
        if (compressed)
        {
            var o = new byte[33];
            o[0] = (byte)(y.IsEven ? 2 : 3);
            ToBytes32(x, o.AsSpan(1));
            return o;
        }
        var u = new byte[65];
        u[0] = 4;
        ToBytes32(x, u.AsSpan(1));
        ToBytes32(y, u.AsSpan(33));
        return u;
    }

    private static BigInteger SecretScalar(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 32) throw new ArgumentException("Secret key must be 32 bytes");
        BigInteger d = ToInt(secret);
        if (d.IsZero || d >= N) throw new CryptographicException("Invalid secp256k1 secret key");
        return d;
    }

    // ------------------------------------------------------------------ public API

    public static byte[] GetPublicKey(ReadOnlySpan<byte> secret, bool compressed) =>
        EncodePoint(Multiply(G, SecretScalar(secret)), compressed);

    public static byte[] ConvertPublicKey(ReadOnlySpan<byte> pub, bool compressed) =>
        EncodePoint(DecodePoint(pub), compressed);

    public static (byte[] Signature, byte RecoveryId) SignRecoverable(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> digest)
    {
        if (digest.Length != 32) throw new ArgumentException("Digest must be 32 bytes");
        BigInteger d = SecretScalar(secret);
        BigInteger z = Mod(ToInt(digest), N);

        foreach (BigInteger k in Rfc6979(secret, digest))
        {
            var (rx, ry) = ToAffine(Multiply(G, k));
            BigInteger r = Mod(rx, N);
            if (r.IsZero) continue;
            BigInteger s = Mod(Inv(k, N) * (z + r * d), N);
            if (s.IsZero) continue;

            byte recId = (byte)((ry.IsEven ? 0 : 1) | (rx >= N ? 2 : 0));
            if (s > HalfN)
            {
                s = N - s;
                recId ^= 1;
            }

            var sig = new byte[64];
            ToBytes32(r, sig.AsSpan(0, 32));
            ToBytes32(s, sig.AsSpan(32, 32));
            return (sig, recId);
        }
        throw new CryptographicException("ECDSA signing failed");
    }

    private static IEnumerable<BigInteger> Rfc6979(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> digest)
    {
        byte[] x = secret.ToArray();
        byte[] h1 = ToBytes32(Mod(ToInt(digest), N));
        return Rfc6979Iterator(x, h1);
    }

    private static IEnumerable<BigInteger> Rfc6979Iterator(byte[] x, byte[] h1)
    {
        byte[] v = Enumerable.Repeat((byte)0x01, 32).ToArray();
        byte[] k = new byte[32];
        try
        {
            k = HMACSHA256.HashData(k, (byte[])[.. v, 0x00, .. x, .. h1]);
            v = HMACSHA256.HashData(k, v);
            k = HMACSHA256.HashData(k, (byte[])[.. v, 0x01, .. x, .. h1]);
            v = HMACSHA256.HashData(k, v);
            while (true)
            {
                v = HMACSHA256.HashData(k, v);
                BigInteger candidate = ToInt(v);
                if (!candidate.IsZero && candidate < N)
                    yield return candidate;
                k = HMACSHA256.HashData(k, (byte[])[.. v, 0x00]);
                v = HMACSHA256.HashData(k, v);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x);
            CryptographicOperations.ZeroMemory(k);
        }
    }

    public static bool Verify(ReadOnlySpan<byte> pub, ReadOnlySpan<byte> digest, ReadOnlySpan<byte> sig)
    {
        if (digest.Length != 32 || sig.Length != 64) return false;
        Jacobian q;
        try { q = DecodePoint(pub); } catch (CryptographicException) { return false; }

        BigInteger r = ToInt(sig[..32]);
        BigInteger s = ToInt(sig[32..]);
        if (r.IsZero || r >= N || s.IsZero || s > HalfN) return false;

        BigInteger z = Mod(ToInt(digest), N);
        BigInteger w = Inv(s, N);
        Jacobian pt = Add(Multiply(G, Mod(z * w, N)), Multiply(q, Mod(r * w, N)));
        if (pt.IsInfinity) return false;
        return Mod(ToAffine(pt).X, N) == r;
    }

    public static byte[] Recover(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> sig, byte recId, bool compressed)
    {
        if (digest.Length != 32 || sig.Length != 64 || recId > 3) throw new ArgumentException("Invalid digest, signature or recovery id");
        BigInteger r = ToInt(sig[..32]);
        BigInteger s = ToInt(sig[32..]);
        if (r.IsZero || r >= N || s.IsZero || s >= N) throw new CryptographicException("Invalid signature");

        BigInteger x = (recId & 2) != 0 ? r + N : r;
        BigInteger? y = LiftY(x, (recId & 1) != 0) ?? throw new CryptographicException("Failed to recover public key");
        Jacobian rPoint = FromAffine(x, y.Value);
        BigInteger z = Mod(ToInt(digest), N);
        BigInteger rInv = Inv(r, N);
        Jacobian q = Add(Multiply(rPoint, Mod(s * rInv, N)), Multiply(G, Mod(-z * rInv, N)));
        if (q.IsInfinity) throw new CryptographicException("Failed to recover public key");
        return EncodePoint(q, compressed);
    }

    // ------------------------------------------------------------------ BIP-340 / BIP-341

    private static byte[] TaggedHash(string tag, ReadOnlySpan<byte> msg)
    {
        byte[] th = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag));
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(th);
        h.AppendData(th);
        h.AppendData(msg);
        return h.GetHashAndReset();
    }

    public static byte[] XOnlyPublicKey(ReadOnlySpan<byte> secret)
    {
        var (x, _) = ToAffine(Multiply(G, SecretScalar(secret)));
        return ToBytes32(x);
    }

    public static byte[] SchnorrSign(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> msg, ReadOnlySpan<byte> aux)
    {
        BigInteger d0 = SecretScalar(secret);
        var (px, py) = ToAffine(Multiply(G, d0));
        BigInteger d = py.IsEven ? d0 : N - d0;
        byte[] pBytes = ToBytes32(px);

        byte[] dBytes = ToBytes32(d);
        byte[] auxHash = TaggedHash("BIP0340/aux", aux.Length == 32 ? aux : new byte[32]);
        byte[] t = new byte[32];
        for (int i = 0; i < 32; i++) t[i] = (byte)(dBytes[i] ^ auxHash[i]);

        byte[] rand = TaggedHash("BIP0340/nonce", [.. t, .. pBytes, .. msg]);
        BigInteger k0 = Mod(ToInt(rand), N);
        if (k0.IsZero) throw new CryptographicException("Schnorr nonce is zero");
        var (rx, ry) = ToAffine(Multiply(G, k0));
        BigInteger k = ry.IsEven ? k0 : N - k0;
        byte[] rBytes = ToBytes32(rx);

        BigInteger e = Mod(ToInt(TaggedHash("BIP0340/challenge", [.. rBytes, .. pBytes, .. msg])), N);
        var sig = new byte[64];
        rBytes.CopyTo(sig, 0);
        ToBytes32(Mod(k + e * d, N), sig.AsSpan(32));

        CryptographicOperations.ZeroMemory(dBytes);
        CryptographicOperations.ZeroMemory(t);
        return sig;
    }

    public static bool SchnorrVerify(ReadOnlySpan<byte> xonly, ReadOnlySpan<byte> msg, ReadOnlySpan<byte> sig)
    {
        if (xonly.Length != 32 || sig.Length != 64) return false;
        BigInteger px = ToInt(xonly);
        BigInteger? py = LiftY(px, odd: false);
        if (py is null) return false;
        BigInteger r = ToInt(sig[..32]);
        BigInteger s = ToInt(sig[32..]);
        if (r >= P || s >= N) return false;

        BigInteger e = Mod(ToInt(TaggedHash("BIP0340/challenge", [.. sig[..32], .. xonly, .. msg])), N);
        Jacobian rPoint = Add(Multiply(G, s), Multiply(FromAffine(px, py.Value), N - e));
        if (rPoint.IsInfinity) return false;
        var (x, y) = ToAffine(rPoint);
        return y.IsEven && x == r;
    }

    private static BigInteger TapTweak(ReadOnlySpan<byte> xonly, ReadOnlySpan<byte> merkleRoot)
    {
        if (!(merkleRoot.IsEmpty || merkleRoot.Length == 32)) throw new ArgumentException("Merkle root must be empty or 32 bytes");
        BigInteger t = ToInt(TaggedHash("TapTweak", [.. xonly, .. merkleRoot]));
        if (t >= N) throw new CryptographicException("Taproot tweak out of range");
        return t;
    }

    public static (byte[] OutputKey, byte Parity) TaprootTweakPublicKey(ReadOnlySpan<byte> internalXOnly, ReadOnlySpan<byte> merkleRoot)
    {
        if (internalXOnly.Length != 32) throw new ArgumentException("x-only key must be 32 bytes");
        BigInteger px = ToInt(internalXOnly);
        BigInteger? py = LiftY(px, odd: false) ?? throw new CryptographicException("Invalid x-only public key");
        BigInteger t = TapTweak(internalXOnly, merkleRoot);
        Jacobian q = Add(FromAffine(px, py.Value), Multiply(G, t));
        var (qx, qy) = ToAffine(q);
        return (ToBytes32(qx), (byte)(qy.IsEven ? 0 : 1));
    }

    public static byte[] TaprootTweakSecretKey(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> merkleRoot)
    {
        BigInteger d = SecretScalar(secret);
        var (px, py) = ToAffine(Multiply(G, d));
        if (!py.IsEven) d = N - d;
        BigInteger t = TapTweak(ToBytes32(px), merkleRoot);
        BigInteger r = Mod(d + t, N);
        if (r.IsZero) throw new CryptographicException("Tweaked key is zero");
        return ToBytes32(r);
    }

    // ------------------------------------------------------------------ BIP-32

    public static (byte[] Key, byte[] ChainCode) Bip32CkdPriv(ReadOnlySpan<byte> parentKey, ReadOnlySpan<byte> chainCode, uint index)
    {
        if (chainCode.Length != 32) throw new ArgumentException("Chain code must be 32 bytes");
        BigInteger kpar = SecretScalar(parentKey);
        byte[] data = new byte[37];
        if ((index & 0x80000000) != 0)
            parentKey.CopyTo(data.AsSpan(1));
        else
            GetPublicKey(parentKey, compressed: true).CopyTo(data, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(33), index);

        byte[] i = HMACSHA512.HashData(chainCode, data);
        CryptographicOperations.ZeroMemory(data);
        try
        {
            BigInteger il = ToInt(i.AsSpan(0, 32));
            if (il >= N) throw new CryptographicException("Derived key invalid (IL >= n); use the next index");
            BigInteger child = Mod(il + kpar, N);
            if (child.IsZero) throw new CryptographicException("Derived key invalid (zero); use the next index");
            return (ToBytes32(child), i.AsSpan(32, 32).ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(i);
        }
    }
}
