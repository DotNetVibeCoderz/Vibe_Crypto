# Indexer

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/indexer.md)

Dua paket mengubah data chain menjadi database yang bisa di-query.

```bash
dotnet add package Crypto.Net.Indexer                         # indexer, checkpoint, hosting
dotnet add package Crypto.Net.Indexer.EntityFrameworkCore     # penyimpanan SQLite / PostgreSQL / SQL Server
```

| Komponen | Peran |
| --- | --- |
| `EvmEvent` (di Crypto.Net.Evm) | Mem-parse `Transfer(address indexed from, address indexed to, uint256 value)` dan men-decode log menjadi nilai bernama |
| `EvmLogIndexer` | Membaca `eth_getLogs` per halaman dari checkpoint sampai head yang terkonfirmasi, lalu terus mengikuti blok baru |
| `BlockCrawler` | Memanggil handler Anda untuk setiap tinggi blok (atau slot Solana) di chain mana pun |
| `ICheckpointStore` | `InMemoryCheckpointStore`, `FileCheckpointStore`, `EfCheckpointStore<TContext>` |
| `EfEvmLogStore<TContext>` | Menyimpan log mentah dan transfer ERC-20 secara idempoten |

## Men-decode event

```csharp
var swap = EvmEvent.Parse("Swap(address indexed sender, uint256 amount0In, uint256 amount1In, uint256 amount0Out, uint256 amount1Out, address indexed to)");
foreach (var log in await rpc.GetLogsAsync(pair, fromBlock, toBlock))
    if (swap.TryDecode(log, out var e))
        Console.WriteLine($"{e["sender"]} → {e["to"]}: {e["amount0In"]}");
```

Parameter indexed yang dinamis (`string`, `bytes`, array, tuple) hanya tersimpan di chain sebagai hash keccak-256-nya.
Hasil decode-nya adalah `byte[32]` tersebut.

## Mengindeks ke database (ASP.NET Core / Worker)

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

Template `cnet-worker` sudah menyiapkan semua ini (`dotnet new cnet-worker`, lalu `--Watcher:Tokens:0 <token>`).
Untuk tabel milik Anda sendiri, tambahkan `modelBuilder.ApplyCryptoNetIndexer()` ke context Anda. Atau implementasikan
`IEvmLogHandler` dan pakai `AddEvmLogIndexer<THandler>`.

## Jaminan

- **Urutan.** Batch datang sesuai urutan blok, dan log diurutkan menurut blok dan indeks log.
- **At-least-once.** Checkpoint baru maju setelah handler selesai. Batch dikirim ulang jika handler melempar exception
  atau proses berhenti. `EfEvmLogStore` melewati baris yang sudah ada, dengan kunci chain, transaksi dan indeks log.
- **Reorg.** Hanya blok sedalam `Confirmations` (default 12) yang diindeks. Pilih kedalaman yang cocok untuk chain:
  Ethereum final setelah sekitar 64 blok; rollup dan Polygon perlu kebijakan sendiri.
- **Batas provider.** Jika provider menolak sebuah rentang ("more than 10000 results", "block range too large", …),
  indexer membagi dua rentangnya lalu memperbesarnya lagi setelah query kembali berhasil.
- **Ketahanan.** `RunAsync` mencatat error RPC dan jaringan, menunggu `PollInterval`, lalu mencoba lagi. Gabungkan
  dengan `UseDrpc`/`UseAnkr` untuk failover provider.

## Chain apa pun: `BlockCrawler`

```csharp
builder.Services.AddCryptoNet(b => b.AddPolkadot("dot", o => o.Network = PolkadotChain.PolkadotAssetHub));
builder.Services.AddBlockCrawler("dot", o => o.Confirmations = 2, async (sp, height, ct) =>
{
    var client = sp.GetRequiredKeyedService<PolkadotRpcClient>("dot");
    var events = await client.GetEventsAsync(await client.GetBlockHashAsync(height, ct), ct);
    // simpan yang Anda perlukan…
});
```
