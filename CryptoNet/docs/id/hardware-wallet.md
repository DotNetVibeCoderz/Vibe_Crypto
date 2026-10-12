# Hardware wallet (Ledger)

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/hardware-wallets.md)

`Crypto.Net.Ledger` menandatangani dengan perangkat Ledger. Kunci tidak pernah keluar dari perangkat, dan pengguna
mengonfirmasi setiap transaksi dan pesan di layarnya.

```bash
dotnet add package Crypto.Net.Ledger
```

```csharp
using var transport = HidLedgerTransport.Open();               // USB; buka kunci perangkat dan buka aplikasi Ethereum
var eth = new LedgerEthereumApp(transport);
string path = LedgerEthereumApp.LedgerLivePath(0);              // m/44'/60'/0'/0/0

var account = await eth.GetAddressAsync(path, display: true);   // tampilkan di perangkat lalu bandingkan
var tx = await rpc.PrepareTransactionAsync(account.Address, "0x…", Amount.FromEther(0.01m).BaseUnits);
SignedEvmTransaction signed = await eth.SignTransactionAsync(path, tx);   // pengguna menyetujui di perangkat
await rpc.SendRawTransactionAsync(signed.RawBytes);
```

| Komponen | Kegunaan |
| --- | --- |
| `HidLedgerTransport` | USB HID di Windows, macOS dan Linux (HidSharp). Linux memerlukan udev rules dari Ledger. |
| `SpeculosTransport` | Port APDU emulator Speculos, untuk pengembangan dan CI |
| `ReplayLedgerTransport` | Memutar ulang sesi APDU `=>`/`<=` yang direkam, untuk unit test |
| `LedgerEthereumApp` | Konfigurasi, alamat (opsional ditampilkan di layar, dengan chain id), transaksi, pesan personal, EIP-712 |
| `LedgerSolanaApp` | Konfigurasi, alamat (opsional ditampilkan di layar), transaksi legacy dan v0, pesan off-chain |
| `LedgerException` | Status word beserta artinya: `IsUserRejection`, perangkat terkunci, aplikasi belum dibuka, blind signing nonaktif |

Transaksi sepanjang apa pun dipecah menjadi APDU 255 byte. Pemecahannya memperhatikan bagian akhir EIP-155 pada
transaksi legacy. Nilai `v` legacy untuk chain id di atas 109 direkonstruksi dengan benar.

## Solana

```csharp
var sol = new LedgerSolanaApp(transport);                       // buka aplikasi Solana di perangkat
string path = LedgerSolanaApp.PhantomPath(0);                   // 44'/501'/0'/0'
string address = await sol.GetAddressAsync(path, display: true);
var tx = new SolanaTransaction(address, blockhash);
tx.Instructions.Add(SystemProgram.Transfer(address, "…", 1_000_000));
await sol.SignTransactionAsync(path, tx);                       // tanda tangan diverifikasi lalu dilampirkan
await rpc.SendRawTransactionAsync(tx.Serialize());

byte[] sig = await sol.SignOffchainMessageAsync(path, LedgerSolanaApp.FormatOffchainMessage("Masuk ke example.org"));
```

Transaksi panjang dikirim dalam potongan 255 byte, dan setiap level path dibuat hardened sesuai kebutuhan aplikasi.

## Cara pengujian

- Unit test memutar ulang sesi APDU yang direkam di test suite `hw-app-eth` milik Ledger, identik byte demi byte.
- CI menjalankan aplikasi Ethereum resmi Ledger (`app-ethereum` 1.22 untuk Nano X) di Speculos, dengan frasa uji BIP-39
  yang publik. Sebuah test menelusuri layar review lewat API Speculos dan memeriksa tiga hal terhadap alamat yang
  diturunkan Crypto.Net secara software dari frasa yang sama: alamat perangkat, serta penandatangan hasil pemulihan dari
  pesan personal dan dari transaksi EIP-1559 dan legacy.
- Aplikasi Solana diuji dengan memutar ulang sesi rekaman `hw-app-solana`; setiap tanda tangan rekaman terverifikasi
  terhadap fee payer. CI juga membangun aplikasi Solana milik Ledger dari source dengan app builder Ledger,
  menjalankannya di Speculos, lalu memeriksa bahwa alamat perangkat sama dengan alamat path Phantom dan bahwa tanda
  tangan transfer dan pesan off-chain terverifikasi.

Aplikasi Ledger untuk chain lain, Trezor (protokol protobuf lewat Bridge atau WebUSB) dan WalletConnect v2 belum
diimplementasikan. WalletConnect
memerlukan project ID dari dashboard cloud Reown.
