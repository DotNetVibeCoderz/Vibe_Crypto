using Crypto.Net.Core;

namespace Crypto.Net.Cosmos;

/// <summary>Cosmos SDK network descriptor.</summary>
public sealed record CosmosChain(
    ChainId Id,
    string Name,
    string Symbol,
    int Decimals,
    string Bech32Prefix,
    string DefaultRestUrl,
    bool IsTestnet = false,
    string NetworkChainId = "",
    string Denom = "uatom",
    decimal GasPrice = 0.025m,
    string? ExplorerUrl = null
) : IChain
{
    public static readonly CosmosChain CosmosHub = new(ChainId.Cosmos, "Cosmos Hub", "ATOM", 6, "cosmos",
        "https://cosmos-rest.publicnode.com", false, "cosmoshub-4", "uatom", 0.025m, "https://www.mintscan.io/cosmos");

    public static readonly CosmosChain CosmosHubTestnet = new(ChainId.CosmosTestnet, "Cosmos Hub Testnet", "ATOM", 6, "cosmos",
        "https://rest.provider-sentry-01.ics-testnet.polypore.xyz", true, "provider", "uatom", 0.025m, "https://www.mintscan.io/cosmoshub-testnet");

    public static readonly CosmosChain Osmosis = new(ChainId.Osmosis, "Osmosis", "OSMO", 6, "osmo",
        "https://osmosis-rest.publicnode.com", false, "osmosis-1", "uosmo", 0.025m, "https://www.mintscan.io/osmosis");

    public static readonly CosmosChain OsmosisTestnet = new(new ChainId("osmosis-testnet"), "Osmosis Testnet", "OSMO", 6, "osmo",
        "https://lcd.osmotest5.osmosis.zone", true, "osmo-test-5", "uosmo", 0.025m);

    /// <summary>Local <c>simd</c> / <c>gaiad</c> node.</summary>
    public static readonly CosmosChain Local = new(new ChainId("cosmos-local"), "Local Cosmos", "STAKE", 6, "cosmos",
        "http://127.0.0.1:1317", true, "testing", "stake", 0.025m);

    public static IReadOnlyList<CosmosChain> All { get; } = [CosmosHub, CosmosHubTestnet, Osmosis, OsmosisTestnet, Local];
}
