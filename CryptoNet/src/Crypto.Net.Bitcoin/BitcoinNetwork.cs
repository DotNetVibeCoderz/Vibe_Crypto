using Crypto.Net.Core;

namespace Crypto.Net.Bitcoin;

/// <summary>Bitcoin address types supported by Crypto.Net.</summary>
public enum BitcoinAddressType
{
    /// <summary>Pay-to-PubKey-Hash, Base58 (<c>1…</c> / <c>m…</c>, <c>n…</c>). BIP-44.</summary>
    LegacyP2PKH,
    /// <summary>Pay-to-Script-Hash, Base58 (<c>3…</c> / <c>2…</c>).</summary>
    P2SH,
    /// <summary>Native SegWit v0 key hash, Bech32 (<c>bc1q…</c>). BIP-84.</summary>
    SegWitP2WPKH,
    /// <summary>Native SegWit v0 script hash, Bech32 (<c>bc1q…</c>, 32-byte program).</summary>
    SegWitP2WSH,
    /// <summary>Taproot key-path (BIP-86), Bech32m (<c>bc1p…</c>).</summary>
    TaprootP2TR,
}

/// <summary>Bitcoin network parameters.</summary>
public sealed record BitcoinNetwork(
    ChainId Id,
    string Name,
    string Bech32Hrp,
    byte P2PkhPrefix,
    byte P2ShPrefix,
    byte WifPrefix,
    uint Bip44CoinType,
    string DefaultEsploraUrl,
    bool IsTestnet = false,
    string? ExplorerUrl = null
) : IChain
{
    public string Symbol => IsTestnet ? "tBTC" : "BTC";
    public int Decimals => 8;

    public static readonly BitcoinNetwork Mainnet = new(ChainId.Bitcoin, "Bitcoin", "bc", 0x00, 0x05, 0x80, 0,
        "https://blockstream.info/api", ExplorerUrl: "https://mempool.space");

    public static readonly BitcoinNetwork Testnet = new(ChainId.BitcoinTestnet, "Bitcoin Testnet", "tb", 0x6f, 0xc4, 0xef, 1,
        "https://blockstream.info/testnet/api", true, "https://mempool.space/testnet");

    public static readonly BitcoinNetwork Signet = new(ChainId.BitcoinSignet, "Bitcoin Signet", "tb", 0x6f, 0xc4, 0xef, 1,
        "https://mempool.space/signet/api", true, "https://mempool.space/signet");

    public static readonly BitcoinNetwork Regtest = new(ChainId.BitcoinRegtest, "Bitcoin Regtest", "bcrt", 0x6f, 0xc4, 0xef, 1,
        "http://127.0.0.1:3002", true);

    public static IReadOnlyList<BitcoinNetwork> All { get; } = [Mainnet, Testnet, Signet, Regtest];
}
