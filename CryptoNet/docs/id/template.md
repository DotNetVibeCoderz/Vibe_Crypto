# Template project

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/templates.md)

`Crypto.Net.Templates` menambahkan template `dotnet new` yang dimulai di testnet dan siap dijalankan.

```bash
dotnet new install Crypto.Net.Templates
dotnet new cnet-console -n Wallet.Demo
cnet new webapi -n Payments.Api --network solana-devnet     # sama, lewat CLI cnet
```

| Template | Isi |
| --- | --- |
| `cnet-console` | Aplikasi console: menurunkan akun untuk jaringan yang dikonfigurasi, menampilkan head dan saldo |
| `cnet-worker` | Worker Service: mengikuti blok baru, mencatat perubahan saldo dan mengindeks transfer ERC-20 ke SQLite ([Indexer](indexer.md)) |
| `cnet-webapi` | Minimal API dengan OpenAPI: jaringan, head, saldo, deteksi alamat; hanya testnet kecuali `CryptoNet:AllowMainnet` |
| `cnet-lib` | Class library dengan `PortfolioService` yang terdaftar di DI, plus project xUnit yang memakai mock `Crypto.Net.Testing` |
| `cnet-test` | Project xUnit dengan vektor resmi, `MockHttpMessageHandler`, dan `docker-compose.yml` untuk Anvil, chain dev Polkadot dan `solana-test-validator` |

Setiap template berisi:

- `README.md` dua bahasa;
- `.editorconfig` yang menjadikan CNET001/CNET003 error;
- central package management (`Directory.Packages.props`);
- `appsettings.json` dengan profil testnet;
- [analyzer](analyzer.md).

## Opsi

| Opsi | Default | Arti |
| --- | --- | --- |
| `--Network` | `sepolia` | Key jaringan default (`bitcoin-testnet`, `solana-devnet`, `westend`, `cosmoshub-testnet`, …; lihat `cnet networks`) |
| `--CryptoNetVersion` | versi paket template | Versi paket Crypto.Net yang direferensikan |
| `--skipRestore` | `false` | Lewati `dotnet restore` setelah dibuat |

## Konfigurasi dan rahasia

Aplikasi yang dihasilkan membaca `appsettings.json`, lalu user secrets, lalu environment variable `CNET_*`, lalu
argumen command line. Simpan frasa test di user secrets (`dotnet user-secrets set Mnemonic "…"`), jangan di source.
`CRYPTONET_DRPC_KEY` / `CRYPTONET_ANKR_KEY` menaikkan batas rate, seperti di
[panduan DI](dependency-injection.md).

CI menjalankan `scripts/test-templates.sh` setiap ada perubahan. Script ini mem-pack repositori, memasang template di
hive privat, membuat setiap template, mem-build-nya dengan warning analyzer dan kerentanan sebagai error, lalu
menjalankan test-nya.
