# Keamanan

[← Dokumentasi](index.md) · [English](../en/security.md)

Crypto.Net **belum** diaudit keamanannya oleh pihak eksternal. Evaluasi di testnet, tinjau kode yang Anda andalkan,
dan laporkan masalah secara privat (lihat [SECURITY.md](../../SECURITY.md)).

## Yang dilakukan library untuk Anda

- **Materi kunci** disimpan di `SecureBuffer`: di-pin (tidak pernah disalin GC), di-nol-kan saat dispose dan
  finalisasi, dibandingkan secara constant-time. Kunci perantara selama derivasi HD di-dispose; Rust me-nol-kan
  nilai sementaranya.
- **Tanda tangan** bersifat deterministik (RFC 6979) dan low-S, sehingga tidak malleable dan tidak butuh sumber
  entropi. Tanda tangan Schnorr dan sr25519 menambahkan keacakan baru sesuai rekomendasi spesifikasinya.
- **Validasi di mana-mana**: checksum EIP-55 diwajibkan untuk alamat huruf campuran, Bech32 vs Bech32m diperiksa per
  versi witness (BIP-350), checksum SS58 dan Base58Check diverifikasi, signing Bitcoin menolak kunci yang tidak
  cocok dengan output yang dibelanjakan.
- **Simulasi sebelum tanda tangan**: transfer EVM menjalankan `eth_estimateGas`, transfer Solana menjalankan
  `simulateTransaction`, transaksi Cosmos disimulasikan untuk gas.
- **Jumlah yang pasti**: `Amount` melempar exception alih-alih membulatkan.
- **Keystore** memakai parameter scrypt standar secara default dan memverifikasi MAC sebelum mendekripsi.
- **API key** tidak pernah muncul di `RpcEndpoint.ToString()` dan client ber-key tidak mencatat URL ke log.

## Yang harus Anda lakukan

- Jangan pernah menaruh mnemonic atau private key di source code, berkas konfigurasi atau log. Gunakan vault,
  keychain OS, hardware wallet atau `ISigner` yang terhubung ke HSM.
- Dispose akun, kunci dan dompet segera setelah tidak dibutuhkan (`using`).
- Utamakan backend native untuk signing di produksi: kode kurva eliptik managed bukan constant-time.
- Verifikasi alamat penerima dengan `IsValid`/`Parse` milik chain terkait sebelum mengirim.
- Gunakan testnet (default DI) sampai alur Anda teruji dari awal hingga akhir.
- Kunci versi paket dan jalankan `dotnet list package --vulnerable` serta `cargo audit` di CI.
- Gallery dan opsi `--show-keys` di CLI adalah alat developer; jangan ekspos Gallery ke jaringan publik.
