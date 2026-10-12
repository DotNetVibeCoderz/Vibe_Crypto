# Crypto.Net — Progress Tracking

> **Author**: Gravicode Studios (led by Kang Fadhil)
> **Platform**: .NET 10 & Rust
> **Last updated**: 2026-10-05

## Dashboard

| Component | Status | Evidence |
| --- | --- | --- |
| Rust workspace (crypto, codec, ffi) | ✅ Done | `cargo test`: 30 passed; clippy clean |
| Native DLL `win-x64` | ✅ Done | `runtimes/win-x64/native/cryptonet.dll`, ABI 0x00010100 |
| Native bindings + managed fallback | ✅ Done | `BackendParityTests` (13) pass |
| Core & Wallet | ✅ Done | BIP-32/39, SLIP-10, keystore vectors pass |
| EVM adapter | ✅ Done | EIP-155/191/712, ABI vectors; Sepolia live read |
| Bitcoin adapter | ✅ Done | BIP-143 example, BIP-84/86 addresses; Esplora live read |
| Solana adapter | ✅ Done | Phantom address vector; devnet simulation accepted |
| Polkadot adapter | ✅ Done | Alice vectors; metadata parsed on Polkadot/Kusama/Asset Hubs; runtime prices our extrinsics |
| Cosmos adapter | ✅ Done | Address vector; mainnet `simulate` accepted our SignDoc |
| DI, ChainCatalog, Ankr/dRPC failover | ✅ Done | DI and failover tests; live provider tests |
| CLI `cnet` | ✅ Done | Smoke-tested every command |
| Gallery | ✅ Done | 7 pages, EN/ID, light/dark, mobile; screenshots |
| QuickStart sample | ✅ Done | Runs read-only against 5 networks |
| Documentation EN + ID | ✅ Done | README ×2, docs 12 + 12 pages |
| NuGet icon, metadata, scripts | ✅ Done | `assets/icon.png`, `scripts/pack.ps1`, `scripts/publish-nuget.ps1` |
| GitHub repository | ✅ Done | DotNetVibeCoderz/Vibe_Crypto, folder `CryptoNet/` (PR #1) |
| CI (6 native RIDs, tests on 3 OSes) | ✅ Done | `cryptonet-ci.yml` green on PR and `main` |
| NuGet publish 1.0.0 | ✅ Done | 11 packages on nuget.org via `cryptonet-publish.yml`; GitHub release `CryptoNet-v1.0.0` |
| Polyglot notebooks EN + ID | ✅ Done | 6 × 2 notebooks executed with dotnet-repl against nuget.org packages, 0 errors |
| 1.1 Taproot trees, PSBT v0/v2, multisig | ✅ Done | BIP-341, BIP-174/370 and Bitcoin Core sighash vectors |
| 1.1 Solana v0 + lookup tables | ✅ Done | Byte-identical to @solana/web3.js 1.98.0 |
| Crypto.Net.Ledger (Ethereum app) | ✅ Done | Replayed hw-app-eth sessions; CI E2E with the real app in Speculos |
| Crypto.Net.Near | ✅ Done | Byte-identical to near-api-js; live testnet reads |
| Crypto.Net.WalletConnect | ✅ Done | Relay session tests; interop both ways with @walletconnect/sign-client |
| Crypto.Net.Aptos | ✅ Done | Matches @aptos-labs/ts-sdk; live testnet simulation |
| Crypto.Net.Sui | ✅ Done | Matches @mysten/sui; GraphQL dry run returns the same digest |
| Crypto.Net.Ton | ✅ Done | Matches @ton/core and @ton/ton; TON Center fee emulation |
| Crypto.Net.Cardano | ✅ Done | Matches cardano-serialization-lib; builds from live preprod UTxOs |
| Gallery desktop + WebAssembly/PWA | ✅ Done | Desktop window verified on Windows; WASM verified in headless Chrome (wallet derived in-page) |
| VS Code extension Crypto.Net Tools | ✅ Done | Unit tests + integration test in an isolated VS Code; .vsix packaged in CI |
| Notebooks 07–09 (indexer, benchmarks, security) | ✅ Done | All 9 × 2 executed with dotnet-repl against locally packed 1.2 packages |
| Standards: ERC-721/1155, ENS, ERC-4337, Token-2022, CosmWasm, staking | ✅ Done | Cross-checked with viem, spl-token, cosmjs-types; live mainnet reads |
| Crypto.Net.Generators + `cnet gen` + cnet-contract-client | ✅ Done | Generated clients compiled and exercised in tests; live Sepolia USDC via template |
| Crypto.Net.Indexer (+ EF Core storage) | ✅ Done | Mock + SQLite tests; live Sepolia USDC; cnet-worker indexes into SQLite |
| Crypto.Net.Templates (5 templates) + `cnet new` | ✅ Done | `scripts/test-templates.sh` in CI: instantiate, build, test |
| Crypto.Net.Analyzers (CNET001–005) | ✅ Done | Roslyn tests incl. code fix; dogfooded on CLI, QuickStart, Gallery |
| 1.1 musl build, audit/deny/SBOM, fuzzing | ✅ Done | CI jobs `native-musl`, `test-musl`, `security`, `fuzz`; fuzzing found and fixed 3 decoder crashes |
| 1.1 WebSocket subscriptions | ✅ Done | Loopback server tests; live heads on Polkadot, Solana devnet, Ethereum |
| 1.1 Polkadot events + dispatch outcome | ✅ Done | Asset Hub fixtures; live on Polkadot, Asset Hub, Westend AH |

## Log

- **2026-10-04** — Project initiated from `solution-design.md`; initial prototype scaffolded.
- **2026-10-04** — Review of the prototype found incorrect SegWit/Taproot encoding, wrong SS58 checksum hash,
  broken managed RIPEMD-160/BLAKE2b, missing managed secp256k1/Ed25519, `panic = "abort"` at the FFI boundary,
  hex instead of base64 in Cosmos broadcasts and a non-standard keystore.
- **2026-10-04** — Rust core rewritten (Schnorr/Taproot, BIP-32, sr25519, scrypt, BLAKE2b-512, SegWit codec) with
  official vectors; FFI rebuilt with consistent buffer/size semantics; ABI 1.1.
- **2026-10-04** — Managed implementations written and verified byte-for-byte against Rust.
- **2026-10-04** — Core/Wallet/EVM/Bitcoin/Solana/Polkadot/Cosmos rewritten; Polkadot extrinsics now built from live
  metadata; Cosmos SIGN_MODE_DIRECT; full ABI codec; EIP-712.
- **2026-10-04** — 89 tests green; live checks against Polkadot, Kusama, Asset Hubs, Sepolia, Solana devnet,
  Bitcoin testnet and Cosmos Hub.
- **2026-10-05** — CLI (`System.CommandLine` + Spectre.Console) and Gallery (frontend-design: "security printing"
  identity with a guilloché seed fingerprint); dead default endpoints replaced (Holesky → Hoodi, Paseo, Cosmos and
  Osmosis testnets, Polygon).
- **2026-10-05** — Ankr and dRPC providers with access-denied failover; keys kept out of logs and source.
- **2026-10-05** — New NuGet icon, package metadata, pack/publish scripts, bilingual documentation and screenshots.
- **2026-10-05** — Moved into the `DotNetVibeCoderz/Vibe_Crypto` repository (`CryptoNet/`); CI builds native libraries for
  six RIDs (Windows CRT linked statically) and tests on Windows, Linux and macOS; all green on the first run.
- **2026-10-05** — Released 1.0.0: tag `CryptoNet-v1.0.0` published 11 packages (+ symbols) to nuget.org with the
  `NUGET_API_KEY` secret and created the GitHub release.
- **2026-10-05** — Added Polyglot notebooks for every chain (EN/ID), verified by executing them against nuget.org.
- **2026-10-12** — Bitcoin: full BIP-341 sighash, Taproot script trees and transaction parsing, checked against the
  official BIP-341 wallet vectors.
- **2026-10-12** — PSBT v0/v2 with all roles and multisig (P2SH, P2WSH, P2SH-P2WSH, sortedmulti). It passes every
  BIP-174/370 vector and Bitcoin Core's 500 legacy sighash cases.
- **2026-10-12** — Solana v0 messages, address lookup table program and message decoder; byte-identical to web3.js.
- **2026-10-12** — Polkadot: generic SCALE decoder, `System.Events` decoding, receipts with dispatch errors, storage reads.
- **2026-10-12** — WebSocket JSON-RPC with typed subscriptions for EVM, Solana and Substrate. The Sepolia publicnode WSS endpoint never answers subscriptions, so the live test uses mainnet endpoints.
- **2026-10-12** — Hardening: decoder fuzzing found and fixed 3 crash classes (metadata compact overflow, EVM decode overflow and nulls, RLP length/nesting). CI gains Alpine/musl, cargo audit/deny, the NuGet vulnerability scan, a CycloneDX SBOM and fuzz jobs.
- **2026-10-12** — Crypto.Net.Analyzers package (CNET001–005, CNET005 code fix), documented in EN/ID.
- **2026-10-12** — dotnet new templates and `cnet new`. Released 1.1.0.
- **2026-10-12** — Crypto.Net.Indexer and its EF Core storage. `EvmEvent` decoding. The worker template now indexes real Sepolia USDC transfers into SQLite.
- **2026-10-13** — Source generators for Solidity ABI and Anchor IDL, `cnet gen`, and the `cnet-contract-client` template (works live against Sepolia USDC).
- **2026-10-13** — Standards: ERC-721/1155, ENS, ERC-4337 (v0.6–0.8), Token-2022, CosmWasm/CW20, SCALE encoder plus staking and pools. Live checks: vitalik.eth, PYUSD, Osmosis contracts, and an Asset Hub fee quote.
- **2026-10-13** — Notebooks 07–09 (EN/ID). Every notebook was executed against freshly packed 1.2 packages.
- **2026-10-13** — VS Code extension: an integration test in a real VS Code checks activation, commands, the wei→ETH hover and the EIP-55 diagnostic.
- **2026-10-13** — Gallery refactored onto a host-neutral core. New desktop app (Photino) and WebAssembly/PWA build, with screenshots of both.
- **2026-10-13** — Ledger support, verified end to end against the real Ethereum app in Speculos. NEAR package, cross-checked with near-api-js.
- **2026-10-13** — Aptos, Sui, TON and Cardano packages. New native primitives: SHA3-256, BIP32-Ed25519 and extended-key signing (ABI 0x00010200). Fuzzing found an OverflowException in the CIP-20 reader, now fixed. All five new chains are in the catalog, the CLI and the Gallery.
- **2026-10-13** — Ledger Solana app: replayed hw-app-solana sessions; a CI job builds app-solana with Ledger's builder and runs the end-to-end test in Speculos.
- **2026-10-13** — Crypto.Net.Trezor: messages match trezorlib, and a CI job signs on Trezor's real firmware in the core emulator. The Ledger Solana app now uses the current off-chain message header (the end-to-end run showed the app rejects the legacy one).
- **2026-10-13** — Added Crypto.Net.WalletConnect and the X25519 primitive. Verified through Reown's relay using the owner's project id (kept out of the repo): a .NET app paired with the official sign-client wallet, and the official app paired with a .NET wallet.
- **2026-10-13** — Released 1.2.0.
