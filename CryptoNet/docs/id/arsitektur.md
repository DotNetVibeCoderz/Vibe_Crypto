# Arsitektur

[← Dokumentasi](index.md) · [English](../en/architecture.md)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Aplikasi    CLI cnet · Gallery (ASP.NET Core) · QuickStart · kode Anda         │
├──────────────────────────────────────────────────────────────────────────────┤
│ Extensions  AddCryptoNet() · ChainCatalog · RpcProviders (Ankr, dRPC) · bench  │
├──────────┬──────────┬──────────┬──────────────┬──────────────────────────────┤
│ Evm      │ Bitcoin  │ Solana   │ Polkadot     │ Cosmos                        │
├──────────┴──────────┴──────────┴──────────────┴──────────────────────────────┤
│ Wallet  BIP-39 · BIP-32/SLIP-10 · keystore · KeySigner                        │
│ Core    Amount · Address · ISigner · IChainClient · JsonRpcClient (failover)  │
├──────────────────────────────────────────────────────────────────────────────┤
│ Native  CryptoNative ──► NativeMethods (LibraryImport) ──► cryptonet          │
│                     └──► Managed/* (C# murni, identik per byte)               │
╞═══════════════════════════════ C ABI ════════════════════════════════════════╡
│ Rust    cryptonet-ffi ─► cryptonet-crypto (sha2, sha3, blake2, ripemd, k256,  │
│                          ed25519-dalek, schnorrkel, pbkdf2, scrypt)            │
│                      └─► cryptonet-codec (bs58, bech32, RLP, hex)              │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Inti Rust

`rust/` adalah workspace Cargo dengan tiga crate. `cryptonet-crypto` dan `cryptonet-codec` adalah library Rust biasa
dengan test-nya sendiri (vector resmi); `cryptonet-ffi` mengeksposnya sebagai C ABI dan membangun
`cryptonet.dll` / `libcryptonet.so` / `libcryptonet.dylib`.

Aturan ABI (lihat komentar di awal `rust/crates/cryptonet-ffi/src/lib.rs`):

- Setiap fungsi mengembalikan `i32`: `0` sukses, kode negatif `CN_ERR_*`. Fungsi verifikasi mengembalikan `1` valid,
  `0` tidak valid, negatif jika error.
- Input berupa `(ptr, len)`. Output berukuran tetap dialokasikan pemanggil. Output berukuran variabel memakai
  `(out, cap, *written)`; buffer yang terlalu kecil mengembalikan `CN_ERR_BUFFER_TOO_SMALL` dengan ukuran yang
  dibutuhkan di `*written`, dan sisi .NET mencoba ulang sekali.
- Setiap entry point berjalan di dalam `catch_unwind`; profil release mempertahankan `panic = "unwind"` sehingga panic
  Rust menjadi `CN_ERR_PANIC`, bukan mematikan proses .NET.
- Nilai rahasia sementara di-nol-kan (`zeroize`).
- `cn_abi_version()` mengembalikan `0xMMmmpppp`. .NET mensyaratkan major yang sama dan minimal
  `NativeLoader.RequiredAbiVersion` (saat ini `0x00010100`); jika tidak, kembali ke kode managed.

## Memuat library native

`NativeLoader` mendaftarkan `DllImportResolver` yang mencari, berurutan: `CRYPTONET_NATIVE_PATH`, folder aplikasi,
`runtimes/<rid>/native`, dan — untuk pengembangan — `runtimes/` serta `rust/target/release/` di folder induk.
Set `CRYPTONET_DISABLE_NATIVE=1` untuk menguji fallback. `cnet doctor` menunjukkan apa yang dimuat dan alasannya.

## Fallback managed

`src/Crypto.Net.Native/Managed` mengimplementasikan Keccak, RIPEMD-160, BLAKE2b, scrypt, secp256k1 (ECDSA dengan
RFC 6979 dan low-S, recovery, BIP-340, tweak BIP-341, BIP-32), Ed25519, Base58 dan Bech32. `BackendParityTests`
memastikan kedua mesin menghasilkan byte identik untuk setiap primitif pada banyak input. Kode kurva eliptik managed
memakai `BigInteger` dan **bukan constant-time**; tujuannya portabilitas. sr25519 tidak punya implementasi managed.

## Menambahkan primitif

1. Implementasikan di `cryptonet-crypto` atau `cryptonet-codec` beserta test vector.
2. Ekspor dari `cryptonet-ffi` mengikuti aturan ABI; naikkan versi minor ABI.
3. Deklarasikan di `NativeMethods.cs`, tambahkan implementasi managed, dan arahkan di `CryptoNative`.
4. Tambahkan kasus di `BackendParityTests`.
5. Naikkan `NativeLoader.RequiredAbiVersion` jika .NET kini bergantung padanya.

## Paket chain

Paket chain hanya bergantung pada Core, Wallet dan Native. Masing-masing menyediakan codec alamat, pembangun/serializer
transaksi, tipe akun dengan `CreateSigner()`, extension `HdWallet` (`GetEvmAccount`, …) dan `IChainClient`. Chain
JSON-RPC berbagi `JsonRpcClient` yang menerima daftar endpoint berurutan dan hanya melakukan failover pada error
penolakan akses.
