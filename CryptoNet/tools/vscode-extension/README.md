# Crypto.Net Tools for VS Code

Tools for building blockchain apps in .NET with [Crypto.Net](https://github.com/DotNetVibeCoderz/Vibe_Crypto/tree/main/CryptoNet),
for Bitcoin, Ethereum/EVM, Solana, Polkadot and Cosmos.

*Built by Gravicode Studios, led by Kang Fadhil.* · [Bahasa Indonesia](#bahasa-indonesia)

## Requirements

The extension uses the `cnet` CLI as its backend, so all cryptography runs in Crypto.Net's tested code:

```bash
dotnet tool install -g Crypto.Net.Cli
```

If `cnet` is not on your `PATH`, set `cryptonet.cliPath`.

## Features

- **Crypto.Net view** in the activity bar:
  - every built-in network, grouped by family; click one to check a balance;
  - quick actions for everything below.
- **Commands** (`Crypto.Net: …` in the command palette):
  - New Testnet Wallet: the phrase is shown in an unsaved document, never written to disk.
  - Check Balance, Validate Address, Convert Units (exact, no floating point).
  - New Project from Template: the six `dotnet new cnet-*` templates.
  - Open Notebook, Run cnet doctor.
- **Generate Contract Client**: right-click a `*.abi.json` (Solidity ABI or a Hardhat/Foundry artifact) or `*.idl.json`
  (Anchor IDL) and pick the command. It writes a typed C# client, using the same generator as the `Crypto.Net.Generators`
  source generator.
- **Hovers in C#**:
  - EVM addresses show their EIP-55 status, the checksummed form and explorer links.
  - Long integers show their value as wei/gwei/ETH, lamports/SOL, sat/BTC, planck/DOT and uatom/ATOM.
- **Diagnostics**: an address whose EIP-55 checksum is wrong gets a warning (`CNETVS001`). The case pattern exists to
  catch exactly that kind of typo.
- **Snippets**: `cnet-transfer`, `cnet-contract`, `cnet-event`, `cnet-psbt`, `cnet-wallet`, `cnet-sol`, `cnet-di`.
- **Local nodes** from the status bar: Anvil, `solana-test-validator`, `bitcoind -regtest`, `polkadot --dev`, or the
  `docker compose` file from the `cnet-test` template.
- English and Bahasa Indonesia UI (follows VS Code's display language).

For compile-time checks (hard-coded keys, float amounts, mainnet in Debug, dropped cancellation tokens) add the
`Crypto.Net.Analyzers` package to your project.

## Settings

| Setting | Default | |
| --- | --- | --- |
| `cryptonet.cliPath` | `cnet` | Path or name of the CLI |
| `cryptonet.defaultNetwork` | `sepolia` | Network offered first |
| `cryptonet.hover.enabled` | `true` | Unit and address hovers |
| `cryptonet.diagnostics.enabled` | `true` | EIP-55 checksum warnings |

---

## Bahasa Indonesia

Alat bantu untuk membangun aplikasi blockchain di .NET dengan Crypto.Net, untuk Bitcoin, Ethereum/EVM, Solana,
Polkadot dan Cosmos. Pasang dulu CLI-nya: `dotnet tool install -g Crypto.Net.Cli`.

- **View Crypto.Net** di activity bar:
  - semua jaringan, dikelompokkan per keluarga; klik untuk cek saldo;
  - aksi cepat untuk semua fitur di bawah.
- **Perintah:**
  - Dompet Testnet Baru: frasa ditampilkan di dokumen yang tidak disimpan.
  - Cek Saldo, Validasi Alamat, Konversi Unit (presisi, tanpa floating point).
  - Project Baru dari Template, Buka Notebook, Jalankan cnet doctor.
- **Buat Client Kontrak**: klik kanan `*.abi.json` atau `*.idl.json` untuk menghasilkan client C# bertipe.
- **Hover di C#**:
  - Alamat EVM menampilkan status EIP-55, bentuk checksum dan tautan explorer.
  - Bilangan bulat panjang menampilkan konversinya ke semua unit.
- **Diagnostik**: alamat dengan checksum EIP-55 yang salah diberi peringatan (`CNETVS001`).
- **Snippet** `cnet-*`, **node lokal** dari status bar, dan UI dalam bahasa Inggris dan Indonesia.

Untuk pemeriksaan saat kompilasi, tambahkan paket `Crypto.Net.Analyzers`.
