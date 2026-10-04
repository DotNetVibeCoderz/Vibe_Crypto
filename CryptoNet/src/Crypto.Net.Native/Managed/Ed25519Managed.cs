using System.Numerics;
using System.Security.Cryptography;

namespace Crypto.Net.Native.Managed;

/// <summary>
/// Portable Ed25519 (RFC 8032) used when the native library is unavailable. Not constant-time.
/// </summary>
internal static class Ed25519Managed
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point B = InitBase();

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static BigInteger Mod(BigInteger a)
    {
        var r = a % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger Inv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    private static Point InitBase()
    {
        BigInteger y = Mod(4 * Inv(5));
        BigInteger x = RecoverX(y, false) ?? throw new InvalidOperationException();
        return new Point(x, y, 1, Mod(x * y));
    }

    private static BigInteger? RecoverX(BigInteger y, bool sign)
    {
        if (y >= P) return null;
        BigInteger x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
        if (x2.IsZero) return sign ? null : BigInteger.Zero;
        BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = Mod(x * SqrtM1);
        if (Mod(x * x - x2) != 0) return null;
        if (!x.IsEven != sign) x = P - x;
        return x;
    }

    private static Point Add(Point p, Point q)
    {
        BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
        BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
        BigInteger c = Mod(2 * p.T * q.T * D);
        BigInteger d = Mod(2 * p.Z * q.Z);
        BigInteger e = b - a, f = d - c, g = d + c, h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(Point p, BigInteger s)
    {
        Point q = new(0, 1, 1, 0);
        while (s > 0)
        {
            if (!s.IsEven) q = Add(q, p);
            p = Add(p, p);
            s >>= 1;
        }
        return q;
    }

    private static byte[] Encode(Point p)
    {
        BigInteger zi = Inv(p.Z);
        BigInteger x = Mod(p.X * zi);
        BigInteger y = Mod(p.Y * zi);
        byte[] o = new byte[32];
        y.TryWriteBytes(o, out _, isUnsigned: true, isBigEndian: false);
        if (!x.IsEven) o[31] |= 0x80;
        return o;
    }

    private static Point? Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length != 32) return null;
        Span<byte> tmp = stackalloc byte[32];
        s.CopyTo(tmp);
        bool sign = (tmp[31] & 0x80) != 0;
        tmp[31] &= 0x7F;
        BigInteger y = new(tmp, isUnsigned: true, isBigEndian: false);
        BigInteger? x = RecoverX(y, sign);
        if (x is null) return null;
        return new Point(x.Value, y, 1, Mod(x.Value * y));
    }

    private static BigInteger HashModL(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var part in parts) h.AppendData(part);
        return new BigInteger(h.GetHashAndReset(), isUnsigned: true, isBigEndian: false) % L;
    }

    private static (BigInteger A, byte[] Prefix) Expand(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 32) throw new ArgumentException("Secret key must be 32 bytes");
        byte[] h = SHA512.HashData(secret);
        try
        {
            h[0] &= 248;
            h[31] &= 127;
            h[31] |= 64;
            BigInteger a = new(h.AsSpan(0, 32), isUnsigned: true, isBigEndian: false);
            return (a, h.AsSpan(32, 32).ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(h);
        }
    }

    public static byte[] GetPublicKey(ReadOnlySpan<byte> secret)
    {
        var (a, prefix) = Expand(secret);
        CryptographicOperations.ZeroMemory(prefix);
        return Encode(Multiply(B, a));
    }

    public static byte[] Sign(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> message)
    {
        var (a, prefix) = Expand(secret);
        byte[] pk = Encode(Multiply(B, a));
        byte[] msg = message.ToArray();
        BigInteger r = HashModL(prefix, msg);
        byte[] rEnc = Encode(Multiply(B, r));
        BigInteger k = HashModL(rEnc, pk, msg);
        BigInteger s = (r + k * a) % L;

        byte[] sig = new byte[64];
        rEnc.CopyTo(sig, 0);
        s.TryWriteBytes(sig.AsSpan(32), out _, isUnsigned: true, isBigEndian: false);
        CryptographicOperations.ZeroMemory(prefix);
        return sig;
    }

    public static bool IsOnCurve(ReadOnlySpan<byte> point) => Decode(point) is not null;

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        Point? a = Decode(publicKey);
        Point? r = Decode(signature[..32]);
        if (a is null || r is null) return false;
        BigInteger s = new(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= L) return false;

        BigInteger k = HashModL(signature[..32].ToArray(), publicKey.ToArray(), message.ToArray());
        byte[] lhs = Encode(Multiply(B, s));
        byte[] rhs = Encode(Add(r.Value, Multiply(a.Value, k)));
        return CryptographicOperations.FixedTimeEquals(lhs, rhs);
    }
}
