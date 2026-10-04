# Gallery

[← Dokumentasi](index.md) · [English](../en/gallery.md)

Gallery adalah studio interaktif untuk setiap fitur Crypto.Net. Setiap halaman menjalankan library sungguhan di server
dan menampilkan kode C# yang melakukan hal yang sama.

```bash
dotnet run --project samples/Crypto.Net.Gallery
# buka http://localhost:5080
```

Isi `CRYPTONET_ANKR_KEY` / `CRYPTONET_DRPC_KEY` sebelum menjalankannya agar jaringan dibaca lewat penyedia Anda.

**Sandbox aman.** Gallery tidak pernah mem-broadcast transaksi. Kunci yang dibuatnya adalah kunci demo yang hanya
hidup di memori selama satu permintaan. Testnet adalah default; beralih ke mainnet menampilkan banner peringatan.

| Halaman | Yang bisa dilakukan |
| --- | --- |
| Dompet | Membuat atau memulihkan frasa dan melihat akun Ethereum, Bitcoin SegWit/Taproot, Solana, Polkadot dan Cosmos. Rosette guilloché adalah sidik jari visual yang digambar dari xpub dompet, bukan dari rahasianya. |
| Periksa alamat | Tempel alamat apa pun untuk melihat chain yang menerimanya, script output-nya, dan bentuknya di jaringan lain. |
| Konversi satuan | Konversi pasti antar semua satuan sebuah chain, langsung saat mengetik. |
| Tanda tangan | Tanda tangan EIP-191, EIP-712, Schnorr BIP-340, Ed25519 dan sr25519, masing-masing diverifikasi ulang. |
| Keystore | Mengenkripsi kunci ke berkas Web3 v3 lalu mendekripsinya kembali. |
| Jaringan langsung | Blok terbaru setiap jaringan lewat client Crypto.Net, ditambah pengecekan saldo. |
| Rust vs managed | Benchmark kedua mesin dengan grafik (skala log) atau tabel. |

Bahasa antarmuka: Inggris dan Indonesia (tombol EN/ID). Tema: terang dan gelap, mengikuti sistem secara default.
Tata letak berfungsi hingga lebar layar ponsel.

![Dompet (Bahasa Indonesia)](../assets/screenshots/gallery-wallet-id.png)
![Periksa alamat](../assets/screenshots/gallery-inspect.png)
![Konversi](../assets/screenshots/gallery-convert.png)
![Tanda tangan](../assets/screenshots/gallery-sign.png)
![Keystore (gelap)](../assets/screenshots/gallery-keystore.png)
![Jaringan langsung](../assets/screenshots/gallery-networks.png)
![Benchmark](../assets/screenshots/gallery-benchmark.png)
<img src="../assets/screenshots/gallery-mobile.png" alt="Tata letak ponsel" width="320">

Parameter URL untuk screenshot dan demo: `?theme=light|dark`, `?lang=en|id`, `?still=1` (tanpa animasi),
`?autorun=1` (menjalankan aksi halaman saat dibuka).
