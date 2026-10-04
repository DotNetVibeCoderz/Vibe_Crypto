# Dependency injection and RPC providers

[← Documentation](index.md) · [Bahasa Indonesia](../id/dependency-injection.md)

## Registering clients

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

Each client is a **keyed singleton** backed by a named `HttpClient` from `IHttpClientFactory`
(connection lifetime 5 minutes). Resolve it by concrete type or as `IChainClient`:

```csharp
app.MapGet("/balance/{address}", async (string address, [FromKeyedServices("eth")] EvmRpcClient eth) =>
    (await eth.GetBalanceAsync(new Address(address, ChainId.Ethereum))).ToString());

var registry = app.Services.GetRequiredService<IChainRegistry>();
foreach (var r in registry.Registrations) Console.WriteLine($"{r.Name}: {r.Chain.Name}");
```

Defaults point at **test networks** (Sepolia, Bitcoin testnet, Solana devnet, Westend, Cosmos Hub testnet) so a
misconfiguration never touches mainnet.

`WithResilience()` adds the standard `Microsoft.Extensions.Http.Resilience` pipeline: retries with backoff,
circuit breaker and timeouts.

## Keyed RPC providers

| Provider | Builder | Environment variable | How the key travels |
| --- | --- | --- | --- |
| Ankr | `UseAnkr(key, networks…)` | `CRYPTONET_ANKR_KEY` | URL path |
| dRPC | `UseDrpc(key, networks…)` | `CRYPTONET_DRPC_KEY` | `Drpc-Key` header |

Networks served: Ethereum, Sepolia, Hoodi, Polygon, Arbitrum, Optimism, Base, BNB Chain, Avalanche, Solana,
Solana devnet, Polkadot, Kusama (your plan may allow fewer). Bitcoin (Esplora) and Cosmos (LCD) use REST endpoints
and keep their configured URLs.

For each EVM, Solana and Substrate client the endpoint list is: providers in the order you added them, then the
network's public node. `JsonRpcClient` moves to the next endpoint when a provider **refuses access** — HTTP
401/402/403/429, "not allowed to access blockchain", "free plan", "rate limit". Those requests were rejected
before execution, so a failover never submits a transaction twice; ordinary RPC errors (revert, nonce too low) are
returned unchanged.

```csharp
builder.Services.AddCryptoNet(c => c
    .UseAnkr(builder.Configuration["Ankr:ApiKey"]!)
    .UseDrpc(builder.Configuration["Drpc:ApiKey"]!, "ethereum", "polygon", "kusama")
    .AddEvm("eth", o => o.Network = EvmChain.Ethereum));
```

Keep keys in user-secrets (`dotnet user-secrets set Ankr:ApiKey …`), environment variables or a vault. When a
provider is configured, Crypto.Net removes the HTTP loggers from those clients so URLs containing keys are never
logged. `RpcEndpoint.ToString()` always redacts.

Outside DI, `ChainCatalog.CreateClient(ChainCatalog.Get("polygon"))` builds a client from the environment
variables (`CRYPTONET_ANKR_NETWORKS` / `CRYPTONET_DRPC_NETWORKS` restrict providers to a comma-separated list).
The CLI and the Gallery use this path.
