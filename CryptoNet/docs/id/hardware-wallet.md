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
| `LedgerException` | Status word beserta artinya: `IsUserRejection`, perangkat terkunci, aplikasi belum dibuka, blind signing nonaktif |

Transaksi sepanjang apa pun dipecah menjadi APDU 255 byte. Pemecahannya memperhatikan bagian akhir EIP-155 pada
transaksi legacy. Nilai `v` legacy untuk chain id di atas 109 direkonstruksi dengan benar.

## Cara pengujian

- Unit test memutar ulang sesi APDU yang direkam di test suite `hw-app-eth` milik Ledger, identik byte demi byte.
- CI menjalankan aplikasi Ethereum resmi Ledger (`app-ethereum` 1.22 untuk Nano X) di Speculos, dengan frasa uji BIP-39
  yang publik. Sebuah test menelusuri layar review lewat API Speculos dan memeriksa tiga hal terhadap alamat yang
  diturunkan Crypto.Net secara software dari frasa yang sama: alamat perangkat, serta penandatangan hasil pemulihan dari
  pesan personal dan dari transaksi EIP-1559 dan legacy.

Trezor (protokol protobuf lewat Bridge atau WebUSB) dan WalletConnect v2 belum diimplementasikan. WalletConnect
memerlukan project ID dari dashboard cloud Reown.
