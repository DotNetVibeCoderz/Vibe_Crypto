using Crypto.Net.Core;
using Crypto.Net.Extensions;
using Microsoft.Extensions.Options;

namespace CnetWorker;

public sealed class WatcherOptions
{
    public string Network { get; set; } = "CNET_DEFAULT_NETWORK";
    public int PollSeconds { get; set; } = 6;
    /// <summary>Addresses whose balance is logged when it changes.</summary>
    public List<string> Addresses { get; set; } = [];

    /// <summary>ERC-20 contracts whose transfers are indexed into SQLite (EVM networks only).</summary>
    public List<string> Tokens { get; set; } = [];

    public ulong Confirmations { get; set; } = 5;

    /// <summary>First block to index; default is the current head.</summary>
    public ulong? StartBlock { get; set; }
}

/// <summary>Follows the chain head and reports balance changes of the watched addresses.</summary>
public sealed class BlockWatcher(IOptions<WatcherOptions> options, IHttpClientFactory httpFactory, ILogger<BlockWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var network = ChainCatalog.Get(o.Network);
        using var client = ChainCatalog.CreateClient(network, http: httpFactory.CreateClient(network.Key));
        var watched = o.Addresses.Where(a => ChainCatalog.IsValidAddress(network, a)).ToList();
        var balances = new Dictionary<string, Amount>();
        logger.LogInformation("Watching {Network} every {Seconds}s, {Count} address(es)", network.Name, o.PollSeconds, watched.Count);

        await foreach (var block in client.WatchBlocksAsync(TimeSpan.FromSeconds(o.PollSeconds), stoppingToken))
        {
            logger.LogInformation("Block {Number}", block.Number);
            foreach (string address in watched)
            {
                try
                {
                    var balance = await client.GetBalanceAsync(new Address(address, network.Chain.Id), stoppingToken);
                    if (!balances.TryGetValue(address, out var previous) || previous != balance)
                    {
                        logger.LogInformation("{Address}: {Balance} {Symbol}", address, balance, network.Chain.Symbol);
                        balances[address] = balance;
                    }
                }
                catch (RpcException ex)
                {
                    logger.LogWarning("Balance of {Address} failed: {Message}", address, ex.RpcMessage);
                }
            }
        }
    }
}
