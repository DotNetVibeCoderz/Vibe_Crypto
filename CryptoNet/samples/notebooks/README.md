# Crypto.Net notebooks · Notebook Crypto.Net

Interactive .NET notebooks, one per chain, in English (`*.en.ipynb`) and Bahasa Indonesia (`*.id.ipynb`).
They install `Crypto.Net.Extensions` from nuget.org, read public networks and sign offline — **nothing is ever
broadcast**.

Notebook .NET interaktif, satu per chain, dalam bahasa Inggris (`*.en.ipynb`) dan Indonesia (`*.id.ipynb`).
Notebook memasang `Crypto.Net.Extensions` dari nuget.org, membaca jaringan publik dan menandatangani secara offline —
**tidak pernah mem-broadcast transaksi**.

| # | English | Bahasa Indonesia | Covers / Isi |
| --- | --- | --- | --- |
| 01 | [Wallets and keys](01-wallet-and-keys.en.ipynb) | [Dompet dan kunci](01-wallet-and-keys.id.ipynb) | BIP-39, HD wallet on 5 chains, zpub, Web3 keystore, Rust vs managed |
| 02 | [Bitcoin](02-bitcoin.en.ipynb) | [Bitcoin](02-bitcoin.id.ipynb) | P2PKH/P2WPKH/P2TR, WIF, Esplora, coin selection, SegWit + Taproot signing |
| 03 | [Ethereum and EVM](03-ethereum-evm.en.ipynb) | [Ethereum dan EVM](03-ethereum-evm.id.ipynb) | EIP-55, Sepolia, ERC-20, EIP-1559, EIP-191/712, ABI |
| 04 | [Solana](04-solana.en.ipynb) | [Solana](04-solana.id.ipynb) | Phantom keys, devnet, PDA/ATA, transaction simulation |
| 05 | [Polkadot and Substrate](05-polkadot.en.ipynb) | [Polkadot dan Substrate](05-polkadot.id.ipynb) | sr25519/ed25519, `//Alice`, SS58, SCALE, metadata-driven extrinsic + fee |
| 06 | [Cosmos SDK](06-cosmos.en.ipynb) | [Cosmos SDK](06-cosmos.id.ipynb) | Bech32 prefixes, REST, SIGN_MODE_DIRECT, gas simulation |

## Run / Menjalankan

- **VS Code**: install the [Polyglot Notebooks](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.dotnet-interactive-vscode)
  extension and the .NET 10 SDK, open a notebook, *Run All*.
- **Headless**: `dotnet tool install -g dotnet-repl`, then
  `dotnet-repl --run 02-bitcoin.en.ipynb --exit-after-run --output-path out.ipynb`.

Every notebook is executed against the published packages before release.

## Editing / Mengubah

The notebooks are generated so both languages always share the same code. Edit `_generate.py` and run
`python samples/notebooks/_generate.py`.

Notebook dibuat oleh generator agar kedua bahasa selalu memakai kode yang sama. Ubah `_generate.py` lalu jalankan
`python samples/notebooks/_generate.py`.

*Built by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
