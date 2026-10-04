# Crypto.Net — Development Plan & Roadmap

> **Author**: Gravicode Studios (led by Kang Fadhil)
> **Platform**: .NET 10 + Rust (C ABI)
> **Design source**: [solution-design.md](solution-design.md) · **Status tracking**: [Progress.md](Progress.md)

## Vision

One consistent .NET API for keys, signing, transactions and RPC across many blockchains, with the heavy
cryptography in a small Rust core and a byte-identical managed fallback, plus first-class developer experience:
CLI, interactive Gallery, bilingual documentation (English / Bahasa Indonesia) and NuGet packages.

## Release 1.0 — delivered

| Area | Scope | State |
| --- | --- | --- |
| Rust core | crypto + codec + FFI crates, ABI 1.1, `catch_unwind`, zeroize, 30 tests on official vectors | ✅ |
| Native layer | `LibraryImport` bindings, RID-aware loader, ABI check, Auto/Native/Managed backends, managed fallback for every primitive except sr25519 | ✅ |
| Core | `Amount`, `Address`, `ISigner`, `IChainClient`, JSON-RPC with provider failover, receipts, block watching | ✅ |
| Wallet | BIP-39, BIP-32, SLIP-10, substrate-bip39, xprv/xpub/zpub, Web3 v3 keystore, `SecureBuffer` | ✅ |
| EVM | Legacy/2930/1559 transactions, ABI, EIP-191/712, ERC-20, RPC client | ✅ |
| Bitcoin | P2PKH/P2SH/P2WPKH/P2WSH/P2TR, legacy/BIP-143/BIP-341 signing, coin selection, Esplora | ✅ |
| Solana | Message builder, System/SPL/ATA/Compute Budget/Memo, PDA, RPC with simulation | ✅ |
| Polkadot | SS58, SCALE, metadata V14/V15, metadata-driven extrinsics, balances | ✅ |
| Cosmos | Protobuf SIGN_MODE_DIRECT (bank, staking, distribution), REST client | ✅ |
| Extensions | `AddCryptoNet`, resilience, `ChainCatalog`, Ankr and dRPC providers, benchmark | ✅ |
| Tools | `cnet` CLI, Gallery web studio (EN/ID, light/dark, mobile), QuickStart sample | ✅ |
| Quality | 95 .NET tests (official vectors, backend parity, mocked RPC, opt-in live tests), 30 Rust tests | ✅ |
| Docs & distribution | README EN/ID, 12-page docs EN + ID, screenshots, NuGet icon, pack/publish scripts | ✅ |

## Next — 1.1 (hardening and reach)

- [x] CI (GitHub Actions): Rust build matrix for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`;
      pack with all native binaries; tests on Windows, Linux and macOS; tag-triggered NuGet publishing.
- [ ] `linux-musl-x64` (Alpine) native build.
- [ ] `cargo audit` / `cargo deny`, `dotnet list package --vulnerable`, SBOM in CI.
- [ ] Fuzzing (`cargo-fuzz`) for Base58, Bech32, RLP, SCALE and metadata decoders.
- [ ] BIP-341 official wallet test vectors (key-path spending suite) in the test project.
- [ ] Decode `System.Events` so Polkadot receipts report dispatch success.
- [ ] WebSocket subscriptions (`eth_subscribe`, `slotSubscribe`, `chain_subscribeNewHeads`).
- [ ] PSBT (BIP-174/370) import/export and multisig.
- [ ] Solana versioned (v0) transactions with address lookup tables.

## Later — ecosystem (from solution-design.md)

- [ ] `Crypto.Net.Indexer`: block crawler, log processor, checkpoints, EF Core/SQLite storage.
- [ ] Source generators: Solidity ABI → typed services, Anchor IDL → clients; `cnet gen`.
- [ ] `Crypto.Net.Analyzers` (CNET001–005: hard-coded secrets, secrets as `string`, `Amount` from `double`…).
- [ ] `dotnet new` templates (`cnet-console`, `cnet-webapi`, `cnet-worker`, `cnet-lib`, `cnet-test`).
- [x] Polyglot notebooks (EN/ID) — one per chain.
- [ ] Notebooks for indexer, benchmarks and security practices.
- [ ] VS Code extension "Crypto.Net Tools".
- [ ] ERC-721/1155, ENS, EIP-4337; SPL Token-2022 extensions; CosmWasm; Polkadot staking/nomination helpers.
- [ ] Hardware wallets (Ledger/Trezor) and WalletConnect v2 as `ISigner` implementations.
- [ ] Gallery desktop/mobile (Avalonia or MAUI) and WASM.
- [ ] External security audit before 2.0.
- [ ] Additional chains: TON, Sui/Aptos, NEAR, Cardano.

## Decisions taken

| Question (solution-design §19) | Decision |
| --- | --- |
| License | MIT |
| Binding generator | Hand-written `LibraryImport` signatures checked by backend-parity tests |
| WASM | Managed fallback for now |
| Package names | `Crypto.Net.*` (all sub-IDs available on nuget.org; `Crypto.Net` itself is taken and unused) |
| Gallery technology | ASP.NET Core + static front-end for 1.0 (cross-platform, screenshot-friendly); native desktop/mobile later |
