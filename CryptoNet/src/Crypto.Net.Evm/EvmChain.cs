using Crypto.Net.Core;

namespace Crypto.Net.Evm;

/// <summary>EVM network descriptor: chain id, native symbol and a default public RPC endpoint.</summary>
public sealed record EvmChain(
    ChainId Id,
    string Name,
    string Symbol,
    ulong NumericChainId,
    string DefaultRpcUrl,
    bool IsTestnet = false,
    string? ExplorerUrl = null
) : IChain
{
    public int Decimals => 18;

    public static readonly EvmChain Ethereum = new(ChainId.Ethereum, "Ethereum", "ETH", 1, "https://ethereum-rpc.publicnode.com", ExplorerUrl: "https://etherscan.io");
    public static readonly EvmChain Sepolia = new(ChainId.Sepolia, "Sepolia", "ETH", 11155111, "https://ethereum-sepolia-rpc.publicnode.com", true, "https://sepolia.etherscan.io");
    public static readonly EvmChain Hoodi = new(ChainId.Hoodi, "Hoodi", "ETH", 560048, "https://ethereum-hoodi-rpc.publicnode.com", true, "https://hoodi.etherscan.io");

    /// <summary>Holesky was shut down in 2025; kept for existing configurations. Prefer <see cref="Hoodi"/>.</summary>
    [Obsolete("Holesky has been deprecated; use Hoodi")]
    public static readonly EvmChain Holesky = new(ChainId.Holesky, "Holesky", "ETH", 17000, "https://ethereum-holesky-rpc.publicnode.com", true, "https://holesky.etherscan.io");
    public static readonly EvmChain Polygon = new(ChainId.Polygon, "Polygon PoS", "POL", 137, "https://polygon-bor-rpc.publicnode.com", ExplorerUrl: "https://polygonscan.com");
    public static readonly EvmChain Arbitrum = new(ChainId.Arbitrum, "Arbitrum One", "ETH", 42161, "https://arb1.arbitrum.io/rpc", ExplorerUrl: "https://arbiscan.io");
    public static readonly EvmChain Optimism = new(ChainId.Optimism, "OP Mainnet", "ETH", 10, "https://mainnet.optimism.io", ExplorerUrl: "https://optimistic.etherscan.io");
    public static readonly EvmChain Base = new(ChainId.Base, "Base", "ETH", 8453, "https://mainnet.base.org", ExplorerUrl: "https://basescan.org");
    public static readonly EvmChain BnbChain = new(ChainId.BnbChain, "BNB Smart Chain", "BNB", 56, "https://bsc-dataseed.bnbchain.org", ExplorerUrl: "https://bscscan.com");
    public static readonly EvmChain Avalanche = new(ChainId.Avalanche, "Avalanche C-Chain", "AVAX", 43114, "https://api.avax.network/ext/bc/C/rpc", ExplorerUrl: "https://snowtrace.io");

    /// <summary>A local development node (Anvil / Hardhat), chain id 31337.</summary>
    public static readonly EvmChain Localhost = new(new ChainId("evm-local"), "Localhost", "ETH", 31337, "http://127.0.0.1:8545", true);

    public static IReadOnlyList<EvmChain> All { get; } = [Ethereum, Sepolia, Hoodi, Polygon, Arbitrum, Optimism, Base, BnbChain, Avalanche, Localhost];

    /// <summary>Looks up a known chain by its numeric id.</summary>
    public static EvmChain? FromChainId(ulong chainId) => All.FirstOrDefault(c => c.NumericChainId == chainId);
}
