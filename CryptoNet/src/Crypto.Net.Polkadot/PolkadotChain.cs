using Crypto.Net.Core;

namespace Crypto.Net.Polkadot;

/// <summary>Substrate network descriptor.</summary>
public sealed record PolkadotChain(
    ChainId Id,
    string Name,
    string Symbol,
    int Decimals,
    ushort Ss58Prefix,
    string DefaultRpcUrl,
    bool IsTestnet = false,
    string? ExplorerUrl = null
) : IChain
{
    public static readonly PolkadotChain Polkadot = new(ChainId.Polkadot, "Polkadot", "DOT", 10, 0, "https://rpc.polkadot.io", ExplorerUrl: "https://polkadot.subscan.io");
    public static readonly PolkadotChain PolkadotAssetHub = new(new ChainId("polkadot-asset-hub"), "Polkadot Asset Hub", "DOT", 10, 0, "https://polkadot-asset-hub-rpc.polkadot.io", ExplorerUrl: "https://assethub-polkadot.subscan.io");
    public static readonly PolkadotChain Kusama = new(ChainId.Kusama, "Kusama", "KSM", 12, 2, "https://kusama-rpc.polkadot.io", ExplorerUrl: "https://kusama.subscan.io");
    public static readonly PolkadotChain KusamaAssetHub = new(new ChainId("kusama-asset-hub"), "Kusama Asset Hub", "KSM", 12, 2, "https://kusama-asset-hub-rpc.polkadot.io", ExplorerUrl: "https://assethub-kusama.subscan.io");
    public static readonly PolkadotChain Westend = new(ChainId.Westend, "Westend", "WND", 12, 42, "https://westend-rpc.polkadot.io", true, "https://westend.subscan.io");
    public static readonly PolkadotChain WestendAssetHub = new(new ChainId("westend-asset-hub"), "Westend Asset Hub", "WND", 12, 42, "https://westend-asset-hub-rpc.polkadot.io", true, "https://assethub-westend.subscan.io");
    public static readonly PolkadotChain Paseo = new(ChainId.Paseo, "Paseo", "PAS", 10, 0, "https://pas-rpc.stakeworld.io", true, "https://paseo.subscan.io");

    /// <summary>A local <c>substrate-node</c> / <c>polkadot --dev</c> instance.</summary>
    public static readonly PolkadotChain Local = new(new ChainId("substrate-local"), "Local Substrate", "UNIT", 12, 42, "http://127.0.0.1:9944", true);

    [Obsolete("Use Local")]
    public static PolkadotChain GenericSubstrate => Local;

    public static IReadOnlyList<PolkadotChain> All { get; } = [Polkadot, PolkadotAssetHub, Kusama, KusamaAssetHub, Westend, WestendAssetHub, Paseo, Local];
}
