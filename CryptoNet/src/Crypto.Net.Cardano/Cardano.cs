using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Cardano;

/// <summary>Cardano network descriptor. ADA has 6 decimals (lovelace). Clients use the Koios REST API.</summary>
public sealed record CardanoChain(ChainId Id, string Name, byte NetworkId, uint ProtocolMagic, string DefaultApiUrl, bool IsTestnet = false, string? ExplorerUrl = null) : IChain
{
    public string Symbol => "ADA";
    public int Decimals => 6;

    public static readonly CardanoChain Mainnet = new(new ChainId("cardano"), "Cardano", 1, 764824073, "https://api.koios.rest/api/v1", ExplorerUrl: "https://cardanoscan.io");
    public static readonly CardanoChain Preprod = new(new ChainId("cardano-preprod"), "Cardano Preprod", 0, 1, "https://preprod.koios.rest/api/v1", true, "https://preprod.cardanoscan.io");
    public static readonly CardanoChain Preview = new(new ChainId("cardano-preview"), "Cardano Preview", 0, 2, "https://preview.koios.rest/api/v1", true, "https://preview.cardanoscan.io");

    public static IReadOnlyList<CardanoChain> All { get; } = [Mainnet, Preprod, Preview];
}

/// <summary>Shelley addresses (CIP-19): base, enterprise and reward (stake) addresses, plus legacy Byron addresses.</summary>
public static class CardanoAddress
{
    /// <summary>blake2b-224 of a public key: the key hash that addresses and witnesses refer to.</summary>
    public static byte[] KeyHash(ReadOnlySpan<byte> publicKey) => CryptoNative.Blake2b(publicKey, 28);

    /// <summary>Base address: payment and stake key hashes (header type 0).</summary>
    public static byte[] Base(byte networkId, ReadOnlySpan<byte> paymentKeyHash, ReadOnlySpan<byte> stakeKeyHash) =>
        [(byte)(0x00 | networkId), .. Check28(paymentKeyHash), .. Check28(stakeKeyHash)];

    /// <summary>Enterprise address: payment key hash only, no staking (header type 6).</summary>
    public static byte[] Enterprise(byte networkId, ReadOnlySpan<byte> paymentKeyHash) => [(byte)(0x60 | networkId), .. Check28(paymentKeyHash)];

    /// <summary>Reward (stake) address (header type 14).</summary>
    public static byte[] Reward(byte networkId, ReadOnlySpan<byte> stakeKeyHash) => [(byte)(0xe0 | networkId), .. Check28(stakeKeyHash)];

    /// <summary>Bech32 with the CIP-5 prefix for the address type and network.</summary>
    public static string ToBech32(ReadOnlySpan<byte> address)
    {
        if (address.Length == 0) throw new ArgumentException("Empty address", nameof(address));
        int type = address[0] >> 4;
        bool mainnet = (address[0] & 0x0f) == 1;
        string hrp = type switch
        {
            14 or 15 => mainnet ? "stake" : "stake_test",
            8 => throw new ArgumentException("Byron addresses use Base58", nameof(address)),
            _ => mainnet ? "addr" : "addr_test",
        };
        return CryptoNative.Bech32Encode(hrp, address);
    }

    /// <summary>Raw bytes of a Bech32 Shelley address or a Base58 Byron address.</summary>
    public static byte[] ToBytes(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.StartsWith("addr", StringComparison.Ordinal) || address.StartsWith("stake", StringComparison.Ordinal))
        {
            var (hrp, data, bech32m) = CryptoNative.Bech32Decode(address);
            if (bech32m || data.Length < 29) throw new FormatException($"Invalid Cardano address '{address}'");
            int type = data[0] >> 4;
            bool mainnet = (data[0] & 0x0f) == 1;
            bool stake = type is 14 or 15;
            string expected = stake ? (mainnet ? "stake" : "stake_test") : (mainnet ? "addr" : "addr_test");
            if (hrp != expected || type is >= 8 and <= 13) throw new FormatException($"Invalid Cardano address '{address}'");
            int expectedLength = type switch { <= 3 => 57, 4 or 5 => -1, _ => 29 };
            if (expectedLength > 0 && data.Length != expectedLength) throw new FormatException($"Invalid Cardano address length '{address}'");
            return data;
        }
        byte[] byron = CryptoNative.Base58Decode(address);
        if (!IsByron(byron)) throw new FormatException($"Invalid Cardano address '{address}'");
        return byron;
    }

    public static bool IsValid(string? address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        try
        {
            ToBytes(address);
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

    /// <summary>Payment key hash of a base or enterprise address (null for script or other addresses).</summary>
    public static byte[]? PaymentKeyHash(ReadOnlySpan<byte> address) =>
        address.Length >= 29 && (address[0] >> 4) is 0 or 2 or 4 or 6 ? address[1..29].ToArray() : null;

    // Byron: CBOR [tag 24 (bytes), crc32(bytes)].
    private static bool IsByron(byte[] raw)
    {
        try
        {
            var r = new System.Formats.Cbor.CborReader(raw);
            if (r.ReadStartArray() != 2 || r.ReadTag() != (System.Formats.Cbor.CborTag)24) return false;
            byte[] payload = r.ReadByteString();
            ulong crc = r.ReadUInt64();
            r.ReadEndArray();
            return r.BytesRemaining == 0 && crc == Crc32(payload);
        }
        catch (Exception ex) when (ex is System.Formats.Cbor.CborContentException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static ReadOnlySpan<byte> Check28(ReadOnlySpan<byte> hash) =>
        hash.Length == 28 ? hash : throw new ArgumentException("Key hashes are 28 bytes");

    /// <summary>1 ADA = 10^6 lovelace.</summary>
    public static Amount Ada(decimal ada) => Amount.FromDecimal(ada, 6);
    public static Amount Lovelace(ulong lovelace) => new(lovelace, 6);
}

/// <summary>
/// A Cardano wallet account (CIP-1852): a payment key at <c>m/1852'/1815'/account'/0/index</c> and the stake key at
/// <c>…/2/0</c>, both BIP32-Ed25519 extended keys. Its address is the base address of the two.
/// </summary>
public sealed class CardanoAccount : IAccount, IDisposable
{
    private readonly SecureBuffer _payment;
    private readonly SecureBuffer? _stake;

    /// <param name="paymentKey">Extended payment key <c>kL ‖ kR ‖ chainCode</c> (96 bytes) or <c>kL ‖ kR</c> (64 bytes).</param>
    /// <param name="stakeKey">Extended stake key, or null for an enterprise (no staking) address.</param>
    public CardanoAccount(ReadOnlySpan<byte> paymentKey, ReadOnlySpan<byte> stakeKey = default, CardanoChain? network = null, string? derivationPath = null)
    {
        if (paymentKey.Length is not (64 or 96)) throw new ArgumentException("Extended keys are 64 or 96 bytes", nameof(paymentKey));
        if (stakeKey.Length is not (0 or 64 or 96)) throw new ArgumentException("Extended keys are 64 or 96 bytes", nameof(stakeKey));
        _payment = SecureBuffer.FromBytes(paymentKey[..64]);
        PublicKey = CryptoNative.Ed25519ExtendedGetPublicKey(paymentKey[..32]);
        PaymentKeyHash = CardanoAddress.KeyHash(PublicKey.Span);
        Network = network ?? CardanoChain.Mainnet;
        if (stakeKey.Length > 0)
        {
            _stake = SecureBuffer.FromBytes(stakeKey[..64]);
            StakePublicKey = CryptoNative.Ed25519ExtendedGetPublicKey(stakeKey[..32]);
            StakeKeyHash = CardanoAddress.KeyHash(StakePublicKey);
            AddressBytes = CardanoAddress.Base(Network.NetworkId, PaymentKeyHash, StakeKeyHash);
            RewardAddress = CardanoAddress.ToBech32(CardanoAddress.Reward(Network.NetworkId, StakeKeyHash));
        }
        else
        {
            AddressBytes = CardanoAddress.Enterprise(Network.NetworkId, PaymentKeyHash);
        }
        Address = new Address(CardanoAddress.ToBech32(AddressBytes), Network.Id);
        EnterpriseAddress = CardanoAddress.ToBech32(CardanoAddress.Enterprise(Network.NetworkId, PaymentKeyHash));
        DerivationPath = derivationPath;
    }

    /// <summary>Opens account <paramref name="account"/>, address <paramref name="index"/> of a BIP-39 mnemonic (Yoroi, Eternl, Lace, Daedalus).</summary>
    public static CardanoAccount FromMnemonic(string mnemonic, string? passphrase = "", uint account = 0, uint index = 0, CardanoChain? network = null)
    {
        using var wallet = HdWallet.FromMnemonic(mnemonic, passphrase);
        return wallet.GetCardanoAccount(account, index, network);
    }

    public CardanoChain Network { get; }
    public IChain Chain => Network;

    /// <summary>The base address (or enterprise address without a stake key), Bech32.</summary>
    public Address Address { get; }
    public byte[] AddressBytes { get; }
    public string EnterpriseAddress { get; }

    /// <summary>The <c>stake…</c> reward address, or null without a stake key.</summary>
    public string? RewardAddress { get; }

    /// <summary>Payment public key.</summary>
    public ReadOnlyMemory<byte> PublicKey { get; }
    public byte[] PaymentKeyHash { get; }
    public byte[]? StakePublicKey { get; }
    public byte[]? StakeKeyHash { get; }
    public string? DerivationPath { get; }

    /// <summary>Signs with the payment key (extended-key ed25519).</summary>
    public byte[] Sign(ReadOnlySpan<byte> message) => CryptoNative.Ed25519SignExtended(_payment.Span[..32], _payment.Span[32..64], message);

    /// <summary>Signs with the stake key (for certificates and withdrawals).</summary>
    public byte[] SignWithStakeKey(ReadOnlySpan<byte> message) =>
        _stake is null ? throw new InvalidOperationException("This account has no stake key") : CryptoNative.Ed25519SignExtended(_stake.Span[..32], _stake.Span[32..64], message);

    public void Dispose()
    {
        _payment.Dispose();
        _stake?.Dispose();
    }
}

public static class CardanoWalletExtensions
{
    /// <summary>CIP-1852 account: payment key <c>m/1852'/1815'/account'/0/index</c>, stake key <c>m/1852'/1815'/account'/2/0</c>.</summary>
    public static CardanoAccount GetCardanoAccount(this HdWallet wallet, uint account = 0, uint index = 0, CardanoChain? network = null)
    {
        string path = $"m/1852'/1815'/{account}'/0/{index}";
        using var payment = wallet.DeriveBip32Ed25519(path);
        using var stake = wallet.DeriveBip32Ed25519($"m/1852'/1815'/{account}'/2/0");
        return new CardanoAccount(payment.Span, stake.Span, network, path);
    }

    /// <summary>Account extended public key (<c>A ‖ chainCode</c>, 64 bytes) at <c>m/1852'/1815'/account'</c>, for watch-only address generation.</summary>
    public static byte[] GetCardanoAccountXpub(this HdWallet wallet, uint account = 0)
    {
        using var key = wallet.DeriveBip32Ed25519($"m/1852'/1815'/{account}'");
        return [.. CryptoNative.Ed25519ExtendedGetPublicKey(key.Span[..32]), .. key.Span[64..96]];
    }

    /// <summary>Base address for <c>…/0/index</c> derived from an account xpub alone (no private key needed).</summary>
    public static string DeriveCardanoAddress(ReadOnlySpan<byte> accountXpub, uint index, CardanoChain network)
    {
        byte[] payment = CryptoNative.Bip32Ed25519DerivePublic(CryptoNative.Bip32Ed25519DerivePublic(accountXpub, 0), index);
        byte[] stake = CryptoNative.Bip32Ed25519DerivePublic(CryptoNative.Bip32Ed25519DerivePublic(accountXpub, 2), 0);
        return CardanoAddress.ToBech32(CardanoAddress.Base(network.NetworkId, CardanoAddress.KeyHash(payment.AsSpan(0, 32)), CardanoAddress.KeyHash(stake.AsSpan(0, 32))));
    }
}
