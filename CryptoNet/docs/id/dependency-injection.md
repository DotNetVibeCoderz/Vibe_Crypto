# Dependency injection dan penyedia RPC

[← Dokumentasi](index.md) · [English](../en/dependency-injection.md)

## Mendaftarkan client

```csharp
builder.Services.AddCryptoNet(c => c
    .AddEvm("eth", o => { o.Network = EvmChain.Ethereum; o.RpcUrl = builder.Configuration["Rpc:Ethereum"]; })
    .AddEvm("base", o => o.Network = EvmChain.Base)
    .AddBitcoin("btc", o => o.Network = BitcoinNetwork.Mainnet)
    .AddSolana("sol", o => o.Cluster = SolanaChain.MainnetBeta)
    .AddPolkadot("dot", o => o.Network = PolkadotChain.PolkadotAssetHub)
    .AddCosmos("atom", o => o.Network = CosmosChain.CosmosHub)
    .WithResilience());
```

Setiap client adalah **keyed singleton** yang memakai `HttpClient` bernama dari `IHttpClientFactory` (umur koneksi
5 menit). Ambil berdasarkan tipe konkret atau sebagai `IChainClient`:

```csharp
app.MapGet("/balance/{address}", async (string address, [FromKeyedServices("eth")] EvmRpcClient eth) =>
    (await eth.GetBalanceAsync(new Address(address, ChainId.Ethereum))).ToString());

var registry = app.Services.GetRequiredService<IChainRegistry>();
foreach (var r in registry.Registrations) Console.WriteLine($"{r.Name}: {r.Chain.Name}");
```

Nilai default mengarah ke **jaringan uji** (Sepolia, Bitcoin testnet, Solana devnet, Westend, Cosmos Hub testnet)
sehingga kesalahan konfigurasi tidak pernah menyentuh mainnet.

`WithResilience()` menambahkan pipeline standar `Microsoft.Extensions.Http.Resilience`: retry dengan backoff,
circuit breaker dan timeout.

## Penyedia RPC dengan API key

| Penyedia | Builder | Variabel lingkungan | Cara kunci dikirim |
| --- | --- | --- | --- |
| Ankr | `UseAnkr(key, networks…)` | `CRYPTONET_ANKR_KEY` | path URL |
| dRPC | `UseDrpc(key, networks…)` | `CRYPTONET_DRPC_KEY` | header `Drpc-Key` |

Jaringan yang dilayani: Ethereum, Sepolia, Hoodi, Polygon, Arbitrum, Optimism, Base, BNB Chain, Avalanche, Solana,
Solana devnet, Polkadot, Kusama (paket Anda mungkin lebih terbatas). Bitcoin (Esplora) dan Cosmos (LCD) memakai
endpoint REST dan tetap memakai URL yang dikonfigurasi.

Untuk setiap client EVM, Solana dan Substrate, daftar endpoint-nya adalah: penyedia sesuai urutan penambahan, lalu
node publik jaringan tersebut. `JsonRpcClient` berpindah ke endpoint berikutnya ketika penyedia **menolak akses** —
HTTP 401/402/403/429, "not allowed to access blockchain", "free plan", "rate limit". Permintaan seperti itu ditolak
sebelum dieksekusi, sehingga failover tidak pernah mengirim transaksi dua kali; error RPC biasa (revert, nonce too
low) dikembalikan apa adanya.

```csharp
builder.Services.AddCryptoNet(c => c
    .UseAnkr(builder.Configuration["Ankr:ApiKey"]!)
    .UseDrpc(builder.Configuration["Drpc:ApiKey"]!, "ethereum", "polygon", "kusama")
    .AddEvm("eth", o => o.Network = EvmChain.Ethereum));
```

Simpan kunci di user-secrets (`dotnet user-secrets set Ankr:ApiKey …`), variabel lingkungan atau vault. Ketika
penyedia dikonfigurasi, Crypto.Net menghapus logger HTTP dari client tersebut sehingga URL yang mengandung kunci tidak
pernah tercatat. `RpcEndpoint.ToString()` selalu menyamarkan kunci.

Di luar DI, `ChainCatalog.CreateClient(ChainCatalog.Get("polygon"))` membangun client dari variabel lingkungan
(`CRYPTONET_ANKR_NETWORKS` / `CRYPTONET_DRPC_NETWORKS` membatasi penyedia ke daftar yang dipisah koma). CLI dan
Gallery memakai jalur ini.
