# Indexer

> Built by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/indexer.md)

Two packages turn chain data into a database you can query.

```bash
dotnet add package Crypto.Net.Indexer                         # indexers, checkpoints, hosting
dotnet add package Crypto.Net.Indexer.EntityFrameworkCore     # SQLite / PostgreSQL / SQL Server storage
```

| Piece | Role |
| --- | --- |
| `EvmEvent` (in Crypto.Net.Evm) | Parses `Transfer(address indexed from, address indexed to, uint256 value)` and decodes logs into named values |
| `EvmLogIndexer` | Pages through `eth_getLogs` from a checkpoint to the confirmed head, then keeps following new blocks |
| `BlockCrawler` | Calls your handler for every block height (or Solana slot) on any chain |
| `ICheckpointStore` | `InMemoryCheckpointStore`, `FileCheckpointStore`, `EfCheckpointStore<TContext>` |
| `EfEvmLogStore<TContext>` | Writes raw logs and ERC-20 transfers idempotently |

## Decoding events

```csharp
var swap = EvmEvent.Parse("Swap(address indexed sender, uint256 amount0In, uint256 amount1In, uint256 amount0Out, uint256 amount1Out, address indexed to)");
foreach (var log in await rpc.GetLogsAsync(pair, fromBlock, toBlock))
    if (swap.TryDecode(log, out var e))
        Console.WriteLine($"{e["sender"]} → {e["to"]}: {e["amount0In"]}");
```

Indexed dynamic parameters (`string`, `bytes`, arrays, tuples) are stored on-chain only as their keccak-256 hash.
They decode to that `byte[32]`.

## Indexing into a database (ASP.NET Core / Worker)

```csharp
builder.Services.AddDbContextFactory<CryptoNetIndexerDbContext>(o => o.UseSqlite("Data Source=indexer.db"));
builder.Services.AddCryptoNet(b => b.AddEvm("sepolia", o => o.Network = EvmChain.Sepolia));
builder.Services.AddEfCheckpointStore<CryptoNetIndexerDbContext>();
builder.Services.AddEvmLogIndexerToDatabase<CryptoNetIndexerDbContext>("sepolia", o =>
{
    o.Addresses = ["0x1c7D4B196Cb0C7B01d743Fbc6116a902379C7238"];   // USDC
    o.Events = [EvmEvent.Erc20Transfer];
    o.Confirmations = 5;
});
```

```sql
select [From], [To], Value from cnet_token_transfers where ChainId = 'sepolia' order by BlockNumber desc limit 10;
```

The `cnet-worker` template sets all of this up (`dotnet new cnet-worker`, then `--Watcher:Tokens:0 <token>`).

For your own tables, add `modelBuilder.ApplyCryptoNetIndexer()` to your context. Or implement `IEvmLogHandler` and
use `AddEvmLogIndexer<THandler>`.

## Guarantees

- **Order.** Batches arrive in block order, and logs are sorted by block and log index.
- **At-least-once.** The checkpoint advances only after the handler returns. A batch is redelivered if the handler
  throws or the process stops. `EfEvmLogStore` skips rows it already has, keyed by chain, transaction and log index.
- **Reorgs.** Only blocks `Confirmations` deep (default 12) are indexed. Pick a depth that suits the chain:
  Ethereum finalizes after about 64 blocks; rollups and Polygon need their own policy.
- **Provider limits.** When a provider refuses a range ("more than 10000 results", "block range too large", …),
  the indexer halves the range and grows it back once queries succeed again.
- **Resilience.** `RunAsync` logs RPC and network errors, waits `PollInterval`, and retries. Combine it with
  `UseDrpc`/`UseAnkr` for provider failover.

## Any chain: `BlockCrawler`

```csharp
builder.Services.AddCryptoNet(b => b.AddPolkadot("dot", o => o.Network = PolkadotChain.PolkadotAssetHub));
builder.Services.AddBlockCrawler("dot", o => o.Confirmations = 2, async (sp, height, ct) =>
{
    var client = sp.GetRequiredKeyedService<PolkadotRpcClient>("dot");
    var events = await client.GetEventsAsync(await client.GetBlockHashAsync(height, ct), ct);
    // store what you need…
});
```
