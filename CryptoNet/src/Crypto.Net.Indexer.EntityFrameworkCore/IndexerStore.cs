using System.Globalization;
using System.Numerics;
using Crypto.Net.Evm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crypto.Net.Indexer.EntityFrameworkCore;

/// <summary>Last processed block of an indexer.</summary>
public sealed class IndexerCheckpoint
{
    public string Name { get; set; } = "";
    public long Block { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A raw EVM log. Unique per (chain, transaction, log index).</summary>
public sealed class IndexedEvmLog
{
    public long Id { get; set; }
    public string ChainId { get; set; } = "";
    public long BlockNumber { get; set; }
    public string TransactionHash { get; set; } = "";
    public int LogIndex { get; set; }
    public string Address { get; set; } = "";
    public string? Topic0 { get; set; }
    public string? Topic1 { get; set; }
    public string? Topic2 { get; set; }
    public string? Topic3 { get; set; }
    public string Data { get; set; } = "0x";
    /// <summary>Name of the configured event the log decoded as, if any.</summary>
    public string? EventName { get; set; }
}

/// <summary>An ERC-20 <c>Transfer</c>. <see cref="Value"/> is the raw integer amount as a decimal string (up to 78 digits).</summary>
public sealed class TokenTransfer
{
    public long Id { get; set; }
    public string ChainId { get; set; } = "";
    public string Token { get; set; } = "";
    public long BlockNumber { get; set; }
    public string TransactionHash { get; set; } = "";
    public int LogIndex { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Value { get; set; } = "0";

    public BigInteger RawValue => BigInteger.Parse(Value, CultureInfo.InvariantCulture);
}

/// <summary>Ready-made context with the indexer tables. Use <see cref="IndexerModel.ApplyCryptoNetIndexer"/> to add them to your own context.</summary>
public class CryptoNetIndexerDbContext : DbContext
{
    public CryptoNetIndexerDbContext(DbContextOptions<CryptoNetIndexerDbContext> options) : base(options)
    {
    }

    /// <summary>For derived contexts that add their own tables.</summary>
    protected CryptoNetIndexerDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<IndexerCheckpoint> Checkpoints => Set<IndexerCheckpoint>();
    public DbSet<IndexedEvmLog> EvmLogs => Set<IndexedEvmLog>();
    public DbSet<TokenTransfer> TokenTransfers => Set<TokenTransfer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCryptoNetIndexer();
}

public static class IndexerModel
{
    /// <summary>Maps the indexer entities (tables <c>cnet_checkpoints</c>, <c>cnet_evm_logs</c>, <c>cnet_token_transfers</c>).</summary>
    public static ModelBuilder ApplyCryptoNetIndexer(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<IndexerCheckpoint>(e =>
        {
            e.ToTable("cnet_checkpoints");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(200);
        });
        modelBuilder.Entity<IndexedEvmLog>(e =>
        {
            e.ToTable("cnet_evm_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.ChainId).HasMaxLength(64);
            e.Property(x => x.TransactionHash).HasMaxLength(66);
            e.Property(x => x.Address).HasMaxLength(42);
            foreach (var t in new[] { nameof(IndexedEvmLog.Topic0), nameof(IndexedEvmLog.Topic1), nameof(IndexedEvmLog.Topic2), nameof(IndexedEvmLog.Topic3) })
                e.Property<string?>(t).HasMaxLength(66);
            e.Property(x => x.EventName).HasMaxLength(128);
            e.HasIndex(x => new { x.ChainId, x.TransactionHash, x.LogIndex }).IsUnique();
            e.HasIndex(x => new { x.ChainId, x.Address, x.BlockNumber });
            e.HasIndex(x => new { x.ChainId, x.Topic0, x.BlockNumber });
        });
        modelBuilder.Entity<TokenTransfer>(e =>
        {
            e.ToTable("cnet_token_transfers");
            e.HasKey(x => x.Id);
            e.Property(x => x.ChainId).HasMaxLength(64);
            e.Property(x => x.Token).HasMaxLength(42);
            e.Property(x => x.TransactionHash).HasMaxLength(66);
            e.Property(x => x.From).HasMaxLength(42);
            e.Property(x => x.To).HasMaxLength(42);
            e.Property(x => x.Value).HasMaxLength(80);
            e.HasIndex(x => new { x.ChainId, x.TransactionHash, x.LogIndex }).IsUnique();
            e.HasIndex(x => new { x.ChainId, x.From, x.BlockNumber });
            e.HasIndex(x => new { x.ChainId, x.To, x.BlockNumber });
            e.HasIndex(x => new { x.ChainId, x.Token, x.BlockNumber });
        });
        return modelBuilder;
    }
}

/// <summary>Checkpoints in the <c>cnet_checkpoints</c> table of <typeparamref name="TContext"/>.</summary>
public sealed class EfCheckpointStore<TContext>(IDbContextFactory<TContext> factory) : ICheckpointStore where TContext : DbContext
{
    public async ValueTask<ulong?> GetAsync(string indexer, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Set<IndexerCheckpoint>().AsNoTracking().FirstOrDefaultAsync(x => x.Name == indexer, ct).ConfigureAwait(false);
        return row is null ? null : (ulong)row.Block;
    }

    public async ValueTask SetAsync(string indexer, ulong block, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Set<IndexerCheckpoint>().FirstOrDefaultAsync(x => x.Name == indexer, ct).ConfigureAwait(false);
        if (row is null) db.Add(row = new IndexerCheckpoint { Name = indexer });
        row.Block = checked((long)block);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Options of <see cref="EfEvmLogStore{TContext}"/>.</summary>
public sealed class EvmLogStoreOptions
{
    /// <summary>Chain identifier stored with every row (e.g. <c>sepolia</c>), so one database can hold several chains.</summary>
    public string ChainId { get; set; } = "evm";
    public bool StoreRawLogs { get; set; } = true;
    public bool StoreErc20Transfers { get; set; } = true;
}

/// <summary>
/// Writes indexed logs and ERC-20 transfers. Inserts are idempotent (rows already present for the same chain,
/// transaction and log index are skipped), which makes redelivered batches harmless.
/// </summary>
public sealed class EfEvmLogStore<TContext>(IDbContextFactory<TContext> factory, EvmLogStoreOptions options) : IEvmLogHandler where TContext : DbContext
{
    public async Task HandleAsync(EvmLogBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Logs.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        long from = checked((long)batch.FromBlock), to = checked((long)batch.ToBlock);
        string chain = options.ChainId;
        var eventNames = batch.Events.ToDictionary(e => (e.Log.TransactionHash, e.Log.LogIndex), e => e.Name);

        if (options.StoreRawLogs)
        {
            var existing = (await db.Set<IndexedEvmLog>().AsNoTracking()
                    .Where(x => x.ChainId == chain && x.BlockNumber >= from && x.BlockNumber <= to)
                    .Select(x => new { x.TransactionHash, x.LogIndex })
                    .ToListAsync(ct).ConfigureAwait(false))
                .Select(x => (x.TransactionHash, x.LogIndex)).ToHashSet();
            foreach (var log in batch.Logs)
            {
                if (!existing.Add((log.TransactionHash, (int)log.LogIndex))) continue;
                db.Add(new IndexedEvmLog
                {
                    ChainId = chain,
                    BlockNumber = (long)log.BlockNumber,
                    TransactionHash = log.TransactionHash,
                    LogIndex = (int)log.LogIndex,
                    Address = log.Address,
                    Topic0 = log.Topics.ElementAtOrDefault(0),
                    Topic1 = log.Topics.ElementAtOrDefault(1),
                    Topic2 = log.Topics.ElementAtOrDefault(2),
                    Topic3 = log.Topics.ElementAtOrDefault(3),
                    Data = log.Data,
                    EventName = eventNames.GetValueOrDefault((log.TransactionHash, log.LogIndex)),
                });
            }
        }

        if (options.StoreErc20Transfers)
        {
            var existing = (await db.Set<TokenTransfer>().AsNoTracking()
                    .Where(x => x.ChainId == chain && x.BlockNumber >= from && x.BlockNumber <= to)
                    .Select(x => new { x.TransactionHash, x.LogIndex })
                    .ToListAsync(ct).ConfigureAwait(false))
                .Select(x => (x.TransactionHash, x.LogIndex)).ToHashSet();
            foreach (var log in batch.Logs)
            {
                if (!EvmEvent.Erc20Transfer.TryDecode(log, out var transfer)) continue;
                if (!existing.Add((log.TransactionHash, (int)log.LogIndex))) continue;
                db.Add(new TokenTransfer
                {
                    ChainId = chain,
                    Token = log.Address,
                    BlockNumber = (long)log.BlockNumber,
                    TransactionHash = log.TransactionHash,
                    LogIndex = (int)log.LogIndex,
                    From = (string)transfer!["from"]!,
                    To = (string)transfer["to"]!,
                    Value = ((BigInteger)transfer["value"]!).ToString(CultureInfo.InvariantCulture),
                });
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

public static class IndexerEfServiceCollectionExtensions
{
    /// <summary>
    /// Stores checkpoints in <typeparamref name="TContext"/> (register it with <c>AddDbContextFactory</c>). Call before
    /// <c>AddEvmLogIndexer</c> so the indexers pick it up.
    /// </summary>
    public static IServiceCollection AddEfCheckpointStore<TContext>(this IServiceCollection services) where TContext : DbContext
    {
        services.RemoveAll<ICheckpointStore>();
        services.AddSingleton<ICheckpointStore, EfCheckpointStore<TContext>>();
        return services;
    }

    /// <summary>Indexes EVM logs and ERC-20 transfers of client <paramref name="clientName"/> into <typeparamref name="TContext"/>.</summary>
    public static IServiceCollection AddEvmLogIndexerToDatabase<TContext>(this IServiceCollection services, string clientName,
        Action<EvmLogIndexerOptions> configure, Action<EvmLogStoreOptions>? store = null) where TContext : DbContext
    {
        var storeOptions = new EvmLogStoreOptions { ChainId = clientName };
        store?.Invoke(storeOptions);
        return services.AddEvmLogIndexer(clientName, configure,
            sp => new EfEvmLogStore<TContext>(sp.GetRequiredService<IDbContextFactory<TContext>>(), storeOptions));
    }
}
