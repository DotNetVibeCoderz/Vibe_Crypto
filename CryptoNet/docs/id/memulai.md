# Memulai

[← Dokumentasi](index.md) · [English](../en/getting-started.md)

## Instalasi

```bash
dotnet add package Crypto.Net.Extensions   # semuanya: core, wallet, lima chain, DI
# atau pilih yang dibutuhkan saja:
dotnet add package Crypto.Net.Evm
dotnet add package Crypto.Net.Bitcoin
```

Kebutuhan: .NET 10. Paket `Crypto.Net.Native` membawa library Rust di `runtimes/<rid>/native`; jika tidak ada binary
untuk platform Anda, implementasi managed dipakai otomatis (sr25519 satu-satunya fitur yang butuh library native).
Periksa dengan:

```bash
dotnet tool install -g Crypto.Net.Cli
cnet doctor
```

![cnet doctor](../assets/screenshots/cli-doctor.png)

## 1. Buat dompet

```csharp
using Crypto.Net.Wallet;
using Crypto.Net.Evm;

using var wallet = HdWallet.Generate(wordCount: 12);
string phrase = wallet.RevealMnemonic();          // tampilkan sekali, simpan offline

using var account = wallet.GetEvmAccount(index: 0, EvmChain.Sepolia);
Console.WriteLine(account.Address);               // 0x… (EIP-55)
```

Memulihkan dompet memakai `HdWallet.FromMnemonic(phrase, passphrase)`. Frasa yang sama menghasilkan alamat yang sama
dengan MetaMask, Phantom, Polkadot.js dan Keplr — lihat [Dompet dan kunci](dompet-dan-kunci.md).

## 2. Membaca dari jaringan

```csharp
await using var client = new EvmRpcClient(EvmChain.Sepolia);
Amount balance = await client.GetBalanceAsync(account.Address);
Console.WriteLine($"{balance} ETH");
```

## 3. Menandatangani pesan

```csharp
byte[] signature = account.SignMessage("Masuk ke example.com");             // EIP-191
string signer = EvmMessageSigner.RecoverPersonalMessageSigner("Masuk ke example.com", HexUtil.Encode(signature));
```

## 4. Mengirim transaksi (testnet)

```csharp
await using var signer = account.CreateSigner();
TxHash hash = await client.TransferAsync(signer, "0xPenerima…", Amount.FromEther(0.001m));
TxReceipt receipt = await client.WaitForReceiptAsync(hash);
```

`TransferAsync` mengambil nonce, menyarankan fee EIP-1559, mensimulasikan transaksi dengan `eth_estimateGas` (revert
gagal di sini, sebelum apa pun ditandatangani), lalu menandatangani dan mem-broadcast.

## 5. Semua chain sekaligus

[Sample quick start](../../samples/Crypto.Net.QuickStart/Program.cs) menurunkan akun di kelima chain, menandatangani
secara offline, membaca blok terbaru lewat dependency injection dan, dengan `--send "<frasa>"`, mem-broadcast
transfer kecil ke diri sendiri di testnet:

```bash
dotnet run --project samples/Crypto.Net.QuickStart
```

## 6. Belajar secara interaktif

[Notebook](../../samples/notebooks/README.md) membahas setiap chain langkah demi langkah dengan
pembacaan jaringan live (bahasa Inggris dan Indonesia).

Berikutnya: [Konsep](konsep.md).
