using System.Numerics;
using System.Text;
using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Ton;

/// <summary>TON network descriptor. TON has 9 decimals (nanotons). Clients use TON Center's HTTP API.</summary>
public sealed record TonChain(ChainId Id, string Name, int GlobalId, string DefaultApiUrl, bool IsTestnet = false, string? ExplorerUrl = null) : IChain
{
    public string Symbol => "TON";
    public int Decimals => 9;

    public static readonly TonChain Mainnet = new(new ChainId("ton"), "TON", -239, "https://toncenter.com/api", ExplorerUrl: "https://tonviewer.com");
    public static readonly TonChain Testnet = new(new ChainId("ton-testnet"), "TON Testnet", -3, "https://testnet.toncenter.com/api", true, "https://testnet.tonviewer.com");

    public static IReadOnlyList<TonChain> All { get; } = [Mainnet, Testnet];
}

/// <summary>A TON account address: a workchain and a 256-bit account id.</summary>
public sealed record TonAddress
{
    public TonAddress(int workchain, byte[] hash)
    {
        if (hash is not { Length: 32 }) throw new ArgumentException("TON account ids are 32 bytes", nameof(hash));
        if (workchain is < -128 or > 127) throw new ArgumentOutOfRangeException(nameof(workchain));
        Workchain = workchain;
        Hash = hash;
    }

    public int Workchain { get; }
    public byte[] Hash { get; }

    /// <summary>Parses the raw form (<c>0:hex</c>) or the 48-character user-friendly form (base64 or base64url).</summary>
    public static TonAddress Parse(string address) => ParseWithFlags(address).Address;

    /// <summary>Parses an address and, for the user-friendly form, its bounceable and test-only flags.</summary>
    public static (TonAddress Address, bool? Bounceable, bool? TestOnly) ParseWithFlags(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        int colon = address.IndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(address.AsSpan(0, colon), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int wc))
                throw new FormatException($"Invalid TON address '{address}'");
            string hex = address[(colon + 1)..];
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) throw new FormatException($"Invalid TON address '{address}'");
            return (new TonAddress(wc, System.Convert.FromHexString(hex)), null, null);
        }
        if (address.Length != 48) throw new FormatException($"Invalid TON address '{address}'");
        byte[] raw;
        try
        {
            raw = System.Convert.FromBase64String(address.Replace('-', '+').Replace('_', '/'));
        }
        catch (FormatException)
        {
            throw new FormatException($"Invalid TON address '{address}'");
        }
        if (raw.Length != 36 || Crc16(raw.AsSpan(0, 34)) != (ushort)(raw[34] << 8 | raw[35])) throw new FormatException($"Invalid TON address checksum '{address}'");
        byte tag = raw[0];
        bool testOnly = (tag & 0x80) != 0;
        bool bounceable = (tag & 0x7f) switch
        {
            0x11 => true,
            0x51 => false,
            _ => throw new FormatException($"Unknown TON address tag 0x{tag:x2}"),
        };
        return (new TonAddress((sbyte)raw[1], raw[2..34]), bounceable, testOnly);
    }

    public static bool IsValid(string? address)
    {
        if (address is null) return false;
        try
        {
            Parse(address);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary><c>workchain:hex</c>.</summary>
    public string ToRawString() => $"{Workchain}:{System.Convert.ToHexStringLower(Hash)}";

    /// <summary>
    /// User-friendly form. Wallets are shown non-bounceable (<c>UQ…</c>) and contracts bounceable (<c>EQ…</c>);
    /// test-only addresses start with <c>0Q…</c>/<c>kQ…</c>.
    /// </summary>
    public string ToString(bool bounceable, bool testOnly = false, bool urlSafe = true)
    {
        byte[] raw = new byte[36];
        raw[0] = (byte)((bounceable ? 0x11 : 0x51) | (testOnly ? 0x80 : 0));
        raw[1] = (byte)(sbyte)Workchain;
        Hash.CopyTo(raw, 2);
        ushort crc = Crc16(raw.AsSpan(0, 34));
        raw[34] = (byte)(crc >> 8);
        raw[35] = (byte)crc;
        string s = System.Convert.ToBase64String(raw);
        return urlSafe ? s.Replace('+', '-').Replace('/', '_') : s;
    }

    /// <summary>Bounceable, mainnet, URL-safe.</summary>
    public override string ToString() => ToString(bounceable: true);

    public bool Equals(TonAddress? other) => other is not null && Workchain == other.Workchain && Hash.AsSpan().SequenceEqual(other.Hash);
    public override int GetHashCode() => HashCode.Combine(Workchain, BitConverter.ToInt32(Hash, 0));

    /// <summary>CRC-16/XMODEM, the checksum of user-friendly addresses.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
        }
        return (ushort)crc;
    }
}

/// <summary>Units: 1 TON = 10^9 nanotons.</summary>
public static class TonUnits
{
    public static Amount Ton(decimal ton) => Amount.FromDecimal(ton, 9);
    public static Amount Nano(BigInteger nanotons) => new(nanotons, 9);
    public static BigInteger ToNano(decimal ton) => Ton(ton).BaseUnits;
}

/// <summary>
/// TON's own mnemonic scheme (Tonkeeper, MyTonWallet, TON CLI): 24 BIP-39 English words whose HMAC-SHA512 entropy
/// passes a PBKDF2 check. It is not BIP-39: the words carry no checksum and keys are not derived from a BIP-32 seed.
/// </summary>
public static class TonMnemonic
{
    private const int Iterations = 100_000;

    /// <summary>Generates a new mnemonic (optionally protected by <paramref name="password"/>).</summary>
    public static string Generate(int wordCount = 24, string? password = null)
    {
        var words = Bip39Wordlist.Words;
        while (true)
        {
            var m = new string[wordCount];
            for (int i = 0; i < wordCount; i++) m[i] = words[System.Security.Cryptography.RandomNumberGenerator.GetInt32(words.Count)];
            string phrase = string.Join(' ', m);
            if (!string.IsNullOrEmpty(password) && !IsPasswordNeeded(phrase)) continue;
            if (!IsBasicSeed(Entropy(phrase, password))) continue;
            return phrase;
        }
    }

    public static bool Validate(string mnemonic, string? password = null)
    {
        string phrase = Normalize(mnemonic);
        var set = Bip39Wordlist.Words;
        if (phrase.Split(' ').Any(w => !set.Contains(w))) return false;
        if (!string.IsNullOrEmpty(password) && !IsPasswordNeeded(phrase)) return false;
        return IsBasicSeed(Entropy(phrase, password));
    }

    /// <summary>True when the mnemonic was generated with a password (and is unusable without it).</summary>
    public static bool IsPasswordNeeded(string mnemonic)
    {
        byte[] e = Entropy(Normalize(mnemonic), null);
        return CryptoNative.Pbkdf2Sha512(e, "TON fast seed version"u8, 1, 64)[0] == 1 && !IsBasicSeed(e);
    }

    /// <summary>The 32-byte ed25519 seed: PBKDF2-SHA512(entropy, "TON default seed", 100000)[0..32].</summary>
    public static byte[] ToPrivateKey(string mnemonic, string? password = null)
    {
        string phrase = Normalize(mnemonic);
        if (!Validate(phrase, password)) throw new FormatException("Invalid TON mnemonic");
        byte[] e = Entropy(phrase, password);
        byte[] seed = CryptoNative.Pbkdf2Sha512(e, "TON default seed"u8, Iterations, 64);
        try
        {
            return seed[..32];
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(e);
        }
    }

    private static string Normalize(string mnemonic) =>
        string.Join(' ', mnemonic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()));

    private static byte[] Entropy(string phrase, string? password) =>
        CryptoNative.HmacSha512(Encoding.UTF8.GetBytes(phrase), Encoding.UTF8.GetBytes(password ?? ""));

    private static bool IsBasicSeed(byte[] entropy) =>
        CryptoNative.Pbkdf2Sha512(entropy, "TON seed version"u8, Math.Max(1, Iterations / 256), 64)[0] == 0;
}

/// <summary>Wallet contract versions. W5 (V5R1) is the current default in Tonkeeper; V4R2 is still widely used.</summary>
public enum TonWalletVersion { V4R2, V5R1 }

/// <summary>
/// A TON wallet: an ed25519 key plus a wallet contract. The address is the hash of the contract's state init, so the
/// same key has a different address for each wallet version (and, for W5, each network).
/// </summary>
public sealed class TonAccount : IAccount, IDisposable
{
    private readonly SecureBuffer _seed;

    public TonAccount(ReadOnlySpan<byte> seed32, TonChain? network = null, TonWalletVersion version = TonWalletVersion.V5R1, int workchain = 0, uint? subwalletId = null, string? derivationPath = null)
    {
        if (seed32.Length != 32) throw new ArgumentException("TON keys are 32-byte ed25519 seeds", nameof(seed32));
        _seed = SecureBuffer.FromBytes(seed32);
        PublicKey = CryptoNative.Ed25519GetPublicKey(seed32);
        Network = network ?? TonChain.Mainnet;
        Version = version;
        Workchain = workchain;
        WalletId = subwalletId ?? TonWalletContract.DefaultWalletId(version, Network, workchain);
        StateInit = TonWalletContract.StateInit(version, PublicKey.Span, WalletId);
        WalletAddress = new TonAddress(workchain, StateInit.Hash());
        Address = new Address(WalletAddress.ToString(bounceable: false, testOnly: Network.IsTestnet), Network.Id);
        DerivationPath = derivationPath;
    }

    /// <summary>Opens the wallet of a TON (Tonkeeper-style) 24-word mnemonic.</summary>
    public static TonAccount FromMnemonic(string mnemonic, TonChain? network = null, TonWalletVersion version = TonWalletVersion.V5R1, string? password = null)
    {
        byte[] key = TonMnemonic.ToPrivateKey(mnemonic, password);
        try
        {
            return new TonAccount(key, network, version);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    public TonChain Network { get; }
    public IChain Chain => Network;
    public TonWalletVersion Version { get; }
    public int Workchain { get; }
    public uint WalletId { get; }

    /// <summary>The wallet contract's code and initial data (sent with the first transaction to deploy it).</summary>
    public TonCell StateInit { get; }
    public TonAddress WalletAddress { get; }

    /// <summary>The non-bounceable user-friendly address (<c>UQ…</c>, or <c>0Q…</c> on testnet), as wallets display it.</summary>
    public Address Address { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public string? DerivationPath { get; }

    public byte[] Sign(ReadOnlySpan<byte> message) => CryptoNative.Ed25519Sign(_seed.Span, message);

    /// <summary>The signed body of a transfer (the external message body the wallet contract checks).</summary>
    public TonCell CreateTransferBody(uint seqno, IReadOnlyList<TonMessage> messages, uint? validUntil = null, byte sendMode = TonSendMode.Default) =>
        TonWalletContract.CreateTransfer(this, seqno, messages, validUntil ?? (seqno == 0 ? uint.MaxValue : (uint)DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()), sendMode);

    /// <summary>
    /// The external message to broadcast: the signed body, plus the state init when <paramref name="seqno"/> is 0 (the
    /// first transaction deploys the wallet).
    /// </summary>
    public TonCell CreateTransfer(uint seqno, IReadOnlyList<TonMessage> messages, uint? validUntil = null, byte sendMode = TonSendMode.Default) =>
        TonMessage.External(WalletAddress, CreateTransferBody(seqno, messages, validUntil, sendMode), seqno == 0 ? StateInit : null);

    public KeySigner CreateSigner() => new(Network, Address, _seed.Clone(), SignatureScheme.Ed25519);

    public void Dispose() => _seed.Dispose();
}

public static class TonWalletExtensions
{
    /// <summary>
    /// TON key from a BIP-39 wallet at <c>m/44'/607'/index'</c> (SLIP-10 ed25519, the path multi-chain wallets use).
    /// Tonkeeper-style 24-word phrases are not BIP-39; open those with <see cref="TonAccount.FromMnemonic"/>.
    /// </summary>
    public static TonAccount GetTonAccount(this HdWallet wallet, uint index = 0, TonChain? network = null, TonWalletVersion version = TonWalletVersion.V5R1)
    {
        string path = $"m/44'/607'/{index}'";
        using var key = wallet.DeriveEd25519Path(path);
        return new TonAccount(key.PrivateKey.Span, network, version, derivationPath: path);
    }
}
