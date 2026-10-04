# Architecture

[← Documentation](index.md) · [Bahasa Indonesia](../id/arsitektur.md)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Apps        cnet CLI · Gallery (ASP.NET Core) · QuickStart · your code         │
├──────────────────────────────────────────────────────────────────────────────┤
│ Extensions  AddCryptoNet() · ChainCatalog · RpcProviders (Ankr, dRPC) · bench  │
├──────────┬──────────┬──────────┬──────────────┬──────────────────────────────┤
│ Evm      │ Bitcoin  │ Solana   │ Polkadot     │ Cosmos                        │
├──────────┴──────────┴──────────┴──────────────┴──────────────────────────────┤
│ Wallet  BIP-39 · BIP-32/SLIP-10 · keystore · KeySigner                        │
│ Core    Amount · Address · ISigner · IChainClient · JsonRpcClient (failover)  │
├──────────────────────────────────────────────────────────────────────────────┤
│ Native  CryptoNative ──► NativeMethods (LibraryImport) ──► cryptonet          │
│                     └──► Managed/* (pure C#, byte-identical)                  │
╞═══════════════════════════════ C ABI ════════════════════════════════════════╡
│ Rust    cryptonet-ffi ─► cryptonet-crypto (sha2, sha3, blake2, ripemd, k256,  │
│                          ed25519-dalek, schnorrkel, pbkdf2, scrypt)            │
│                      └─► cryptonet-codec (bs58, bech32, RLP, hex)              │
└──────────────────────────────────────────────────────────────────────────────┘
```

## The Rust core

`rust/` is a Cargo workspace with three crates. `cryptonet-crypto` and `cryptonet-codec` are ordinary Rust
libraries with their own tests (official vectors); `cryptonet-ffi` exposes them as a C ABI and builds
`cryptonet.dll` / `libcryptonet.so` / `libcryptonet.dylib`.

ABI rules (see the header comment of `rust/crates/cryptonet-ffi/src/lib.rs`):

- Every function returns `i32`: `0` success, negative `CN_ERR_*` codes. Verification functions return
  `1` valid, `0` invalid, negative on error.
- Inputs are `(ptr, len)`. Fixed-size outputs are caller-allocated. Variable-size outputs take `(out, cap, *written)`;
  too small a buffer returns `CN_ERR_BUFFER_TOO_SMALL` with the required size in `*written`, and the .NET side
  retries once.
- Every entry point runs inside `catch_unwind`; the release profile keeps `panic = "unwind"` so a Rust panic
  becomes `CN_ERR_PANIC` instead of terminating the .NET process.
- Secret intermediates are zeroized (`zeroize`).
- `cn_abi_version()` returns `0xMMmmpppp`. .NET requires the same major and at least
  `NativeLoader.RequiredAbiVersion` (currently `0x00010100`); otherwise it falls back to managed code.

## Loading the native library

`NativeLoader` registers a `DllImportResolver` that searches, in order: `CRYPTONET_NATIVE_PATH`, the app folder,
`runtimes/<rid>/native`, and — for development — parent folders' `runtimes/` and `rust/target/release/`. Set
`CRYPTONET_DISABLE_NATIVE=1` to test the fallback. `cnet doctor` shows what was loaded and why.

## The managed fallback

`src/Crypto.Net.Native/Managed` implements Keccak, RIPEMD-160, BLAKE2b, scrypt, secp256k1 (ECDSA with RFC 6979
and low-S, recovery, BIP-340, BIP-341 tweaks, BIP-32), Ed25519, Base58 and Bech32. `BackendParityTests` checks
that both engines produce identical bytes for every primitive across many inputs. The managed elliptic-curve code
uses `BigInteger` and is **not constant-time**; it exists for portability. sr25519 has no managed implementation.

## Adding a primitive

1. Implement it in `cryptonet-crypto` or `cryptonet-codec` with a test vector.
2. Export it from `cryptonet-ffi` following the ABI rules; bump the minor ABI version.
3. Declare it in `NativeMethods.cs`, add the managed implementation, and route it in `CryptoNative`.
4. Add a case to `BackendParityTests`.
5. Raise `NativeLoader.RequiredAbiVersion` if .NET now depends on it.

## Chain packages

Chain packages depend only on Core, Wallet and Native. Each provides an address codec, transaction
builder/serializer, account type with `CreateSigner()`, `HdWallet` extension (`GetEvmAccount`, …) and an
`IChainClient`. JSON-RPC chains share `JsonRpcClient`, which takes an ordered endpoint list and fails over on
access-denied errors only.
