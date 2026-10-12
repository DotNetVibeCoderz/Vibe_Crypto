using Crypto.Net.Core;
using System.Text;
using Crypto.Net.Native;

namespace Crypto.Net.Solana;

/// <summary>Anchor conventions: 8-byte instruction and account discriminators.</summary>
public static class Anchor
{
    /// <summary><c>sha256("global:" + name)[..8]</c> for instruction <paramref name="name"/> (snake_case as in Rust).</summary>
    public static byte[] InstructionDiscriminator(string name) => CryptoNative.Sha256(Encoding.UTF8.GetBytes("global:" + name))[..8];

    /// <summary><c>sha256("account:" + Name)[..8]</c> for account type <paramref name="name"/> (PascalCase).</summary>
    public static byte[] AccountDiscriminator(string name) => CryptoNative.Sha256(Encoding.UTF8.GetBytes("account:" + name))[..8];

    /// <summary>Checks the discriminator of account data and returns the Borsh payload after it.</summary>
    public static ReadOnlySpan<byte> AccountPayload(ReadOnlySpan<byte> data, ReadOnlySpan<byte> discriminator)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual(discriminator)) throw new FormatException("Account discriminator does not match");
        return data[8..];
    }
}
