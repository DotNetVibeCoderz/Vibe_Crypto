using Crypto.Net.Core;
using Crypto.Net.Evm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Crypto.Net.Indexer;

/// <summary>Runs an indexer for the lifetime of the host.</summary>
public sealed class IndexerHostedService(string name, Func<CancellationToken, Task> run, ILogger<IndexerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Indexer {Name} started", name);
        try
        {
            await run(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        logger.LogInformation("Indexer {Name} stopped", name);
    }
}

/// <summary>
/// Hosting integration. Clients are resolved by the name used with <c>AddCryptoNet(b =&gt; b.AddEvm("name"))</c>.
/// Without a registered <see cref="ICheckpointStore"/>, checkpoints go to <c>cryptonet-checkpoints.json</c> in the
/// content root, so restarts resume.
/// </summary>
public static class IndexerServiceCollectionExtensions
{
    /// <summary>Indexes EVM logs into <typeparamref name="THandler"/> (registered as a singleton if not yet registered).</summary>
    public static IServiceCollection AddEvmLogIndexer<THandler>(this IServiceCollection services, string clientName, Action<EvmLogIndexerOptions> configure)
        where THandler : class, IEvmLogHandler
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.TryAddSingleton<THandler>();
        return services.AddEvmLogIndexer(clientName, configure, sp => sp.GetRequiredService<THandler>());
    }

    public static IServiceCollection AddEvmLogIndexer(this IServiceCollection services, string clientName, Action<EvmLogIndexerOptions> configure,
        Func<IServiceProvider, IEvmLogHandler> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        var options = new EvmLogIndexerOptions { Name = clientName + "-logs" };
        configure(options);
        AddDefaultCheckpoints(services);
        services.AddSingleton<IHostedService>(sp =>
        {
            var indexer = new EvmLogIndexer(
                sp.GetRequiredKeyedService<EvmRpcClient>(clientName),
                sp.GetRequiredService<ICheckpointStore>(),
                handler(sp),
                options,
                sp.GetService<ILogger<EvmLogIndexer>>());
            return new IndexerHostedService(options.Name, indexer.RunAsync, sp.GetRequiredService<ILogger<IndexerHostedService>>());
        });
        return services;
    }

    /// <summary>Calls <paramref name="handleBlock"/> for every block of the client registered as <paramref name="clientName"/>.</summary>
    public static IServiceCollection AddBlockCrawler(this IServiceCollection services, string clientName, Action<BlockCrawlerOptions> configure,
        Func<IServiceProvider, ulong, CancellationToken, Task> handleBlock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new BlockCrawlerOptions { Name = clientName + "-blocks" };
        configure(options);
        AddDefaultCheckpoints(services);
        services.AddSingleton<IHostedService>(sp =>
        {
            var crawler = new BlockCrawler(
                sp.GetRequiredKeyedService<IChainClient>(clientName),
                sp.GetRequiredService<ICheckpointStore>(),
                (n, ct) => handleBlock(sp, n, ct),
                options,
                sp.GetService<ILogger<BlockCrawler>>());
            return new IndexerHostedService(options.Name, crawler.RunAsync, sp.GetRequiredService<ILogger<IndexerHostedService>>());
        });
        return services;
    }

    private static void AddDefaultCheckpoints(IServiceCollection services) =>
        services.TryAddSingleton<ICheckpointStore>(sp =>
        {
            string root = sp.GetService<IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;
            return new FileCheckpointStore(Path.Combine(root, "cryptonet-checkpoints.json"));
        });
}
