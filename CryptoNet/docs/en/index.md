# Crypto.Net documentation

> Multi-chain cryptography and blockchain clients for .NET 10, powered by a Rust core.
> Built by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/index.md)

![Crypto.Net Gallery](../assets/screenshots/gallery-wallet.png)

## Start here

| Page | Read it when you want to… |
| --- | --- |
| [Getting started](getting-started.md) | install the packages and make your first wallet, signature and transfer |
| [Concepts](concepts.md) | understand `Amount`, `Address`, `ISigner`, `IChainClient`, backends and errors |
| [Chains](chains.md) | use Ethereum/EVM, Bitcoin, Solana, Polkadot or Cosmos in depth |
| [Wallets and keys](wallet-and-keys.md) | work with mnemonics, HD derivation, xpubs and keystores |
| [Dependency injection and RPC providers](dependency-injection.md) | register clients in ASP.NET Core, add Ankr/dRPC, resilience |
| [The `cnet` CLI](cli.md) | script wallets, balances and signatures from a terminal |
| [The Gallery](gallery.md) | explore every feature interactively |
| [Architecture](architecture.md) | see how the Rust core, the C ABI and the managed fallback fit together |
| [Security](security.md) | deploy safely |
| [Building and contributing](contributing.md) | build from source, run tests, release packages |
| [FAQ](faq.md) | find quick answers |
| [Notebooks](../../samples/notebooks/README.md) | learn each chain interactively in Polyglot Notebooks |

## What is supported

| | Bitcoin | Ethereum / EVM | Solana | Polkadot / Substrate | Cosmos SDK |
| --- | --- | --- | --- | --- | --- |
| Curve / scheme | secp256k1 ECDSA, BIP-340 Schnorr | secp256k1 ECDSA | Ed25519 | sr25519, Ed25519 | secp256k1 ECDSA |
| Addresses | P2PKH, P2SH, P2WPKH, P2WSH, P2TR | EIP-55 | Base58, PDA, ATA | SS58 (any prefix) | Bech32 (any HRP) |
| HD paths | BIP-44/84/86, xpub/zpub | BIP-44 (MetaMask) | SLIP-10 (Phantom) | substrate-bip39 `//hard/soft` | BIP-44 coin 118 |
| Transactions | Legacy, SegWit v0, Taproot key-path; coin selection | Legacy (EIP-155), EIP-2930, EIP-1559 | Legacy messages: System, SPL Token, ATA, Compute Budget, Memo | Extrinsic v4 built from live runtime metadata | `SIGN_MODE_DIRECT`: bank, staking, distribution |
| Off-chain signing | — | EIP-191, EIP-712 | Ed25519 messages | Polkadot.js `signRaw` | — |
| RPC | Esplora REST | JSON-RPC | JSON-RPC | JSON-RPC | REST (LCD) |
| Tokens | — | ERC-20 | SPL Token | native balance | bank balances |

Networks built in: Ethereum, Sepolia, Hoodi, Polygon, Arbitrum, Optimism, Base, BNB Chain, Avalanche, local Anvil;
Bitcoin mainnet/testnet/signet/regtest; Solana mainnet/devnet/testnet/local; Polkadot, Kusama, their Asset Hubs,
Westend, Paseo, local; Cosmos Hub (+ testnet), Osmosis (+ testnet), local `simd`. Run `cnet networks` for the list.

Roadmap items (indexer, WalletConnect, hardware wallets, VS Code extension, templates, mobile Gallery) are tracked
in [PLAN.md](../../PLAN.md).
