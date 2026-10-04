using System.Security.Cryptography;
using System.Text;
using Crypto.Net.Native;

namespace Crypto.Net.Wallet;

/// <summary>
/// BIP-39 mnemonic generation, validation, entropy recovery and seed derivation (English wordlist).
/// </summary>
public static class Mnemonic
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n', '　'];

    /// <summary>Generates a new mnemonic from cryptographically secure randomness.</summary>
    /// <param name="wordCount">12, 15, 18, 21 or 24.</param>
    public static string Generate(int wordCount = 12)
    {
        int entropyBytes = wordCount switch
        {
            12 => 16,
            15 => 20,
            18 => 24,
            21 => 28,
            24 => 32,
            _ => throw new ArgumentOutOfRangeException(nameof(wordCount), "Word count must be 12, 15, 18, 21 or 24"),
        };

        Span<byte> entropy = stackalloc byte[entropyBytes];
        RandomNumberGenerator.Fill(entropy);
        try
        {
            return FromEntropy(entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    /// <summary>Encodes 16/20/24/28/32 bytes of entropy as a mnemonic sentence.</summary>
    public static string FromEntropy(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length is not (16 or 20 or 24 or 28 or 32))
            throw new ArgumentException("Entropy must be 16, 20, 24, 28 or 32 bytes", nameof(entropy));

        int checksumBits = entropy.Length / 4;
        int wordCount = (entropy.Length * 8 + checksumBits) / 11;
        byte checksum = CryptoNative.Sha256(entropy)[0];

        var words = new string[wordCount];
        int bitPos = 0;
        for (int w = 0; w < wordCount; w++)
        {
            int index = 0;
            for (int b = 0; b < 11; b++, bitPos++)
                index = (index << 1) | GetBit(entropy, checksum, bitPos);
            words[w] = Bip39Wordlist.GetWord(index);
        }
        return string.Join(' ', words);
    }

    private static int GetBit(ReadOnlySpan<byte> entropy, byte checksum, int pos)
    {
        int entropyBits = entropy.Length * 8;
        return pos < entropyBits
            ? (entropy[pos >> 3] >> (7 - (pos & 7))) & 1
            : (checksum >> (7 - (pos - entropyBits))) & 1;
    }

    /// <summary>Collapses whitespace and lower-cases a user-supplied phrase.</summary>
    public static string Normalize(string mnemonic)
    {
        ArgumentNullException.ThrowIfNull(mnemonic);
        return string.Join(' ', mnemonic.Normalize(NormalizationForm.FormKD)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant()));
    }

    /// <summary>Recovers the original entropy; throws <see cref="FormatException"/> for an invalid phrase.</summary>
    public static byte[] ToEntropy(string mnemonic)
    {
        string[] words = Normalize(mnemonic).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (12 or 15 or 18 or 21 or 24))
            throw new FormatException($"Mnemonic must have 12, 15, 18, 21 or 24 words (got {words.Length})");

        int totalBits = words.Length * 11;
        int checksumBits = totalBits / 33;
        int entropyBytes = (totalBits - checksumBits) / 8;

        byte[] entropy = new byte[entropyBytes];
        int checksum = 0;
        int bitPos = 0;
        foreach (string word in words)
        {
            int index = Bip39Wordlist.GetIndex(word);
            if (index < 0) throw new FormatException($"'{word}' is not in the BIP-39 English wordlist");
            for (int b = 10; b >= 0; b--, bitPos++)
            {
                int bit = (index >> b) & 1;
                if (bitPos < entropyBytes * 8)
                    entropy[bitPos >> 3] |= (byte)(bit << (7 - (bitPos & 7)));
                else
                    checksum = (checksum << 1) | bit;
            }
        }

        int expected = CryptoNative.Sha256(entropy)[0] >> (8 - checksumBits);
        if (expected != checksum)
        {
            CryptographicOperations.ZeroMemory(entropy);
            throw new FormatException("Invalid BIP-39 checksum");
        }
        return entropy;
    }

    public static bool Validate(string mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic)) return false;
        try
        {
            CryptographicOperations.ZeroMemory(ToEntropy(mnemonic));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>BIP-39 seed (64 bytes). Validates the checksum first.</summary>
    public static byte[] ToSeed(string mnemonic, string? passphrase = "")
    {
        if (!Validate(mnemonic))
            throw new ArgumentException("Invalid BIP-39 mnemonic phrase", nameof(mnemonic));
        return CryptoNative.Bip39MnemonicToSeed(Normalize(mnemonic), passphrase ?? "");
    }

    /// <summary>
    /// Substrate "mini secret" used by Polkadot wallets (substrate-bip39):
    /// PBKDF2-HMAC-SHA512(entropy, "mnemonic" + passphrase, 2048)[0..32].
    /// Note this differs from the BIP-39 seed, which hashes the words rather than the entropy.
    /// </summary>
    public static byte[] ToSubstrateSeed(string mnemonic, string? passphrase = "")
    {
        byte[] entropy = ToEntropy(mnemonic);
        byte[] salt = Encoding.UTF8.GetBytes(("mnemonic" + (passphrase ?? "")).Normalize(NormalizationForm.FormKD));
        try
        {
            byte[] full = CryptoNative.Pbkdf2Sha512(entropy, salt, 2048, 64);
            byte[] seed = full[..32];
            CryptographicOperations.ZeroMemory(full);
            return seed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(salt);
        }
    }
}
