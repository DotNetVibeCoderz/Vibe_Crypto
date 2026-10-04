namespace Crypto.Net.Core;

/// <summary>Stable string identifier of a network (e.g. <c>ethereum</c>, <c>solana-devnet</c>).</summary>
public readonly record struct ChainId(string Value)
{
    public static readonly ChainId Bitcoin = new("bitcoin");
    public static readonly ChainId BitcoinTestnet = new("bitcoin-testnet");
    public static readonly ChainId BitcoinSignet = new("bitcoin-signet");
    public static readonly ChainId BitcoinRegtest = new("bitcoin-regtest");
    public static readonly ChainId Ethereum = new("ethereum");
    public static readonly ChainId Sepolia = new("sepolia");
    public static readonly ChainId Hoodi = new("hoodi");
    public static readonly ChainId Holesky = new("holesky");
    public static readonly ChainId Polygon = new("polygon");
    public static readonly ChainId Arbitrum = new("arbitrum");
    public static readonly ChainId Optimism = new("optimism");
    public static readonly ChainId Base = new("base");
    public static readonly ChainId BnbChain = new("bnb");
    public static readonly ChainId Avalanche = new("avalanche");
    public static readonly ChainId Solana = new("solana");
    public static readonly ChainId SolanaDevnet = new("solana-devnet");
    public static readonly ChainId SolanaTestnet = new("solana-testnet");
    public static readonly ChainId Polkadot = new("polkadot");
    public static readonly ChainId Kusama = new("kusama");
    public static readonly ChainId Westend = new("westend");
    public static readonly ChainId Paseo = new("paseo");
    public static readonly ChainId Cosmos = new("cosmoshub");
    public static readonly ChainId CosmosTestnet = new("cosmoshub-testnet");
    public static readonly ChainId Osmosis = new("osmosis");

    public override string ToString() => Value;
}
