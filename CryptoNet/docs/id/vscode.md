# Ekstensi VS Code

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/vscode.md)

**Crypto.Net Tools** membawa Crypto.Net ke dalam editor. Ekstensi ini hanya UI tipis di atas CLI `cnet`, sehingga
setiap operasi menjalankan kode .NET teruji yang sama dengan aplikasi Anda.

## Memasang

1. `dotnet tool install -g Crypto.Net.Cli`
2. Unduh `crypto-net-tools.vsix` dari [rilis GitHub](https://github.com/DotNetVibeCoderz/Vibe_Crypto/releases), lalu
   jalankan **Extensions: Install from VSIX…**, atau `code --install-extension crypto-net-tools.vsix`.

## Fitur

| Fitur | Cara |
| --- | --- |
| View jaringan | Activity bar → Crypto.Net. Klik jaringan untuk cek saldo. |
| Dompet testnet baru | Menampilkan frasa dan alamat di semua chain dalam dokumen yang **tidak disimpan**. |
| Cek saldo, validasi alamat, konversi unit | Command palette, `Crypto.Net: …`. Konversi unit presisi. |
| Buat client kontrak | Klik kanan `*.abi.json` / `*.idl.json`. Menulis `<Nama>.g.cs` (generator yang sama dengan `cnet gen`). |
| Project baru | Pilih salah satu dari enam [template](template.md), sebuah folder dan jaringan default. |
| Hover | Alamat EVM menampilkan status EIP-55 dan tautan explorer. Bilangan bulat panjang menampilkan wei/gwei/ETH, lamports/SOL, sat/BTC, planck/DOT, uatom/ATOM. |
| Diagnostik | `CNETVS001` menandai alamat yang huruf besar-kecilnya tidak cocok dengan checksum EIP-55, kemungkinan salah ketik. |
| Snippet | `cnet-transfer`, `cnet-contract`, `cnet-event`, `cnet-psbt`, `cnet-wallet`, `cnet-sol`, `cnet-di` |
| Node lokal | Status bar → Anvil, solana-test-validator, bitcoind regtest, polkadot --dev, atau docker compose |
| Notebook | Membuka [notebook](../../samples/notebooks/README.md) dalam bahasa Anda |

UI mengikuti bahasa tampilan VS Code (Inggris atau Indonesia). [Analyzer](analyzer.md) melengkapi ekstensi ini dengan
aturan saat kompilasi.

## Pengembangan

```bash
cd tools/vscode-extension
npm ci
npm test                  # unit test (vektor EIP-55, matematika unit presisi, scanning)
npm run test:integration  # menjalankan VS Code terisolasi dan memeriksa aktivasi, perintah, hover dan diagnostik
npm run package           # crypto-net-tools.vsix
```

CI menjalankan semua langkah di atas (integrasi di bawah `xvfb`) dan melampirkan `.vsix` ke setiap rilis. Publikasi ke
Marketplace juga memerlukan secret repositori `VSCE_PAT`.
