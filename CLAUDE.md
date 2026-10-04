# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Crypto.Net is a multi-chain crypto library for .NET 10 (C# 13) with a Rust core (`rust/` → `cryptonet` C ABI library). Every primitive also has a managed C# implementation that must produce byte-identical output. Attribution used in docs and apps: "Built by Gravicode Studios, led by Kang Fadhil" / "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil".

Owner requirements (`CryptoNet.txt`):
- Documentation is bilingual. Any user-visible change updates both `docs/en` and `docs/id`, both READMEs, and `CHANGELOG.md`.
- Apps and samples get polished UI built with the `frontend-design` skill.
- `PLAN.md` holds the roadmap and `Progress.md` the dated log; keep both current.
- Repository: https://github.com/DotNetVibeCoderz/Vibe_Crypto (public, default branch `main`).
- NuGet publishing goes through `.github/workflows/publish.yml`, triggered by a `v*` tag or a manual run. It uses the `NUGET_API_KEY` repo secret. Publishing is irreversible, so confirm with the user first.
- The local fallback is `scripts/publish-nuget.ps1 -CredentialsFile C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt`.
- RPC API keys live outside the repo: `C:\Users\mifma\Documents\CodeSandbox\drpc.txt` and `ankr.txt`, formatted `label: <key>`. Pass them only through the `CRYPTONET_DRPC_KEY` and `CRYPTONET_ANKR_KEY` env vars. Never print a key or write one into the repo.

`solution-design.md` (in Indonesian) is the long-term spec. It describes more than 1.0 ships; check PLAN.md for what's done.

## Commands

```bash
# Rust (from rust/)
cargo test                         # 30 tests, official vectors
cargo test -p cryptonet-crypto     # one crate
cargo clippy --all-targets && cargo fmt --all
./scripts/build-native.ps1        # cargo test + build, copies into runtimes/<rid>/native (gitignored; CI builds all RIDs)

# .NET
dotnet build Crypto.Net.slnx
dotnet test tests/Crypto.Net.Tests
dotnet test tests/Crypto.Net.Tests --filter "FullyQualifiedName~EvmTests.Eip712_MailExample"
CRYPTONET_LIVE_TESTS=1 dotnet test tests/Crypto.Net.Tests --filter "FullyQualifiedName~Live"   # internet

dotnet run --project tools/Crypto.Net.Cli -- doctor        # cnet CLI
dotnet run --project samples/Crypto.Net.Gallery            # http://localhost:5080
dotnet run --project samples/Crypto.Net.QuickStart         # README code, read-only
./scripts/pack.ps1                                         # .nupkg into ./artifacts
```

CI: `.github/workflows/ci.yml` runs on push and PR.
- Builds Rust for win-x64/arm64, linux-x64/arm64 and osx-x64/arm64.
- Runs `cargo fmt --check` and `clippy -D warnings`.
- Runs the .NET tests on Windows, Linux and macOS.
- Packs packages containing all 6 native binaries.
- Windows DLLs link the CRT statically (`rust/.cargo/config.toml`).

Known gotchas:
- A running Gallery or CLI locks DLLs in `bin/`, so the next build fails with MSB3027. Stop the process first.
- Bash heredocs that contain apostrophes break in this environment. Write such files with the Write tool, or write a Python script to the scratchpad and run it.
- Headless Chrome won't render narrower than about 500px. For mobile screenshots, wrap the page in an iframe. Use the Gallery URL params `?theme=`, `?lang=`, `?still=1`, `?autorun=1`.

## Architecture

**Dependency order:**
- `Native` → `Core` → `Wallet` → chain packages (`Evm`, `Bitcoin`, `Solana`, `Polkadot`, `Cosmos`) → `Extensions`.
- `Extensions` is referenced by the CLI, the Gallery, QuickStart and the tests.
- `Testing` (mocks plus official `TestVectors`) depends on Core only.

**Native boundary:**
- `rust/crates/cryptonet-ffi/src/lib.rs` exports the `cn_*` functions.
  - They return `i32` codes; `*_verify` functions return 1, 0, or a negative code.
  - Variable-size outputs use `(out, cap, *written)` and return `CN_ERR_BUFFER_TOO_SMALL` with the required size.
  - Every entry point runs inside `catch_unwind`. The release profile must keep `panic = "unwind"`.
- `src/Crypto.Net.Native/NativeMethods.cs` mirrors those exports by hand. Keep the two in sync.
- `NativeLoader` checks the ABI version (`RequiredAbiVersion` = 0x00010100, same major).
- `CryptoNative` routes each call by `CryptoBackend`:
  - `Auto` uses Rust, except SHA-2/HMAC/PBKDF2, which go to the hardware-accelerated platform crypto.
  - `Native` forces Rust; `Managed` forces C#.
  - `UseBackend()` overrides per async flow (AsyncLocal).
  - Managed code lives in `Native/Managed/`. sr25519 is native-only.
- Adding a primitive: Rust implementation + vector → FFI export → `NativeMethods` → managed implementation → `CryptoNative` routing → a `BackendParityTests` case → bump the ABI minor.

**Chains:**
- Each chain package provides:
  - an address codec
  - a transaction builder
  - an account class holding its key in a `SecureBuffer`, with `CreateSigner()` returning a `KeySigner`/`ISigner`
  - an `HdWallet` extension (`GetEvmAccount`, `GetBitcoinAccount`, …)
  - an `IChainClient`
- EVM, Solana and Polkadot share `Core/JsonRpcClient`. It takes an ordered list of `RpcEndpoint`s and fails over only on access-denied errors (401/402/403/429, "free plan", "not allowed", …), never on execution errors.
- Bitcoin (Esplora) and Cosmos (LCD) are REST clients.
- Polkadot extrinsics are built from live runtime metadata (`RuntimeMetadata` parses V14/V15).
  - Signed extensions are encoded by identifier.
  - Unknown extensions are allowed only when they're zero-sized.
- Substrate keys come from mnemonic entropy (`Mnemonic.ToSubstrateSeed`), not the BIP-39 seed.

**Extensions:**
- `AddCryptoNet()` registers keyed singletons with named HttpClients.
  - `UseAnkr`/`UseDrpc` prepend provider endpoints.
  - When providers are set, it removes the HTTP loggers, because Ankr keys sit in the URL.
- `ChainCatalog` maps network keys (`sepolia`, `polkadot-asset-hub`, …) to chains, clients and address validation.
- `RpcProviders.FromEnvironment` reads `CRYPTONET_{ANKR,DRPC}_KEY` and `_NETWORKS`.
- `CryptoBenchmark` powers both `cnet bench` and the Gallery chart.

**Conventions:**
- `Directory.Build.props`:
  - net10.0, C# 13, nullable, unsafe allowed
  - XML docs, with CS1591/CS1573 suppressed
  - package metadata, plus the icon and README packed into every package
- `Amount` is always exact. It throws on precision loss; never go through `double`.
- Public endpoints change over time. When one dies, the `Live` tests catch it. Update the defaults in the `*Chain` records.
- Regenerate the icon with `python generate_icon.py`.
