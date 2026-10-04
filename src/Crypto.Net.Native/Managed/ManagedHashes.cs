using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Crypto.Net.Native.Managed;

/// <summary>Keccak-256 (original Keccak padding, as used by Ethereum).</summary>
internal static class Keccak
{
    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL, 0x8000000080008000UL,
        0x000000000000808bUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008aUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800aUL, 0x800000008000000aUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    ];

    private static readonly int[] Rotations = [0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14];

    public static void Hash256(ReadOnlySpan<byte> input, Span<byte> output)
    {
        const int Rate = 136;
        Span<ulong> state = stackalloc ulong[25];
        state.Clear();

        while (input.Length >= Rate)
        {
            Absorb(state, input[..Rate]);
            Permute(state);
            input = input[Rate..];
        }

        Span<byte> block = stackalloc byte[Rate];
        block.Clear();
        input.CopyTo(block);
        block[input.Length] ^= 0x01;
        block[Rate - 1] ^= 0x80;
        Absorb(state, block);
        Permute(state);

        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(output.Slice(i * 8, 8), state[i]);
    }

    private static void Absorb(Span<ulong> state, ReadOnlySpan<byte> block)
    {
        for (int i = 0; i < block.Length / 8; i++)
            state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));
    }

    private static void Permute(Span<ulong> a)
    {
        Span<ulong> b = stackalloc ulong[25];
        Span<ulong> c = stackalloc ulong[5];

        for (int round = 0; round < 24; round++)
        {
            for (int x = 0; x < 5; x++)
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];

            for (int x = 0; x < 5; x++)
            {
                ulong d = c[(x + 4) % 5] ^ ulong.RotateLeft(c[(x + 1) % 5], 1);
                for (int y = 0; y < 25; y += 5)
                    a[y + x] ^= d;
            }

            // rho + pi: B[y, 2x+3y] = rot(A[x, y], r[x, y])
            for (int x = 0; x < 5; x++)
            {
                for (int y = 0; y < 5; y++)
                {
                    int idx = x + 5 * y;
                    int nx = y;
                    int ny = (2 * x + 3 * y) % 5;
                    b[nx + 5 * ny] = ulong.RotateLeft(a[idx], Rotations[idx]);
                }
            }

            for (int y = 0; y < 25; y += 5)
            {
                for (int x = 0; x < 5; x++)
                    a[y + x] = b[y + x] ^ (~b[y + (x + 1) % 5] & b[y + (x + 2) % 5]);
            }

            a[0] ^= RoundConstants[round];
        }
    }
}

/// <summary>RIPEMD-160.</summary>
internal static class Ripemd160
{
    private static readonly byte[] RL =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
        3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
        1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2,
        4, 0, 5, 9, 7, 12, 2, 10, 14, 1, 3, 8, 11, 6, 15, 13,
    ];

    private static readonly byte[] RR =
    [
        5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
        6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
        15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
        8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14,
        12, 15, 10, 4, 1, 5, 8, 7, 6, 2, 13, 14, 0, 3, 9, 11,
    ];

    private static readonly byte[] SL =
    [
        11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
        7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
        11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
        11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12,
        9, 15, 5, 11, 6, 8, 13, 12, 5, 12, 13, 14, 11, 8, 5, 6,
    ];

    private static readonly byte[] SR =
    [
        8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
        9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
        9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
        15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8,
        8, 5, 12, 9, 12, 5, 14, 6, 8, 13, 6, 5, 15, 13, 11, 11,
    ];

    private static readonly uint[] KL = [0x00000000, 0x5A827999, 0x6ED9EBA1, 0x8F1BBCDC, 0xA953FD4E];
    private static readonly uint[] KR = [0x50A28BE6, 0x5C4DD124, 0x6D703EF3, 0x7A6D76E9, 0x00000000];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint F(int j, uint x, uint y, uint z) => (j >> 4) switch
    {
        0 => x ^ y ^ z,
        1 => (x & y) | (~x & z),
        2 => (x | ~y) ^ z,
        3 => (x & z) | (y & ~z),
        _ => x ^ (y | ~z),
    };

    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;

        int paddedLen = ((input.Length + 8) / 64 + 1) * 64;
        byte[] padded = new byte[paddedLen];
        input.CopyTo(padded);
        padded[input.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(paddedLen - 8), (ulong)input.Length * 8);

        Span<uint> x = stackalloc uint[16];
        for (int block = 0; block < paddedLen; block += 64)
        {
            for (int i = 0; i < 16; i++)
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(block + i * 4, 4));

            uint al = h0, bl = h1, cl = h2, dl = h3, el = h4;
            uint ar = h0, br = h1, cr = h2, dr = h3, er = h4;

            for (int j = 0; j < 80; j++)
            {
                uint t = uint.RotateLeft(al + F(j, bl, cl, dl) + x[RL[j]] + KL[j >> 4], SL[j]) + el;
                al = el; el = dl; dl = uint.RotateLeft(cl, 10); cl = bl; bl = t;

                t = uint.RotateLeft(ar + F(79 - j, br, cr, dr) + x[RR[j]] + KR[j >> 4], SR[j]) + er;
                ar = er; er = dr; dr = uint.RotateLeft(cr, 10); cr = br; br = t;
            }

            uint tmp = h1 + cl + dr;
            h1 = h2 + dl + er;
            h2 = h3 + el + ar;
            h3 = h4 + al + br;
            h4 = h0 + bl + cr;
            h0 = tmp;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output[0..4], h0);
        BinaryPrimitives.WriteUInt32LittleEndian(output[4..8], h1);
        BinaryPrimitives.WriteUInt32LittleEndian(output[8..12], h2);
        BinaryPrimitives.WriteUInt32LittleEndian(output[12..16], h3);
        BinaryPrimitives.WriteUInt32LittleEndian(output[16..20], h4);
    }
}

/// <summary>BLAKE2b (RFC 7693), unkeyed, variable output length 1..64.</summary>
internal static class Blake2b
{
    private static readonly ulong[] IV =
    [
        0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL, 0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
        0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL, 0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL,
    ];

    private static readonly byte[] Sigma =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    ];

    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int outLen = output.Length;
        if (outLen is < 1 or > 64) throw new ArgumentException("BLAKE2b output length must be 1..64 bytes", nameof(output));

        Span<ulong> h = stackalloc ulong[8];
        IV.CopyTo(h);
        h[0] ^= 0x01010000UL ^ (ulong)outLen;

        Span<byte> block = stackalloc byte[128];
        ulong counter = 0;

        while (input.Length > 128)
        {
            counter += 128;
            Compress(h, input[..128], counter, last: false);
            input = input[128..];
        }

        block.Clear();
        input.CopyTo(block);
        counter += (ulong)input.Length;
        Compress(h, block, counter, last: true);

        Span<byte> full = stackalloc byte[64];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(full.Slice(i * 8, 8), h[i]);
        full[..outLen].CopyTo(output);
    }

    private static void Compress(Span<ulong> h, ReadOnlySpan<byte> block, ulong counter, bool last)
    {
        Span<ulong> m = stackalloc ulong[16];
        Span<ulong> v = stackalloc ulong[16];
        for (int i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));

        h.CopyTo(v);
        IV.CopyTo(v[8..]);
        v[12] ^= counter;
        if (last) v[14] = ~v[14];

        for (int r = 0; r < 12; r++)
        {
            int s = (r % 10) * 16;
            G(v, 0, 4, 8, 12, m[Sigma[s + 0]], m[Sigma[s + 1]]);
            G(v, 1, 5, 9, 13, m[Sigma[s + 2]], m[Sigma[s + 3]]);
            G(v, 2, 6, 10, 14, m[Sigma[s + 4]], m[Sigma[s + 5]]);
            G(v, 3, 7, 11, 15, m[Sigma[s + 6]], m[Sigma[s + 7]]);
            G(v, 0, 5, 10, 15, m[Sigma[s + 8]], m[Sigma[s + 9]]);
            G(v, 1, 6, 11, 12, m[Sigma[s + 10]], m[Sigma[s + 11]]);
            G(v, 2, 7, 8, 13, m[Sigma[s + 12]], m[Sigma[s + 13]]);
            G(v, 3, 4, 9, 14, m[Sigma[s + 14]], m[Sigma[s + 15]]);
        }

        for (int i = 0; i < 8; i++)
            h[i] ^= v[i] ^ v[i + 8];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = ulong.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d];
        v[b] = ulong.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = ulong.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = ulong.RotateRight(v[b] ^ v[c], 63);
    }
}

/// <summary>scrypt (RFC 7914).</summary>
internal static class Scrypt
{
    public static void Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int logN, int r, int p, Span<byte> output)
    {
        if (logN is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(logN));
        if (r < 1 || p < 1) throw new ArgumentOutOfRangeException(nameof(r));
        int n = 1 << logN;
        int blockWords = 32 * r; // 128*r bytes

        byte[] b = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, 1,
            System.Security.Cryptography.HashAlgorithmName.SHA256, p * 128 * r);

        uint[] x = new uint[blockWords];
        uint[] y = new uint[blockWords];
        uint[] v = new uint[blockWords * n];

        try
        {
            for (int i = 0; i < p; i++)
            {
                Span<byte> chunk = b.AsSpan(i * 128 * r, 128 * r);
                for (int k = 0; k < blockWords; k++)
                    x[k] = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(k * 4, 4));

                for (int k = 0; k < n; k++)
                {
                    Array.Copy(x, 0, v, k * blockWords, blockWords);
                    BlockMix(x, y, r);
                }

                for (int k = 0; k < n; k++)
                {
                    int j = (int)(x[blockWords - 16] & (uint)(n - 1));
                    for (int w = 0; w < blockWords; w++)
                        x[w] ^= v[j * blockWords + w];
                    BlockMix(x, y, r);
                }

                for (int k = 0; k < blockWords; k++)
                    BinaryPrimitives.WriteUInt32LittleEndian(chunk.Slice(k * 4, 4), x[k]);
            }

            byte[] dk = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, b, 1,
                System.Security.Cryptography.HashAlgorithmName.SHA256, output.Length);
            dk.CopyTo(output);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(dk);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(b);
            Array.Clear(x);
            Array.Clear(y);
            Array.Clear(v);
        }
    }

    private static void BlockMix(uint[] b, uint[] y, int r)
    {
        Span<uint> x = stackalloc uint[16];
        b.AsSpan((2 * r - 1) * 16, 16).CopyTo(x);

        for (int i = 0; i < 2 * r; i++)
        {
            for (int k = 0; k < 16; k++)
                x[k] ^= b[i * 16 + k];
            Salsa208(x);
            // even blocks go to the first half, odd blocks to the second half
            int dest = (i / 2 + (i % 2) * r) * 16;
            x.CopyTo(y.AsSpan(dest, 16));
        }

        Array.Copy(y, b, 32 * r);
    }

    private static void Salsa208(Span<uint> b)
    {
        Span<uint> x = stackalloc uint[16];
        b.CopyTo(x);
        for (int i = 0; i < 8; i += 2)
        {
            x[4] ^= uint.RotateLeft(x[0] + x[12], 7); x[8] ^= uint.RotateLeft(x[4] + x[0], 9);
            x[12] ^= uint.RotateLeft(x[8] + x[4], 13); x[0] ^= uint.RotateLeft(x[12] + x[8], 18);
            x[9] ^= uint.RotateLeft(x[5] + x[1], 7); x[13] ^= uint.RotateLeft(x[9] + x[5], 9);
            x[1] ^= uint.RotateLeft(x[13] + x[9], 13); x[5] ^= uint.RotateLeft(x[1] + x[13], 18);
            x[14] ^= uint.RotateLeft(x[10] + x[6], 7); x[2] ^= uint.RotateLeft(x[14] + x[10], 9);
            x[6] ^= uint.RotateLeft(x[2] + x[14], 13); x[10] ^= uint.RotateLeft(x[6] + x[2], 18);
            x[3] ^= uint.RotateLeft(x[15] + x[11], 7); x[7] ^= uint.RotateLeft(x[3] + x[15], 9);
            x[11] ^= uint.RotateLeft(x[7] + x[3], 13); x[15] ^= uint.RotateLeft(x[11] + x[7], 18);
            x[1] ^= uint.RotateLeft(x[0] + x[3], 7); x[2] ^= uint.RotateLeft(x[1] + x[0], 9);
            x[3] ^= uint.RotateLeft(x[2] + x[1], 13); x[0] ^= uint.RotateLeft(x[3] + x[2], 18);
            x[6] ^= uint.RotateLeft(x[5] + x[4], 7); x[7] ^= uint.RotateLeft(x[6] + x[5], 9);
            x[4] ^= uint.RotateLeft(x[7] + x[6], 13); x[5] ^= uint.RotateLeft(x[4] + x[7], 18);
            x[11] ^= uint.RotateLeft(x[10] + x[9], 7); x[8] ^= uint.RotateLeft(x[11] + x[10], 9);
            x[9] ^= uint.RotateLeft(x[8] + x[11], 13); x[10] ^= uint.RotateLeft(x[9] + x[8], 18);
            x[12] ^= uint.RotateLeft(x[15] + x[14], 7); x[13] ^= uint.RotateLeft(x[12] + x[15], 9);
            x[14] ^= uint.RotateLeft(x[13] + x[12], 13); x[15] ^= uint.RotateLeft(x[14] + x[13], 18);
        }
        for (int i = 0; i < 16; i++)
            b[i] += x[i];
    }
}
