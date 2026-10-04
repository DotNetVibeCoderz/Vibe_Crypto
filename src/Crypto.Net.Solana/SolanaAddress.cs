using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Native;

namespace Crypto.Net.Solana;

/// <summary>Solana public keys (Base58, 32 bytes) and program-derived addresses.</summary>
public static class SolanaAddress
{
    public const string SystemProgram = "11111111111111111111111111111111";
    public const string TokenProgram = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
    public const string Token2022Program = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
    public const string AssociatedTokenProgram = "ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL";
    public const string MemoProgram = "MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr";
    public const string ComputeBudgetProgram = "ComputeBudget111111111111111111111111111111";
    public const string SysvarRent = "SysvarRent111111111111111111111111111111111";

    public static Address FromPrivateKey(ReadOnlySpan<byte> privateKey32, ChainId? chain = null) =>
        FromPublicKey(CryptoNative.Ed25519GetPublicKey(privateKey32), chain);

    public static Address FromPublicKey(ReadOnlySpan<byte> publicKey32, ChainId? chain = null)
    {
        if (publicKey32.Length != 32) throw new ArgumentException("Solana public keys are 32 bytes", nameof(publicKey32));
        return new Address(CryptoNative.Base58Encode(publicKey32), chain ?? ChainId.Solana);
    }

    /// <summary>Decodes a Base58 public key; throws <see cref="FormatException"/> unless it is exactly 32 bytes.</summary>
    public static byte[] Decode(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        byte[] bytes = CryptoNative.Base58Decode(address.Trim());
        return bytes.Length == 32 ? bytes : throw new FormatException($"'{address}' is not a 32-byte Solana public key");
    }

    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length is < 32 or > 44) return false;
        try
        {
            return CryptoNative.Base58Decode(address).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True for keys on the ed25519 curve (wallets); PDAs are always off-curve.</summary>
    public static bool IsOnCurve(string address) => CryptoNative.Ed25519IsOnCurve(Decode(address));

    /// <summary>sha256(seeds || programId || "ProgramDerivedAddress"); fails if the result lies on the curve.</summary>
    public static string CreateProgramAddress(IEnumerable<byte[]> seeds, string programId)
    {
        using var ms = new MemoryStream();
        foreach (var seed in seeds)
        {
            if (seed.Length > 32) throw new ArgumentException("Seeds are limited to 32 bytes");
            ms.Write(seed);
        }
        ms.Write(Decode(programId));
        ms.Write("ProgramDerivedAddress"u8);
        byte[] hash = CryptoNative.Sha256(ms.ToArray());
        if (CryptoNative.Ed25519IsOnCurve(hash)) throw new InvalidOperationException("Invalid seeds: address lies on the ed25519 curve");
        return CryptoNative.Base58Encode(hash);
    }

    /// <summary>Finds the canonical PDA and its bump seed (searching 255 down to 0).</summary>
    public static (string Address, byte Bump) FindProgramAddress(IEnumerable<byte[]> seeds, string programId)
    {
        var list = seeds.ToList();
        for (int bump = 255; bump >= 0; bump--)
        {
            try
            {
                return (CreateProgramAddress([.. list, [(byte)bump]], programId), (byte)bump);
            }
            catch (InvalidOperationException)
            {
                // on curve, try the next bump
            }
        }
        throw new InvalidOperationException("Unable to find a viable program address");
    }

    /// <summary>Associated token account of <paramref name="owner"/> for <paramref name="mint"/>.</summary>
    public static string GetAssociatedTokenAddress(string owner, string mint, string tokenProgram = TokenProgram) =>
        FindProgramAddress([Decode(owner), Decode(tokenProgram), Decode(mint)], AssociatedTokenProgram).Address;

    public static byte[] Utf8Seed(string seed) => Encoding.UTF8.GetBytes(seed);
}
