using Crypto.Net.Bitcoin;
using Crypto.Net.Core;
using Crypto.Net.Cosmos;
using Crypto.Net.Evm;
using Crypto.Net.Polkadot;
using Crypto.Net.Solana;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crypto.Net.Extensions;

/// <summary>A registered chain client: its DI key and network.</summary>
public sealed record ChainRegistration(string Name, IChain Chain, Type ClientType);

/// <summary>Lists every chain client registered through <see cref="CryptoNetBuilder"/>.</summary>
public interface IChainRegistry
{
    IReadOnlyList<ChainRegistration> Registrations { get; }
    IChainClient GetClient(string name);
}

internal sealed class ChainRegistry(IServiceProvider provider, IEnumerable<ChainRegistration> registrations) : IChainRegistry
{
    public IReadOnlyList<ChainRegistration> Registrations { get; } = registrations.ToList();
    public IChainClient GetClient(string name) => provider.GetRequiredKeyedService<IChainClient>(name);
}

/// <summary>
/// Fluent registration of multi-chain clients. Each client is a keyed singleton (resolve it with
/// <c>GetRequiredKeyedService&lt;EvmRpcClient&gt;("eth")</c> or as <see cref="IChainClient"/>) backed by a named
/// <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/>.
/// </summary>
public sealed class CryptoNetBuilder
{
    private bool _resilience;
    private readonly List<(RpcProvider Provider, string Key, HashSet<string>? Networks)> _providers = [];

    public CryptoNetBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    /// <summary>Adds the standard resilience pipeline (retry, circuit breaker, timeouts) to every chain HttpClient.</summary>
    public CryptoNetBuilder WithResilience()
    {
        _resilience = true;
        return this;
    }

    /// <summary>
    /// Routes EVM, Solana and Substrate clients without an explicit endpoint through a keyed provider
    /// (<see cref="RpcProviders.Ankr"/>, <see cref="RpcProviders.Drpc"/>). Providers are tried in the order they
    /// are added, then the public node. Optionally limit a provider to <paramref name="networks"/>
    /// (catalog keys such as <c>ethereum</c>, <c>polygon</c>).
    /// </summary>
    public CryptoNetBuilder UseRpcProvider(RpcProvider provider, string apiKey, params string[] networks)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _providers.Add((provider, apiKey.Trim(), networks.Length == 0 ? null : new HashSet<string>(networks, StringComparer.OrdinalIgnoreCase)));
        return this;
    }

    public CryptoNetBuilder UseAnkr(string apiKey, params string[] networks) => UseRpcProvider(RpcProviders.Ankr, apiKey, networks);

    public CryptoNetBuilder UseDrpc(string apiKey, params string[] networks) => UseRpcProvider(RpcProviders.Drpc, apiKey, networks);

    private IReadOnlyList<RpcEndpoint> Endpoints(IChain chain, string? explicitUrl, string publicUrl)
    {
        if (explicitUrl is not null) return [new RpcEndpoint(explicitUrl)];
        var list = new List<RpcEndpoint>();
        foreach (var (provider, key, networks) in _providers)
        {
            if ((networks is null || networks.Contains(chain.Id.Value)) && provider.GetEndpoint(chain, key) is { } endpoint)
                list.Add(endpoint);
        }
        list.Add(new RpcEndpoint(publicUrl, null, "public"));
        return list;
    }

    private string HttpClientName(string name)
    {
        string clientName = $"Crypto.Net:{name}";
        var http = Services.AddHttpClient(clientName, c => c.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        if (_resilience) http.AddStandardResilienceHandler();
        // Provider URLs can carry API keys (Ankr puts the key in the path); never write them to logs.
        if (_providers.Count > 0) http.RemoveAllLoggers();
        return clientName;
    }

    private CryptoNetBuilder Register<TClient>(string name, IChain chain, Func<HttpClient, TClient> factory) where TClient : class, IChainClient
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string clientName = HttpClientName(name);
        Services.AddKeyedSingleton(name, (sp, _) => factory(sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName)));
        Services.AddKeyedSingleton<IChainClient>(name, (sp, _) => sp.GetRequiredKeyedService<TClient>(name));
        Services.AddSingleton(new ChainRegistration(name, chain, typeof(TClient)));
        return this;
    }

    public CryptoNetBuilder AddEvm(string name, Action<EvmOptions>? configure = null)
    {
        var o = new EvmOptions();
        configure?.Invoke(o);
        return Register(name, o.Network, http => new EvmRpcClient(o.Network, Endpoints(o.Network, o.RpcUrl, o.Network.DefaultRpcUrl), http));
    }

    public CryptoNetBuilder AddBitcoin(string name, Action<BitcoinOptions>? configure = null)
    {
        var o = new BitcoinOptions();
        configure?.Invoke(o);
        return Register(name, o.Network, http => new EsploraClient(o.Network, o.EsploraUrl, http));
    }

    public CryptoNetBuilder AddSolana(string name, Action<SolanaOptions>? configure = null)
    {
        var o = new SolanaOptions();
        configure?.Invoke(o);
        return Register(name, o.Cluster, http => new SolanaRpcClient(o.Cluster, Endpoints(o.Cluster, o.RpcUrl, o.Cluster.DefaultRpcUrl), http));
    }

    public CryptoNetBuilder AddPolkadot(string name, Action<PolkadotOptions>? configure = null)
    {
        var o = new PolkadotOptions();
        configure?.Invoke(o);
        return Register(name, o.Network, http => new PolkadotRpcClient(o.Network, Endpoints(o.Network, o.RpcUrl, o.Network.DefaultRpcUrl), http));
    }

    public CryptoNetBuilder AddCosmos(string name, Action<CosmosOptions>? configure = null)
    {
        var o = new CosmosOptions();
        configure?.Invoke(o);
        return Register(name, o.Network, http => new CosmosRestClient(o.Network, o.RestUrl, http));
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>Registers Crypto.Net chain clients. Defaults point at test networks.</summary>
    public static IServiceCollection AddCryptoNet(this IServiceCollection services, Action<CryptoNetBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new CryptoNetBuilder(services);
        configure(builder);
        services.TryAddSingleton<IChainRegistry>(sp => new ChainRegistry(sp, sp.GetServices<ChainRegistration>()));
        return services;
    }
}
