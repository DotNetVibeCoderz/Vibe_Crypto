using Crypto.Net.Bitcoin;
using Crypto.Net.Core;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;

namespace Crypto.Net.Extensions;

/// <summary>Chain families supported by Crypto.Net.</summary>
public enum ChainFamily
{
    Evm,
    Bitcoin,
    Solana,
    Polkadot,
    Cosmos,
}

/// <summary>A network known to the catalog.</summary>
public sealed record CatalogEntry(string Key, ChainFamily Family, IChain Chain)
{
    public string Name => Chain.Name;
    public bool IsTestnet => Chain.IsTestnet;
}

/// <summary>A chain an address is valid for.</summary>
public sealed record AddressMatch(ChainFamily Family, string Format);

/// <summary>
/// Registry of every built-in network with helpers to create clients and validate addresses by name,
/// used by the <c>cnet</c> CLI and the Gallery.
/// </summary>
public static class ChainCatalog
{
    public static IReadOnlyList<CatalogEntry> Entries { get; } = Build();

    private static List<CatalogEntry> Build()
    {
        var list = new List<CatalogEntry>();
        list.AddRange(EvmChain.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Evm, c)));
        list.AddRange(BitcoinNetwork.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Bitcoin, c)));
        list.AddRange(SolanaChain.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Solana, c)));
        list.AddRange(PolkadotChain.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Polkadot, c)));
        list.AddRange(CosmosChain.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Cosmos, c)));
        return list;
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eth"] = "ethereum", ["evm"] = "ethereum", ["mainnet"] = "ethereum",
        ["btc"] = "bitcoin", ["tbtc"] = "bitcoin-testnet", ["testnet"] = "bitcoin-testnet", ["signet"] = "bitcoin-signet",
        ["sol"] = "solana", ["devnet"] = "solana-devnet",
        ["dot"] = "polkadot", ["ksm"] = "kusama", ["wnd"] = "westend",
        ["atom"] = "cosmoshub", ["cosmos"] = "cosmoshub", ["osmo"] = "osmosis",
        ["matic"] = "polygon", ["pol"] = "polygon", ["arb"] = "arbitrum", ["op"] = "optimism", ["bsc"] = "bnb", ["avax"] = "avalanche",
    };

    /// <summary>Finds a network by key (<c>sepolia</c>), alias (<c>eth</c>, <c>btc</c>, <c>sol</c>…) or display name.</summary>
    public static CatalogEntry? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = Aliases.TryGetValue(name.Trim(), out var alias) ? alias : name.Trim();
        return Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? Entries.FirstOrDefault(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    public static CatalogEntry Get(string name) =>
        Find(name) ?? throw new ArgumentException($"Unknown network '{name}'. Known: {string.Join(", ", Entries.Select(e => e.Key))}");

    /// <summary>
    /// Creates the right client for a network. Without an explicit <paramref name="endpoint"/>, JSON-RPC chains use
    /// the providers whose keys are set (<c>CRYPTONET_ANKR_KEY</c>, <c>CRYPTONET_DRPC_KEY</c>, see
    /// <see cref="RpcProviders"/>) and fail over to the network's public default.
    /// </summary>
    public static IChainClient CreateClient(CatalogEntry entry, string? endpoint = null, HttpClient? http = null) => entry.Chain switch
    {
        EvmChain c => new EvmRpcClient(c, Endpoints(c, endpoint, c.DefaultRpcUrl), http),
        SolanaChain c => new SolanaRpcClient(c, Endpoints(c, endpoint, c.DefaultRpcUrl), http),
        PolkadotChain c => new PolkadotRpcClient(c, Endpoints(c, endpoint, c.DefaultRpcUrl), http),
        BitcoinNetwork c => new EsploraClient(c, endpoint, http),
        CosmosChain c => new CosmosRestClient(c, endpoint, http),
        _ => throw new NotSupportedException(entry.Chain.GetType().Name),
    };

    private static IReadOnlyList<RpcEndpoint> Endpoints(IChain chain, string? endpoint, string publicUrl) =>
        endpoint is not null ? [new RpcEndpoint(endpoint)] : RpcProviders.FromEnvironment(chain, publicUrl);

    /// <summary>Validates an address for a specific network.</summary>
    public static bool IsValidAddress(CatalogEntry entry, string address) => entry.Chain switch
    {
        EvmChain => EvmAddress.IsValid(address),
        BitcoinNetwork n => BitcoinAddress.IsValid(address, n),
        SolanaChain => SolanaAddress.IsValid(address),
        PolkadotChain p => Ss58Address.IsValid(address, p.Ss58Prefix),
        CosmosChain c => CosmosAddress.IsValid(address, c.Bech32Prefix),
        _ => false,
    };

    /// <summary>Detects which chain families an address could belong to.</summary>
    public static IReadOnlyList<AddressMatch> DetectAddress(string address)
    {
        var matches = new List<AddressMatch>();
        if (string.IsNullOrWhiteSpace(address)) return matches;
        address = address.Trim();

        if (EvmAddress.IsValid(address))
            matches.Add(new(ChainFamily.Evm, address.Any(char.IsUpper) && address.Any(char.IsLower) ? "EIP-55 checksummed hex" : "hex (no checksum)"));

        foreach (var net in new[] { BitcoinNetwork.Mainnet, BitcoinNetwork.Testnet, BitcoinNetwork.Regtest })
        {
            if (BitcoinAddress.TryParse(address, net, out var info))
            {
                matches.Add(new(ChainFamily.Bitcoin, $"{info!.Type} on {net.Name}"));
                break;
            }
        }

        if (SolanaAddress.IsValid(address))
            matches.Add(new(ChainFamily.Solana, SolanaAddress.IsOnCurve(address) ? "ed25519 public key" : "program-derived address"));

        if (Ss58Address.IsValid(address))
        {
            var (prefix, _) = Ss58Address.Decode(address);
            var known = PolkadotChain.All.FirstOrDefault(c => c.Ss58Prefix == prefix);
            matches.Add(new(ChainFamily.Polkadot, $"SS58 prefix {prefix}" + (known is null ? "" : $" ({known.Name})")));
        }

        if (!address.StartsWith("bc1", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("tb1", StringComparison.OrdinalIgnoreCase)
            && CosmosAddress.IsValid(address, hrp: null))
            matches.Add(new(ChainFamily.Cosmos, $"Bech32 '{CosmosAddress.Decode(address).Hrp}'"));

        return matches;
    }
}
