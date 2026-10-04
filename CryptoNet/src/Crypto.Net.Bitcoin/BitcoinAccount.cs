using Crypto.Net.Core;
using Crypto.Net.Native;
using Crypto.Net.Wallet;

namespace Crypto.Net.Bitcoin;

/// <summary>A single-key Bitcoin account of a given address type.</summary>
public sealed class BitcoinAccount : IAccount, IDisposable
{
    private readonly SecureBuffer _privateKey;

    public BitcoinAccount(ReadOnlySpan<byte> privateKey, BitcoinAddressType type = BitcoinAddressType.SegWitP2WPKH, BitcoinNetwork? network = null, string? derivationPath = null)
    {
        if (type is not (BitcoinAddressType.LegacyP2PKH or BitcoinAddressType.SegWitP2WPKH or BitcoinAddressType.TaprootP2TR))
            throw new ArgumentException("Single-key accounts support P2PKH, P2WPKH and P2TR", nameof(type));
        _privateKey = SecureBuffer.FromBytes(privateKey);
        Network = network ?? BitcoinNetwork.Mainnet;
        Type = type;
        PublicKey = CryptoNative.Secp256k1GetPublicKey(privateKey, compressed: true);
        Address = BitcoinAddress.FromPublicKey(PublicKey.Span, type, Network);
        ScriptPubKey = BitcoinAddress.GetScriptPubKey(Address.Value, Network);
        DerivationPath = derivationPath;
    }

    public static BitcoinAccount FromWif(string wif, BitcoinAddressType type = BitcoinAddressType.SegWitP2WPKH, BitcoinNetwork? network = null)
    {
        using var key = BitcoinAddress.PrivateKeyFromWif(wif, out _, network);
        return new BitcoinAccount(key.Span, type, network);
    }

    public BitcoinNetwork Network { get; }
    public IChain Chain => Network;
    public BitcoinAddressType Type { get; }
    public Address Address { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public byte[] ScriptPubKey { get; }
    public string? DerivationPath { get; }

    public string ExportWif() => BitcoinAddress.WifFromPrivateKey(_privateKey.Span, Network);

    /// <summary>Signs all inputs of <paramref name="transaction"/>; <paramref name="spentOutputs"/> must be in input order.</summary>
    public void Sign(BitcoinTransaction transaction, IReadOnlyList<BitcoinUtxo> spentOutputs) =>
        transaction.SignAllInputs(_privateKey.Span, spentOutputs);

    public void Dispose() => _privateKey.Dispose();
}

/// <summary>HD wallet helpers: BIP-44 (legacy), BIP-84 (native SegWit) and BIP-86 (Taproot) paths.</summary>
public static class BitcoinWalletExtensions
{
    public static BitcoinAccount GetBitcoinAccount(this HdWallet wallet, BitcoinAddressType type = BitcoinAddressType.SegWitP2WPKH,
        uint index = 0, BitcoinNetwork? network = null, uint account = 0, bool change = false)
    {
        network ??= BitcoinNetwork.Mainnet;
        uint purpose = type switch
        {
            BitcoinAddressType.LegacyP2PKH => 44,
            BitcoinAddressType.SegWitP2WPKH => 84,
            BitcoinAddressType.TaprootP2TR => 86,
            _ => throw new ArgumentException("HD accounts support P2PKH, P2WPKH and P2TR", nameof(type)),
        };
        string path = $"m/{purpose}'/{network.Bip44CoinType}'/{account}'/{(change ? 1 : 0)}/{index}";
        using var key = wallet.DerivePath(path);
        return new BitcoinAccount(key.PrivateKey.Span, type, network, path);
    }

    /// <summary>Account-level extended public key (xpub/zpub/tpub/vpub) for watch-only wallets.</summary>
    public static string GetBitcoinAccountXpub(this HdWallet wallet, BitcoinAddressType type = BitcoinAddressType.SegWitP2WPKH, BitcoinNetwork? network = null, uint account = 0)
    {
        network ??= BitcoinNetwork.Mainnet;
        uint purpose = type switch { BitcoinAddressType.LegacyP2PKH => 44, BitcoinAddressType.TaprootP2TR => 86, _ => 84 };
        using var key = wallet.DerivePath($"m/{purpose}'/{network.Bip44CoinType}'/{account}'");
        var version = (type, network.IsTestnet) switch
        {
            (BitcoinAddressType.SegWitP2WPKH, false) => ExtendedKeyVersion.SegWit,
            (BitcoinAddressType.SegWitP2WPKH, true) => ExtendedKeyVersion.SegWitTestnet,
            (_, true) => ExtendedKeyVersion.Testnet,
            _ => ExtendedKeyVersion.Mainnet,
        };
        return key.ToExtendedPublicKey(version);
    }
}
