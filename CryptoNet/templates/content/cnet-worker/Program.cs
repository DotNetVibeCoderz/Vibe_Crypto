using CnetWorker;
using Crypto.Net.Evm;
using Crypto.Net.Extensions;
using Crypto.Net.Indexer.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
var section = builder.Configuration.GetSection("Watcher");
var watcher = section.Get<WatcherOptions>() ?? new WatcherOptions();
var network = ChainCatalog.Get(watcher.Network);

builder.Services.Configure<WatcherOptions>(section);
builder.Services.AddHttpClient();
builder.Services.AddHostedService<BlockWatcher>();

// ERC-20 indexer: on EVM networks, every Transfer of the configured token contracts goes into SQLite
// (tables cnet_token_transfers, cnet_evm_logs, cnet_checkpoints). Restarts resume from the checkpoint.
bool indexing = network.Chain is EvmChain && watcher.Tokens.Count > 0;
if (indexing)
{
    builder.Services.AddDbContextFactory<CryptoNetIndexerDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Indexer")));
    builder.Services.AddCryptoNet(b => b.AddEvm("chain", o => o.Network = (EvmChain)network.Chain));
    builder.Services.AddEfCheckpointStore<CryptoNetIndexerDbContext>();
    builder.Services.AddEvmLogIndexerToDatabase<CryptoNetIndexerDbContext>("chain", o =>
    {
        o.Name = $"{network.Key}-erc20";
        o.Addresses = watcher.Tokens;
        o.Events = [EvmEvent.Erc20Transfer];
        o.Confirmations = watcher.Confirmations;
        o.StartBlock = watcher.StartBlock;
    }, store => store.ChainId = network.Key);
}

var host = builder.Build();
if (indexing)
{
    // EnsureCreated keeps the sample self-contained; use EF Core migrations in production.
    await using var db = await host.Services.GetRequiredService<IDbContextFactory<CryptoNetIndexerDbContext>>().CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}
await host.RunAsync();
