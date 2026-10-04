using Crypto.Net.Core;

namespace Crypto.Net.Solana;

/// <summary>Solana cluster descriptor.</summary>
public sealed record SolanaChain(ChainId Id, string Name, string DefaultRpcUrl, bool IsTestnet = false, string? ExplorerCluster = null) : IChain
{
    public string Symbol => "SOL";
    public int Decimals => 9;

    public static readonly SolanaChain MainnetBeta = new(ChainId.Solana, "Solana Mainnet Beta", "https://api.mainnet-beta.solana.com");
    public static readonly SolanaChain Devnet = new(ChainId.SolanaDevnet, "Solana Devnet", "https://api.devnet.solana.com", true, "devnet");
    public static readonly SolanaChain Testnet = new(ChainId.SolanaTestnet, "Solana Testnet", "https://api.testnet.solana.com", true, "testnet");
    public static readonly SolanaChain Localnet = new(new ChainId("solana-local"), "Solana Localnet", "http://127.0.0.1:8899", true, "custom");

    public static IReadOnlyList<SolanaChain> All { get; } = [MainnetBeta, Devnet, Testnet, Localnet];

    /// <summary>Solana Explorer URL for a transaction signature.</summary>
    public string ExplorerTxUrl(string signature) =>
        $"https://explorer.solana.com/tx/{signature}" + (ExplorerCluster is null ? "" : $"?cluster={ExplorerCluster}");
}
