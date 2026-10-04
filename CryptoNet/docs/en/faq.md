# FAQ

[← Documentation](index.md) · [Bahasa Indonesia](../id/faq.md)

**Do I need Rust installed to use the packages?**
No. `Crypto.Net.Native` ships the compiled library. Rust is only needed to build from source.

**What happens on a platform without a native binary?**
Everything except sr25519 falls back to pure C# with identical results. `cnet doctor` tells you which engine is active.

**My addresses differ from wallet X.**
Check the derivation path. Crypto.Net uses MetaMask (`m/44'/60'/0'/0/i`), Phantom (`m/44'/501'/i'/0'`),
BIP-84/86 for Bitcoin and the Polkadot.js root account (empty path). Ledger Live uses `m/44'/60'/i'/0/0` for
Ethereum — derive it with `wallet.DerivePath(...)`. Also check that both sides use the same passphrase.

**Why is SHA-256 not faster with Rust?**
.NET already uses the operating system's SHA-NI-accelerated implementation, and crossing the FFI boundary costs
more than it saves for small inputs. `Auto` mode therefore keeps SHA-2, HMAC and PBKDF2 on the platform.

**Is a Polkadot receipt proof that the transfer succeeded?**
It proves inclusion in a block. Dispatch errors are reported through `System.Events`; check an explorer.

**Public endpoints keep rate-limiting me.**
Add an Ankr or dRPC key (see [Dependency injection and RPC providers](dependency-injection.md)) or point the
options at your own node.

**Holesky?**
Holesky was retired; `EvmChain.Holesky` is marked obsolete. Use Sepolia or Hoodi.

**Can I use it with a hardware wallet or HSM?**
Implement `ISigner`. Transaction builders (`EvmTransaction.SignAsync`, `SolanaTransaction.SignAsync`,
`CosmosTxBuilder.SignAsync`) accept any signer.
