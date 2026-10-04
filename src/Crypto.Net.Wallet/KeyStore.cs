using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Crypto.Net.Native;

namespace Crypto.Net.Wallet;

/// <summary>Key-derivation settings for <see cref="KeyStore"/>.</summary>
public abstract record KeyStoreKdf
{
    /// <summary>scrypt with N = 2^18, r = 8, p = 1 (geth "standard"). Requires ~256 MB of memory.</summary>
    public static KeyStoreKdf Standard { get; } = new ScryptKdf(18, 8, 1);

    /// <summary>scrypt with N = 2^12, r = 8, p = 6 (geth "light"). Faster, weaker.</summary>
    public static KeyStoreKdf Light { get; } = new ScryptKdf(12, 8, 6);

    public sealed record ScryptKdf(int LogN, int R, int P) : KeyStoreKdf;
    public sealed record Pbkdf2Kdf(int Iterations) : KeyStoreKdf;
}

/// <summary>
/// Encrypted private-key storage in the Web3 Secret Storage v3 format (compatible with geth, MetaMask,
/// ethers, Nethereum and MyEtherWallet): scrypt or PBKDF2-HMAC-SHA256, AES-128-CTR and a Keccak-256 MAC.
/// </summary>
public static class KeyStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Encrypts <paramref name="privateKey"/> with <paramref name="password"/>.</summary>
    /// <param name="address">Optional address hint stored in clear text (lowercase hex without 0x for EVM).</param>
    public static string Encrypt(ReadOnlySpan<byte> privateKey, string password, string? address = null, KeyStoreKdf? kdf = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (privateKey.IsEmpty) throw new ArgumentException("Private key is empty", nameof(privateKey));
        kdf ??= KeyStoreKdf.Standard;

        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[] derived = DeriveKey(passwordBytes, salt, kdf);
        CryptographicOperations.ZeroMemory(passwordBytes);

        try
        {
            byte[] ciphertext = AesCtr(derived.AsSpan(0, 16), iv, privateKey);
            byte[] mac = CryptoNative.Keccak256([.. derived.AsSpan(16, 16), .. ciphertext]);

            var kdfParams = kdf switch
            {
                KeyStoreKdf.ScryptKdf s => new JsonObject
                {
                    ["dklen"] = 32,
                    ["n"] = 1 << s.LogN,
                    ["p"] = s.P,
                    ["r"] = s.R,
                    ["salt"] = Convert.ToHexStringLower(salt),
                },
                KeyStoreKdf.Pbkdf2Kdf p => new JsonObject
                {
                    ["c"] = p.Iterations,
                    ["dklen"] = 32,
                    ["prf"] = "hmac-sha256",
                    ["salt"] = Convert.ToHexStringLower(salt),
                },
                _ => throw new NotSupportedException(),
            };

            var root = new JsonObject
            {
                ["version"] = 3,
                ["id"] = Guid.NewGuid().ToString(),
                ["crypto"] = new JsonObject
                {
                    ["cipher"] = "aes-128-ctr",
                    ["cipherparams"] = new JsonObject { ["iv"] = Convert.ToHexStringLower(iv) },
                    ["ciphertext"] = Convert.ToHexStringLower(ciphertext),
                    ["kdf"] = kdf is KeyStoreKdf.ScryptKdf ? "scrypt" : "pbkdf2",
                    ["kdfparams"] = kdfParams,
                    ["mac"] = Convert.ToHexStringLower(mac),
                },
            };
            if (!string.IsNullOrWhiteSpace(address))
                root["address"] = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..].ToLowerInvariant() : address;

            return root.ToJsonString(Indented);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>Decrypts a v3 keystore. Throws <see cref="CryptographicException"/> on a wrong password.</summary>
    public static SecureBuffer Decrypt(string json, string password)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(password);

        JsonNode root = JsonNode.Parse(json) ?? throw new FormatException("Invalid keystore JSON");
        if (root["version"]?.GetValue<int>() != 3) throw new NotSupportedException("Only Web3 Secret Storage version 3 is supported");
        JsonNode crypto = root["crypto"] ?? root["Crypto"] ?? throw new FormatException("Missing 'crypto' section");

        string cipher = crypto["cipher"]?.GetValue<string>() ?? "";
        if (!string.Equals(cipher, "aes-128-ctr", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported cipher '{cipher}'");

        byte[] ciphertext = Convert.FromHexString(Req(crypto, "ciphertext"));
        byte[] iv = Convert.FromHexString(Req(crypto["cipherparams"]!, "iv"));
        byte[] expectedMac = Convert.FromHexString(Req(crypto, "mac"));
        JsonNode p = crypto["kdfparams"] ?? throw new FormatException("Missing kdfparams");
        byte[] salt = Convert.FromHexString(Req(p, "salt"));
        int dkLen = p["dklen"]?.GetValue<int>() ?? 32;
        if (dkLen < 32) throw new FormatException("dklen must be at least 32");

        KeyStoreKdf kdf = Req(crypto, "kdf").ToLowerInvariant() switch
        {
            "scrypt" => new KeyStoreKdf.ScryptKdf(Log2(p["n"]!.GetValue<int>()), p["r"]!.GetValue<int>(), p["p"]!.GetValue<int>()),
            "pbkdf2" => (p["prf"]?.GetValue<string>() ?? "hmac-sha256") == "hmac-sha256"
                ? new KeyStoreKdf.Pbkdf2Kdf(p["c"]!.GetValue<int>())
                : throw new NotSupportedException("Only hmac-sha256 PBKDF2 is supported"),
            var other => throw new NotSupportedException($"Unsupported KDF '{other}'"),
        };

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[] derived = DeriveKey(passwordBytes, salt, kdf, dkLen);
        CryptographicOperations.ZeroMemory(passwordBytes);
        try
        {
            byte[] mac = CryptoNative.Keccak256([.. derived.AsSpan(16, 16), .. ciphertext]);
            if (!CryptographicOperations.FixedTimeEquals(mac, expectedMac))
                throw new CryptographicException("Keystore MAC mismatch: wrong password or corrupted file");

            return SecureBuffer.FromBytesAndClear(AesCtr(derived.AsSpan(0, 16), iv, ciphertext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>Reads the clear-text address hint, if present.</summary>
    public static string? ReadAddress(string json) => JsonNode.Parse(json)?["address"]?.GetValue<string>();

    private static string Req(JsonNode node, string name) =>
        node[name]?.GetValue<string>() ?? throw new FormatException($"Missing '{name}'");

    private static int Log2(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new FormatException("scrypt n must be a power of two");
        return System.Numerics.BitOperations.Log2((uint)n);
    }

    private static byte[] DeriveKey(byte[] password, byte[] salt, KeyStoreKdf kdf, int dkLen = 32) => kdf switch
    {
        KeyStoreKdf.ScryptKdf s => CryptoNative.Scrypt(password, salt, s.LogN, s.R, s.P, dkLen),
        KeyStoreKdf.Pbkdf2Kdf p => CryptoNative.Pbkdf2Sha256(password, salt, p.Iterations, dkLen),
        _ => throw new NotSupportedException(),
    };

    /// <summary>AES-128-CTR with a 128-bit big-endian counter starting at <paramref name="iv"/>.</summary>
    private static byte[] AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input)
    {
        if (iv.Length != 16) throw new FormatException("AES-CTR IV must be 16 bytes");
        using var aes = Aes.Create();
        aes.Key = key.ToArray();

        byte[] output = new byte[input.Length];
        Span<byte> counter = stackalloc byte[16];
        Span<byte> keystream = stackalloc byte[16];
        iv.CopyTo(counter);

        for (int offset = 0; offset < input.Length; offset += 16)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            int n = Math.Min(16, input.Length - offset);
            for (int i = 0; i < n; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
            for (int i = 15; i >= 0 && ++counter[i] == 0; i--) { }
        }
        CryptographicOperations.ZeroMemory(keystream);
        return output;
    }
}
