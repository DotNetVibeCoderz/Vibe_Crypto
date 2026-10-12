using Crypto.Net.Bitcoin;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;
using Crypto.Net.Aptos;
using Crypto.Net.Cardano;
using Crypto.Net.Near;
using Crypto.Net.Sui;
using Crypto.Net.Ton;

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

public sealed class NearOptions
{
    public NearChain Network { get; set; } = NearChain.Testnet;
    public string? RpcUrl { get; set; }
}

public sealed class AptosOptions
{
    public AptosChain Network { get; set; } = AptosChain.Testnet;
    public string? RestUrl { get; set; }
    /// <summary>Optional Aptos Build API key.</summary>
    public string? ApiKey { get; set; }
}

public sealed class SuiOptions
{
    public SuiChain Network { get; set; } = SuiChain.Testnet;
    public string? GraphQlUrl { get; set; }
}

public sealed class TonOptions
{
    public TonChain Network { get; set; } = TonChain.Testnet;
    /// <summary>TON Center API root (containing <c>/v2</c> and <c>/v3</c>).</summary>
    public string? ApiUrl { get; set; }
    /// <summary>Optional TON Center API key.</summary>
    public string? ApiKey { get; set; }
}

public sealed class CardanoOptions
{
    public CardanoChain Network { get; set; } = CardanoChain.Preprod;
    public string? KoiosUrl { get; set; }
    /// <summary>Optional Koios bearer token.</summary>
    public string? ApiToken { get; set; }
}
