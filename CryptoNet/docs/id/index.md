# Dokumentasi Crypto.Net

> Kriptografi multi-chain dan client blockchain untuk .NET 10, ditenagai inti Rust.
> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/index.md)

![Crypto.Net Gallery](../assets/screenshots/gallery-wallet-id.png)

## Mulai dari sini

| Halaman | Baca jika Anda ingin… |
| --- | --- |
| [Memulai](memulai.md) | memasang paket dan membuat dompet, tanda tangan serta transfer pertama |
| [Konsep](konsep.md) | memahami `Amount`, `Address`, `ISigner`, `IChainClient`, backend dan error |
| [Chain](chain.md) | memakai Ethereum/EVM, Bitcoin, Solana, Polkadot atau Cosmos secara mendalam |
| [Dompet dan kunci](dompet-dan-kunci.md) | bekerja dengan mnemonic, derivasi HD, xpub dan keystore |
| [Dependency injection dan penyedia RPC](dependency-injection.md) | mendaftarkan client di ASP.NET Core, menambah Ankr/dRPC, resilience |
| [CLI `cnet`](cli.md) | mengotomatiskan dompet, saldo dan tanda tangan dari terminal |
| [Gallery](gallery.md) | menjelajahi setiap fitur secara interaktif |
| [Arsitektur](arsitektur.md) | melihat bagaimana inti Rust, C ABI dan fallback managed saling terhubung |
| [Keamanan](keamanan.md) | menjalankan di produksi dengan aman |
| [Build dan kontribusi](kontribusi.md) | build dari source, menjalankan test, merilis paket |
| [FAQ](faq.md) | mencari jawaban cepat |
| [Notebook](../../samples/notebooks/README.md) | mempelajari setiap chain secara interaktif di Polyglot Notebooks |

## Apa saja yang didukung

| | Bitcoin | Ethereum / EVM | Solana | Polkadot / Substrate | Cosmos SDK |
| --- | --- | --- | --- | --- | --- |
| Kurva / skema | secp256k1 ECDSA, Schnorr BIP-340 | secp256k1 ECDSA | Ed25519 | sr25519, Ed25519 | secp256k1 ECDSA |
| Alamat | P2PKH, P2SH, P2WPKH, P2WSH, P2TR | EIP-55 | Base58, PDA, ATA | SS58 (prefix apa pun) | Bech32 (HRP apa pun) |
| Path HD | BIP-44/84/86, xpub/zpub | BIP-44 (MetaMask) | SLIP-10 (Phantom) | substrate-bip39 `//hard/soft` | BIP-44 coin 118 |
| Transaksi | Legacy, SegWit v0, Taproot key-path; coin selection | Legacy (EIP-155), EIP-2930, EIP-1559 | Message legacy: System, SPL Token, ATA, Compute Budget, Memo | Extrinsic v4 dari metadata runtime live | `SIGN_MODE_DIRECT`: bank, staking, distribution |
| Tanda tangan off-chain | — | EIP-191, EIP-712 | Pesan Ed25519 | `signRaw` Polkadot.js | — |
| RPC | REST Esplora | JSON-RPC | JSON-RPC | JSON-RPC | REST (LCD) |
| Token | — | ERC-20 | SPL Token | saldo native | saldo bank |

Jaringan bawaan: Ethereum, Sepolia, Hoodi, Polygon, Arbitrum, Optimism, Base, BNB Chain, Avalanche, Anvil lokal;
Bitcoin mainnet/testnet/signet/regtest; Solana mainnet/devnet/testnet/lokal; Polkadot, Kusama, Asset Hub keduanya,
Westend, Paseo, lokal; Cosmos Hub (+ testnet), Osmosis (+ testnet), `simd` lokal. Jalankan `cnet networks` untuk daftarnya.

Item roadmap (indexer, WalletConnect, hardware wallet, ekstensi VS Code, template, Gallery mobile) dicatat di
[PLAN.md](../../PLAN.md).
