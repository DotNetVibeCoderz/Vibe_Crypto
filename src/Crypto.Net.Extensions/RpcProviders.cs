using Crypto.Net.Core;

namespace Crypto.Net.Extensions;

/// <summary>A hosted RPC provider that serves several networks with one API key.</summary>
public sealed class RpcProvider
{
    private readonly IReadOnlyDictionary<string, string> _slugs;
    private readonly Func<string, string, RpcEndpoint> _build;

    internal RpcProvider(string name, string environmentVariable, IReadOnlyDictionary<string, string> slugs, Func<string, string, RpcEndpoint> build)
    {
        Name = name;
        EnvironmentVariable = environmentVariable;
        _slugs = slugs;
        _build = build;
    }

    public string Name { get; }

    /// <summary>Environment variable holding the API key, e.g. <c>CRYPTONET_ANKR_KEY</c>.</summary>
    public string EnvironmentVariable { get; }

    /// <summary>Optional comma-separated list restricting which networks use this provider (<c>…_NETWORKS</c>).</summary>
    public string NetworksVariable => EnvironmentVariable.Replace("_KEY", "_NETWORKS", StringComparison.Ordinal);

    /// <summary>Crypto.Net network keys the provider can serve (your plan may allow fewer).</summary>
    public IReadOnlyCollection<string> Networks => (IReadOnlyCollection<string>)_slugs.Keys;

    public bool Serves(IChain chain) => _slugs.ContainsKey(chain.Id.Value);

    /// <summary>Endpoint for <paramref name="chain"/>, or <c>null</c> when this provider does not serve it.</summary>
    public RpcEndpoint? GetEndpoint(IChain chain, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return _slugs.TryGetValue(chain.Id.Value, out var slug) ? _build(slug, apiKey.Trim()) : null;
    }

    public string? ApiKeyFromEnvironment
    {
        get
        {
            string? key = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
    }

    internal bool AllowedByEnvironment(IChain chain)
    {
        string? only = Environment.GetEnvironmentVariable(NetworksVariable);
        return string.IsNullOrWhiteSpace(only) ||
               only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(chain.Id.Value, StringComparer.OrdinalIgnoreCase);
    }

    public override string ToString() => Name;
}

/// <summary>
/// Built-in API-key providers for the JSON-RPC chains (EVM, Solana, Substrate). Keep keys out of source control —
/// pass them from configuration, user-secrets or environment variables. Clients built from these endpoints fail over
/// to the next provider, and finally to the public node, when a provider refuses a network or rate-limits.
/// </summary>
public static class RpcProviders
{
    /// <summary>Ankr (<c>https://rpc.ankr.com/{chain}/{key}</c>). The key travels in the URL path.</summary>
    public static RpcProvider Ankr { get; } = new("Ankr", "CRYPTONET_ANKR_KEY",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ethereum"] = "eth", ["sepolia"] = "eth_sepolia", ["hoodi"] = "eth_hoodi",
            ["polygon"] = "polygon", ["arbitrum"] = "arbitrum", ["optimism"] = "optimism", ["base"] = "base",
            ["bnb"] = "bsc", ["avalanche"] = "avalanche",
            ["solana"] = "solana", ["solana-devnet"] = "solana_devnet",
            ["polkadot"] = "polkadot", ["kusama"] = "kusama",
        },
        (slug, key) => new RpcEndpoint($"https://rpc.ankr.com/{slug}/{Uri.EscapeDataString(key)}", null, "Ankr"));

    /// <summary>dRPC (<c>https://lb.drpc.live/{chain}</c>). The key travels in the <c>Drpc-Key</c> header, not the URL.</summary>
    public static RpcProvider Drpc { get; } = new("dRPC", "CRYPTONET_DRPC_KEY",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ethereum"] = "ethereum", ["sepolia"] = "sepolia", ["hoodi"] = "hoodi",
            ["polygon"] = "polygon", ["arbitrum"] = "arbitrum", ["optimism"] = "optimism", ["base"] = "base",
            ["bnb"] = "bsc", ["avalanche"] = "avalanche",
            ["solana"] = "solana", ["solana-devnet"] = "solana-devnet",
            ["polkadot"] = "polkadot", ["kusama"] = "kusama",
        },
        (slug, key) => new RpcEndpoint($"https://lb.drpc.live/{slug}", new Dictionary<string, string> { ["Drpc-Key"] = key }, "dRPC"));

    /// <summary>Providers in failover order.</summary>
    public static IReadOnlyList<RpcProvider> All { get; } = [Ankr, Drpc];

    /// <summary>
    /// Endpoints for <paramref name="chain"/> from every provider whose key is set in the environment
    /// (respecting <c>…_NETWORKS</c> filters), followed by <paramref name="publicFallback"/>.
    /// Returns an empty list when no provider applies and no fallback is given.
    /// </summary>
    public static IReadOnlyList<RpcEndpoint> FromEnvironment(IChain chain, string? publicFallback = null)
    {
        var list = new List<RpcEndpoint>();
        foreach (var provider in All)
        {
            string? key = provider.ApiKeyFromEnvironment;
            if (key is not null && provider.AllowedByEnvironment(chain) && provider.GetEndpoint(chain, key) is { } endpoint)
                list.Add(endpoint);
        }
        if (publicFallback is not null) list.Add(new RpcEndpoint(publicFallback, null, "public"));
        return list;
    }

    /// <summary>Names of providers configured through environment variables.</summary>
    public static IReadOnlyList<string> ConfiguredInEnvironment() =>
        All.Where(p => p.ApiKeyFromEnvironment is not null).Select(p => p.Name).ToList();
}
