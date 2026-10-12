using System.Numerics;

namespace Crypto.Net.Native.Managed;

/// <summary>X25519 (RFC 7748) Montgomery ladder used when the native library is unavailable. Not constant-time.</summary>
internal static class X25519Managed
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private const int A24 = 121665;

    public static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> u)
    {
        Span<byte> k = stackalloc byte[32];
        scalar.CopyTo(k);
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
        Span<byte> uBytes = stackalloc byte[32];
        u.CopyTo(uBytes);
        uBytes[31] &= 127;

        BigInteger x1 = Mod(new BigInteger(uBytes, isUnsigned: true, isBigEndian: false));
        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        int swap = 0;
        for (int t = 254; t >= 0; t--)
        {
            int kt = (k[t / 8] >> (t % 8)) & 1;
            swap ^= kt;
            if (swap == 1)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }
            swap = kt;
            BigInteger a = Mod(x2 + z2), aa = Mod(a * a), b = Mod(x2 - z2), bb = Mod(b * b), e = Mod(aa - bb);
            BigInteger c = Mod(x3 + z3), d = Mod(x3 - z3), da = Mod(d * a), cb = Mod(c * b);
            x3 = Mod((da + cb) * (da + cb));
            z3 = Mod(x1 * Mod((da - cb) * (da - cb)));
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + A24 * e));
        }
        if (swap == 1)
        {
            (x2, x3) = (x3, x2);
            (z2, z3) = (z3, z2);
        }
        BigInteger result = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
        byte[] o = new byte[32];
        result.TryWriteBytes(o, out _, isUnsigned: true, isBigEndian: false);
        return o;
    }

    private static BigInteger Mod(BigInteger a)
    {
        var r = a % P;
        return r.Sign < 0 ? r + P : r;
    }
}
