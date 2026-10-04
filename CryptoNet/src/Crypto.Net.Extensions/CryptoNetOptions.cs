using Crypto.Net.Bitcoin;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;

namespace Crypto.Net.Extensions;

public sealed class EvmOptions
{
    public EvmChain Network { get; set; } = EvmChain.Sepolia;
    /// <summary>Defaults to <see cref="EvmChain.DefaultRpcUrl"/>.</summary>
    public string? RpcUrl { get; set; }
}

public sealed class BitcoinOptions
{
    public BitcoinNetwork Network { get; set; } = BitcoinNetwork.Testnet;
    /// <summary>Defaults to <see cref="BitcoinNetwork.DefaultEsploraUrl"/>.</summary>
    public string? EsploraUrl { get; set; }
}

public sealed class SolanaOptions
{
    public SolanaChain Cluster { get; set; } = SolanaChain.Devnet;
    public string? RpcUrl { get; set; }
}

public sealed class PolkadotOptions
{
    public PolkadotChain Network { get; set; } = PolkadotChain.Westend;
    public string? RpcUrl { get; set; }
}

public sealed class CosmosOptions
{
    public CosmosChain Network { get; set; } = CosmosChain.CosmosHubTestnet;
    public string? RestUrl { get; set; }
}
