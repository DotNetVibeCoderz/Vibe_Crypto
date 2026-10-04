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
| NuGet publish | ⏳ Waiting | Needs owner confirmation (versions are permanent) |
| CI matrix for other RIDs | ⏳ Planned | See PLAN.md 1.1 |

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
