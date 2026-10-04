<p align="center">
  <img src="assets/icon.png" width="112" alt="Crypto.Net icon">
</p>

<h1 align="center">Crypto.Net</h1>

<p align="center">
  <strong>Multi-chain cryptography and blockchain clients for .NET 10, powered by a Rust core.</strong><br>
  Bitcoin · Ethereum &amp; EVM · Solana · Polkadot · Cosmos
</p>

<p align="center">
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <a href="docs/en/index.md">Documentation</a> ·
  <a href="docs/id/index.md">Dokumentasi</a> ·
  <a href="PLAN.md">Roadmap</a>
</p>

<p align="center">
  <a href="https://github.com/DotNetVibeCoderz/Vibe_Crypto/actions/workflows/cryptonet-ci.yml"><img alt="CI" src="https://github.com/DotNetVibeCoderz/Vibe_Crypto/actions/workflows/cryptonet-ci.yml/badge.svg"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="Rust" src="https://img.shields.io/badge/core-Rust-B7410E">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-0f8a63">
  <img alt="Tests" src="https://img.shields.io/badge/tests-95%20.NET%20%2B%2030%20Rust-0f8a63">
</p>

<p align="center"><em>Built by Gravicode Studios, led by Kang Fadhil.</em></p>

<p align="center">
  <img src="docs/assets/screenshots/gallery-wallet.png" alt="Crypto.Net Gallery: one recovery phrase deriving Ethereum, Bitcoin, Solana, Polkadot and Cosmos accounts" width="900">
</p>

---

## What it is

Crypto.Net gives .NET applications one consistent API for keys, addresses, signing, transactions and
RPC across five blockchain families. The heavy cryptography lives in a small Rust library built on the RustCrypto, dalek and schnorrkel crates
(`cryptonet`) called through a C ABI; every primitive also has a pure C# implementation that produces
**byte-identical** output, so the library keeps working on platforms where the native binary is absent.

- **Correct by construction.** Verified against published test vectors: BIP-32/39/84/86, BIP-143, BIP-340,
  SLIP-10, RFC 6979/7914/8032, EIP-55/155/191/712, Web3 Secret Storage, and Substrate's `//Alice`.
  The same mnemonic yields the same addresses as MetaMask, Phantom, Polkadot.js and Keplr.
- **Fast.** ECDSA, Schnorr and Ed25519 run 15–100× faster than managed code (see [benchmarks](#performance)).
- **Safe by default.** Secrets live in pinned, zeroized `SecureBuffer`s; RFC 6979 low-S signatures;
  transfers are simulated before signing; the Gallery never broadcasts.
- **Pragmatic.** Async, DI-friendly (`AddCryptoNet`), `IHttpClientFactory` + resilience, AOT-compatible native layer.

## Packages

| Package | What it contains |
| --- | --- |
| `Crypto.Net.Native` | Rust core + managed fallback: SHA-2, Keccak, BLAKE2b, RIPEMD-160, secp256k1 ECDSA/Schnorr/Taproot, Ed25519, sr25519, BIP-32, SLIP-10, PBKDF2, scrypt, Base58, Bech32/Bech32m |
| `Crypto.Net.Core` | `Amount` (exact), `Address`, `TxHash`, `ISigner`, `IChainClient`, JSON-RPC transport, receipt polling |
| `Crypto.Net.Wallet` | BIP-39 mnemonics, BIP-32/SLIP-10 HD keys, xprv/xpub/zpub, Web3 v3 keystores, `KeySigner` |
| `Crypto.Net.Evm` | EIP-55, Legacy/EIP-2930/EIP-1559 transactions, ABI codec, EIP-191/712, ERC-20, JSON-RPC client |
| `Crypto.Net.Bitcoin` | P2PKH/P2SH/P2WPKH/P2WSH/P2TR, SegWit serialization, legacy/BIP-143/BIP-341 signing, coin selection, Esplora |
| `Crypto.Net.Solana` | Transactions, System/SPL Token/ATA/Compute Budget/Memo, PDAs, Phantom & solana-cli keys, RPC |
| `Crypto.Net.Polkadot` | SS58, sr25519/ed25519 Substrate derivation, SCALE, runtime-metadata-driven extrinsics, RPC |
| `Crypto.Net.Cosmos` | Bech32 accounts, protobuf `SIGN_MODE_DIRECT` (bank, staking, distribution), REST client |
| `Crypto.Net.Extensions` | `AddCryptoNet()` keyed clients, resilience, `ChainCatalog`, dRPC provider, benchmark runner |
| `Crypto.Net.Testing` | `MockHttpMessageHandler` and official test vectors |
| `Crypto.Net.Cli` | The `cnet` dotnet tool |

```bash
dotnet add package Crypto.Net.Extensions   # pulls in every chain
dotnet tool install -g Crypto.Net.Cli      # the cnet command
```

## Quick start

```csharp
using Crypto.Net.Wallet;
using Crypto.Net.Evm;
using Crypto.Net.Bitcoin;
using Crypto.Net.Solana;
using Crypto.Net.Polkadot;
using Crypto.Net.Cosmos;

using var wallet = HdWallet.Generate(wordCount: 12);           // back up wallet.RevealMnemonic()

using var eth  = wallet.GetEvmAccount(0, EvmChain.Sepolia);    // m/44'/60'/0'/0/0
using var btc  = wallet.GetBitcoinAccount(BitcoinAddressType.TaprootP2TR, 0, BitcoinNetwork.Testnet);
using var sol  = wallet.GetSolanaAccount(0, SolanaChain.Devnet);
using var dot  = wallet.GetPolkadotAccount("", PolkadotChain.Westend);   // sr25519, Polkadot.js-compatible
using var atom = wallet.GetCosmosAccount(0, CosmosChain.CosmosHubTestnet);
```

Send on each chain (testnets shown):

```csharp
await using var evm = new EvmRpcClient(EvmChain.Sepolia);
await using var signer = eth.CreateSigner();
TxHash h1 = await evm.TransferAsync(signer, "0x…", Amount.FromEther(0.01m));   // nonce, EIP-1559 fees, gas, simulation
var receipt = await evm.WaitForReceiptAsync(h1);

await using var esplora = new EsploraClient(BitcoinNetwork.Testnet);
TxHash h2 = await esplora.TransferAsync(btc, "tb1q…", amountSats: 25_000);       // UTXOs, coin selection, Schnorr signing

await using var solana = new SolanaRpcClient(SolanaChain.Devnet);
TxHash h3 = await solana.TransferAsync(sol, "9xQe…", Amount.FromSol(0.1m));

await using var westend = new PolkadotRpcClient(PolkadotChain.Westend);
TxHash h4 = await westend.TransferAsync(dot, "5Grw…", Amount.Parse("0.5", 12));  // pallet/call indices from live metadata

await using var cosmos = new CosmosRestClient(CosmosChain.CosmosHubTestnet);
TxHash h5 = await cosmos.TransferAsync(atom, "cosmos1…", Amount.FromAtom(0.2m));
```

With dependency injection:

```csharp
builder.Services.AddCryptoNet(c => c
    .AddEvm("eth", o => o.Network = EvmChain.Sepolia)
    .AddSolana("sol", o => o.Cluster = SolanaChain.Devnet)
    .AddBitcoin("btc")
    .WithResilience());

var eth = app.Services.GetRequiredKeyedService<EvmRpcClient>("eth");
```

### Keyed RPC providers (Ankr, dRPC)

Public endpoints are rate-limited. Add API keys and EVM, Solana and Substrate clients use them first,
failing over to the next provider, then to the public node, whenever a provider refuses a network or
rate-limits (requests that were *executed* are never retried elsewhere):

```csharp
builder.Services.AddCryptoNet(c => c
    .UseAnkr(builder.Configuration["Ankr:ApiKey"]!)
    .UseDrpc(builder.Configuration["Drpc:ApiKey"]!)
    .AddEvm("eth", o => o.Network = EvmChain.Ethereum));
```

The CLI, the Gallery and `ChainCatalog` read `CRYPTONET_ANKR_KEY` and `CRYPTONET_DRPC_KEY`
(optionally narrowed with `CRYPTONET_ANKR_NETWORKS=ethereum,polygon`). dRPC keys travel in a header;
Ankr keys are part of the URL, so Crypto.Net disables HTTP logging for keyed clients. Keep keys in
user-secrets or environment variables, never in source control.

## The `cnet` command line

```bash
cnet doctor                              # native library, ABI, RID, RPC provider
cnet wallet new --words 24               # phrase + addresses on every chain
cnet address validate <address>          # detects the chain, checks checksums
cnet convert 1.5 ether gwei              # exact unit conversion
cnet balance <address> -N sepolia        # live balance through the built-in clients
cnet sign message "hello" -m "<phrase>"  # EIP-191; `sign typed-data` for EIP-712
cnet keystore encrypt -k <hex>           # Web3 v3 keystore (geth / MetaMask)
cnet bench                               # Rust vs managed on this machine
```

<p align="center"><img src="docs/assets/screenshots/cli-wallet.png" alt="cnet wallet derive output" width="900"></p>

## The Gallery

An interactive studio (ASP.NET Core) for every feature: wallet derivation with a guilloché "fingerprint"
of the seed, address inspection, exact unit conversion, five signature schemes, keystores, live network
heads and a Rust-vs-managed benchmark — each with the C# that produces it. English and Bahasa Indonesia,
light and dark themes, testnet by default.

```bash
dotnet run --project samples/Crypto.Net.Gallery   # http://localhost:5080
```

| | |
| --- | --- |
| ![Sign and verify](docs/assets/screenshots/gallery-sign.png) | ![Live networks](docs/assets/screenshots/gallery-networks.png) |
| ![Benchmark](docs/assets/screenshots/gallery-benchmark.png) | ![Dark theme](docs/assets/screenshots/gallery-wallet-dark.png) |

## Performance

`cnet bench` on a Windows x64 laptop (operations per second, higher is better):

| Operation | Rust core | Managed C# | Speed-up |
| --- | ---: | ---: | ---: |
| Keccak-256 (256 B) | 752,495 | 20,150 | 37× |
| BLAKE2b-256 (256 B) | 2,370,230 | 82,408 | 29× |
| secp256k1 public key | 14,864 | 297 | 50× |
| ECDSA sign (RFC 6979) | 7,171 | 303 | 24× |
| ECDSA verify | 7,611 | 217 | 35× |
| Schnorr sign (BIP-340) | 3,813 | 254 | 15× |
| Ed25519 sign | 20,629 | 234 | 88× |
| Ed25519 verify | 21,127 | 200 | 106× |
| SHA-256 (256 B) | 541,470 | 510,103 | 1.1× |

SHA-2, HMAC and PBKDF2 are already hardware-accelerated by the OS crypto stack, so the default `Auto`
backend uses the platform for those and Rust for everything else. Force either engine with
`CryptoNative.UseBackend(...)` or `CRYPTONET_BACKEND=native|managed`.

## Architecture

```
 Apps:        cnet CLI · Gallery (ASP.NET Core) · your code
 DI:          Crypto.Net.Extensions   AddCryptoNet() · ChainCatalog · dRPC · resilience
 Chains:      .Evm  .Bitcoin  .Solana  .Polkadot  .Cosmos
 Wallet/Core: .Wallet (BIP-39/32, SLIP-10, keystore)  ·  .Core (Amount, ISigner, IChainClient, JSON-RPC)
 Native:      Crypto.Net.Native  → CryptoNative (Auto | Native | Managed) → P/Invoke or managed fallback
 ─────────────────────────── C ABI (cn_*, i32 status codes, catch_unwind) ───────────────────────────
 Rust:        cryptonet-ffi → cryptonet-crypto (k256, ed25519-dalek, schnorrkel, scrypt…) · cryptonet-codec
```

Details: [docs/en/architecture.md](docs/en/architecture.md).

## Build from source

Requirements: .NET 10 SDK, Rust (stable) for the native library. CI builds native binaries for Windows, Linux and
macOS (x64 and arm64); releases are published from a `CryptoNet-v*` tag.

```powershell
./scripts/build-native.ps1          # cargo test + release build, copies to runtimes/<rid>/native
dotnet test tests/Crypto.Net.Tests  # 95 tests (3 live-network tests run with CRYPTONET_LIVE_TESTS=1)
./scripts/pack.ps1                  # NuGet packages in ./artifacts
```

## Security

Crypto.Net has not had an external audit yet. Use testnets while you evaluate it, never commit mnemonics or
private keys, and read [docs/en/security.md](docs/en/security.md). Report vulnerabilities privately as
described in [SECURITY.md](SECURITY.md).

## License

MIT © 2026 Gravicode Studios. **Built by Gravicode Studios, led by Kang Fadhil.**
