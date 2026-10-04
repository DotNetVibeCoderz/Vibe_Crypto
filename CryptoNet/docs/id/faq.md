# FAQ

[← Dokumentasi](index.md) · [English](../en/faq.md)

**Apakah saya perlu memasang Rust untuk memakai paketnya?**
Tidak. `Crypto.Net.Native` sudah membawa library yang terkompilasi. Rust hanya dibutuhkan untuk build dari source.

**Apa yang terjadi di platform tanpa binary native?**
Semua fitur kecuali sr25519 beralih ke C# murni dengan hasil identik. `cnet doctor` memberi tahu mesin mana yang aktif.

**Alamat saya berbeda dengan dompet X.**
Periksa path derivasinya. Crypto.Net memakai MetaMask (`m/44'/60'/0'/0/i`), Phantom (`m/44'/501'/i'/0'`), BIP-84/86
untuk Bitcoin dan akun root Polkadot.js (path kosong). Ledger Live memakai `m/44'/60'/i'/0/0` untuk Ethereum —
turunkan dengan `wallet.DerivePath(...)`. Pastikan juga kedua sisi memakai passphrase yang sama.

**Mengapa SHA-256 tidak lebih cepat dengan Rust?**
.NET sudah memakai implementasi OS yang diakselerasi SHA-NI, dan melintasi batas FFI lebih mahal daripada
penghematannya untuk input kecil. Karena itu mode `Auto` tetap memakai platform untuk SHA-2, HMAC dan PBKDF2.

**Apakah receipt Polkadot membuktikan transfer berhasil?**
Receipt membuktikan inklusi dalam blok. Error dispatch dilaporkan lewat `System.Events`; periksa di explorer.

**Endpoint publik terus membatasi laju permintaan saya.**
Tambahkan API key Ankr atau dRPC (lihat [Dependency injection dan penyedia RPC](dependency-injection.md)) atau arahkan
opsi ke node Anda sendiri.

**Holesky?**
Holesky sudah dihentikan; `EvmChain.Holesky` ditandai obsolete. Gunakan Sepolia atau Hoodi.

**Bisakah dipakai dengan hardware wallet atau HSM?**
Implementasikan `ISigner`. Pembangun transaksi (`EvmTransaction.SignAsync`, `SolanaTransaction.SignAsync`,
`CosmosTxBuilder.SignAsync`) menerima signer apa pun.
