using Crypto.Net.Wallet;
using Crypto.Net.Bitcoin;
using Crypto.Net.Core;
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

/// <summary>Chain families supported by Crypto.Net.</summary>
public enum ChainFamily
{
    Evm,
    Bitcoin,
    Solana,
    Polkadot,
    Cosmos,
    Near,
    Aptos,
    Sui,
    Ton,
    Cardano,
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
        list.AddRange(NearChain.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Near, c)));
        list.AddRange(AptosChain.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Aptos, c)));
        list.AddRange(SuiChain.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Sui, c)));
        list.AddRange(TonChain.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Ton, c)));
        list.AddRange(CardanoChain.All.Select(c => new CatalogEntry(c.Id.Value, ChainFamily.Cardano, c)));
        return list;
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eth"] = "ethereum", ["evm"] = "ethereum", ["mainnet"] = "ethereum",
        ["btc"] = "bitcoin", ["tbtc"] = "bitcoin-testnet", ["testnet"] = "bitcoin-testnet", ["signet"] = "bitcoin-signet",
        ["sol"] = "solana", ["devnet"] = "solana-devnet",
        ["dot"] = "polkadot", ["ksm"] = "kusama", ["wnd"] = "westend",
        ["atom"] = "cosmoshub", ["cosmos"] = "cosmoshub", ["osmo"] = "osmosis",
        ["apt"] = "aptos", ["ada"] = "cardano", ["preprod"] = "cardano-preprod",
        ["matic"] = "polygon", ["pol"] = "polygon", ["arb"] = "arbitrum", ["op"] = "optimism", ["bsc"] = "bnb", ["avax"] = "avalanche",
    };

    /// <summary>
    /// Derives the standard account for <paramref name="entry"/> from <paramref name="wallet"/>: BIP-44 for EVM and Cosmos,
    /// BIP-84 (native SegWit) for Bitcoin, Phantom's path for Solana, the root sr25519 key (or <c>//index</c>) for
    /// Substrate, SLIP-10 ed25519 paths for NEAR, Aptos, Sui and TON (a W5 wallet), and CIP-1852 for Cardano. Dispose the returned account to wipe its private key.
    /// </summary>
    public static IAccount DeriveAccount(HdWallet wallet, CatalogEntry entry, uint index = 0)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Chain switch
        {
            EvmChain evm => wallet.GetEvmAccount(index, evm),
            BitcoinNetwork btc => wallet.GetBitcoinAccount(BitcoinAddressType.SegWitP2WPKH, index, btc),
            SolanaChain sol => wallet.GetSolanaAccount(index, sol),
            PolkadotChain dot => wallet.GetPolkadotAccount(index == 0 ? "" : $"//{index}", dot),
            CosmosChain cosmos => wallet.GetCosmosAccount(index, cosmos),
            NearChain near => wallet.GetNearAccount(index, near),
            AptosChain aptos => wallet.GetAptosAccount(index, aptos),
            SuiChain sui => wallet.GetSuiAccount(index, sui),
            TonChain ton => wallet.GetTonAccount(index, ton),
            CardanoChain ada => wallet.GetCardanoAccount(0, index, ada),
            _ => throw new NotSupportedException($"Unsupported chain {entry.Chain.Name}"),
        };
    }

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
        NearChain c => new NearRpcClient(c, Endpoints(c, endpoint, c.DefaultRpcUrl), http),
        AptosChain c => new AptosRestClient(c, endpoint, http),
        SuiChain c => new SuiGraphQlClient(c, endpoint, http),
        TonChain c => new TonCenterClient(c, endpoint, http),
        CardanoChain c => new KoiosClient(c, endpoint, http),
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
        NearChain => NearAddress.IsValidAccountId(address),
        AptosChain => AptosAddress.IsValid(address),
        SuiChain => SuiAddress.IsValid(address),
        TonChain t => IsValidTon(address, t),
        CardanoChain c => IsValidCardano(address, c),
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

        bool cardano = address.StartsWith("addr", StringComparison.Ordinal) || address.StartsWith("stake", StringComparison.Ordinal);
        if (!cardano && !address.StartsWith("bc1", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("tb1", StringComparison.OrdinalIgnoreCase)
            && CosmosAddress.IsValid(address, hrp: null))
            matches.Add(new(ChainFamily.Cosmos, $"Bech32 '{CosmosAddress.Decode(address).Hrp}'"));

        if (address.Length == 66 && address.StartsWith("0x", StringComparison.Ordinal) && address[2..].All(Uri.IsHexDigit))
        {
            matches.Add(new(ChainFamily.Aptos, "32-byte account address"));
            matches.Add(new(ChainFamily.Sui, "32-byte account address"));
        }

        if (NearAddress.IsValidAccountId(address) && (NearAddress.IsImplicit(address) || address.EndsWith(".near", StringComparison.Ordinal) || address.EndsWith(".testnet", StringComparison.Ordinal)))
            matches.Add(new(ChainFamily.Near, NearAddress.IsImplicit(address) ? "implicit account" : "named account"));

        if (TonAddress.IsValid(address))
        {
            var (_, bounceable, testOnly) = TonAddress.ParseWithFlags(address);
            matches.Add(new(ChainFamily.Ton, bounceable is null ? "raw address" : $"{(bounceable.Value ? "bounceable" : "non-bounceable")}{(testOnly == true ? ", test-only" : "")}"));
        }

        if (CardanoAddress.IsValid(address))
            matches.Add(new(ChainFamily.Cardano, cardano ? (address.StartsWith("stake", StringComparison.Ordinal) ? "reward (stake) address" : "Shelley address") : "Byron address"));

        return matches;
    }

    private static bool IsValidTon(string address, TonChain network)
    {
        if (!TonAddress.IsValid(address)) return false;
        var (_, _, testOnly) = TonAddress.ParseWithFlags(address);
        return testOnly is null || testOnly == network.IsTestnet;
    }

    private static bool IsValidCardano(string address, CardanoChain network)
    {
        if (!CardanoAddress.IsValid(address)) return false;
        byte[] raw = CardanoAddress.ToBytes(address);
        return (raw[0] >> 4) == 8 || (raw[0] & 0x0f) == network.NetworkId;
    }
}
