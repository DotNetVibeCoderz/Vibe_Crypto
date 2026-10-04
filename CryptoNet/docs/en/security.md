# Security

[← Documentation](index.md) · [Bahasa Indonesia](../id/keamanan.md)

Crypto.Net has **not** had an external security audit. Evaluate it on testnets, review the code you depend on,
and report issues privately (see [SECURITY.md](../../SECURITY.md)).

## What the library does for you

- **Key material** lives in `SecureBuffer`: pinned (never copied by the GC), zeroed on dispose and finalization,
  compared in constant time. Intermediate keys during HD derivation are disposed; Rust zeroizes its temporaries.
- **Signatures** are deterministic (RFC 6979) and low-S, so they are not malleable and need no entropy source.
  Schnorr and sr25519 signing add fresh randomness as the specifications recommend.
- **Validation everywhere**: EIP-55 checksums are enforced for mixed-case addresses, Bech32 vs Bech32m is checked
  per witness version (BIP-350), SS58 and Base58Check checksums are verified, Bitcoin signing refuses keys that do
  not match the spent output.
- **Simulation before signing**: EVM transfers run `eth_estimateGas`, Solana transfers run `simulateTransaction`,
  Cosmos transactions are simulated for gas.
- **Exact amounts**: `Amount` throws rather than rounding.
- **Keystores** use the standard scrypt parameters by default and verify the MAC before decrypting.
- **API keys** never appear in `RpcEndpoint.ToString()` and keyed clients do not log URLs.

## What you must do

- Never put a mnemonic or private key in source code, configuration files or logs. Use a vault, OS keychain,
  hardware wallet or an `ISigner` that talks to an HSM.
- Dispose accounts, keys and wallets as soon as you no longer need them (`using`).
- Prefer the native backend for signing in production: the managed elliptic-curve code is not constant-time.
- Verify recipient addresses with the chain-specific `IsValid`/`Parse` before sending.
- Use testnets (the DI defaults) until you have tested your flow end to end.
- Pin package versions and check `dotnet list package --vulnerable` and `cargo audit` in CI.
- The Gallery and the CLI's `--show-keys` are developer tools; do not expose the Gallery on a public network.
