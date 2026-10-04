# Crypto.Net — Dokumen Desain Solusi

> Library .NET 10 + Rust untuk berinteraksi dengan banyak blockchain (Bitcoin, Ethereum/EVM, Solana, Polkadot, Cosmos, dan lainnya) dengan performa native.

Versi dokumen: 0.1 (draft) · Status: Proposal

---

## 1. Ringkasan

**Crypto.Net** adalah ekosistem .NET yang menyatukan akses ke banyak blockchain lewat satu model API yang konsisten. Seperti Nethereum untuk Ethereum, tetapi multi-chain, dengan inti kriptografi dan codec ditulis di **Rust** dan dibungkus ke **.NET 10** lewat C ABI.

**Tujuan**

1. Satu API konsisten untuk akun, penandatanganan, transaksi, query, dan event di banyak chain.
2. Performa terbaik untuk operasi berat (hashing, signing, derivasi kunci, encoding) dengan Rust.
3. Typed, async-first, DI-friendly, mendukung NativeAOT dan trimming.
4. Pengalaman developer lengkap: template, CLI, VS Code extension, notebook, app Gallery, dokumentasi EN/ID.

**Non-tujuan (v1)**: menjalankan full node, custodial service, bridge lintas chain, dukungan semua chain.

---

## 2. Prinsip Desain

| Prinsip | Penjelasan |
| --- | --- |
| Rust untuk yang berat, C# untuk yang ergonomis | Kriptografi, codec, dan parsing biner di Rust. RPC, orkestrasi, DI, dan DX di C#. |
| Chain-agnostic core, chain-specific adapter | Abstraksi umum (`IAccount`, `ISigner`, `IChainClient`), detail chain di paket terpisah. |
| Aman secara default | Secret tidak pernah jadi `string` biasa, memori di-zeroize, tidak ada logging secret. |
| Modular | Pasang hanya chain yang dibutuhkan (`Crypto.Net.Solana`, dst). |
| Dapat diuji | Test vector resmi per chain, fuzzing di sisi Rust, mock RPC di sisi .NET. |
| Dokumentasi sebagai produk | Setiap API publik punya XML doc, contoh, dan halaman docs EN/ID. |

---

## 3. Arsitektur

```
┌───────────────────────────────────────────────────────────────────────┐
│  Aplikasi: Gallery (Avalonia) · CLI · Notebook · VS Code Ext · Template │
├───────────────────────────────────────────────────────────────────────┤
│  Crypto.Net.Extensions (DI, Options, Logging, HttpClientFactory)        │
├──────────┬──────────┬──────────┬──────────┬──────────┬────────────────┤
│ Bitcoin  │ Ethereum │ Solana   │ Polkadot │ Cosmos   │ ... (plugin)   │
│ .Bitcoin │ .Evm     │ .Solana  │ .Polkadot│ .Cosmos  │                │
├──────────┴──────────┴──────────┴──────────┴──────────┴────────────────┤
│  Crypto.Net.Core  (abstraksi: Account, Signer, Tx, Rpc, Events, Units)  │
├───────────────────────────────────────────────────────────────────────┤
│  Crypto.Net.Native  (P/Invoke generated, SafeHandle, loader RID)        │
├═══════════════════════════ C ABI (FFI) ═══════════════════════════════┤
│  Rust workspace: cryptonet-ffi → crates inti (crypto, codec, chain)     │
└───────────────────────────────────────────────────────────────────────┘
```

### Alur sebuah transaksi

```
Builder (C#) → Unsigned Tx → [FFI] encode + hash (Rust) → ISigner → [FFI] sign (Rust)
→ Signed Tx bytes → IRpcClient (HTTP/WS) → Receipt/Confirmation (polling/subscription)
```

---

## 4. Struktur Repository (Monorepo)

```
crypto-net/
├─ README.md / README.id.md
├─ Crypto.Net.slnx
├─ Directory.Build.props / Directory.Packages.props   # central package mgmt
├─ global.json  (SDK .NET 10)  · rust-toolchain.toml · .editorconfig
├─ rust/
│  ├─ Cargo.toml (workspace)
│  └─ crates/
│     ├─ cryptonet-crypto/    # hash, secp256k1, ed25519, sr25519, bip32/39, slip10
│     ├─ cryptonet-codec/     # base58, bech32, RLP, SCALE, borsh, protobuf helper
│     ├─ cryptonet-bitcoin/   # script, PSBT, tx, address, sighash
│     ├─ cryptonet-evm/       # tx 1559/2930/legacy, EIP-712, ABI, keystore
│     ├─ cryptonet-solana/    # message/tx serializer, PDA, compact-u16
│     ├─ cryptonet-polkadot/  # extrinsic, SS58, metadata decode
│     ├─ cryptonet-cosmos/    # SignDoc, amino/direct, bech32
│     └─ cryptonet-ffi/       # C ABI tunggal (cdylib + staticlib), cbindgen
├─ src/
│  ├─ Crypto.Net.Native/
│  ├─ Crypto.Net.Core/
│  ├─ Crypto.Net.Bitcoin/ .Evm/ .Solana/ .Polkadot/ .Cosmos/
│  ├─ Crypto.Net.Wallet/          # HD wallet, keystore, vault
│  ├─ Crypto.Net.Indexer/         # block crawler, log processor, storage
│  ├─ Crypto.Net.Extensions/      # DI & options
│  └─ Crypto.Net.Testing/         # mock RPC, local-node harness
├─ tools/
│  ├─ Crypto.Net.Cli/             # dotnet tool: `cnet`
│  └─ vscode-extension/           # TypeScript
├─ templates/                     # dotnet new templates
├─ samples/
│  ├─ Crypto.Net.Gallery/         # Avalonia
│  └─ notebooks/                  # .ipynb (Polyglot Notebooks)
├─ tests/ (unit, vectors, integration, fuzz) · benchmarks/
├─ docs/ (en/, id/, api/, adr/)
└─ .github/workflows/
```

---

## 5. Rust Core

### 5.1 Crate dan dependensi yang disarankan

| Domain | Crate Rust |
| --- | --- |
| secp256k1 (Bitcoin, EVM, Cosmos) | `secp256k1` atau `k256` |
| ed25519 (Solana) | `ed25519-dalek` |
| sr25519 (Polkadot) | `schnorrkel` |
| Hash | `sha2`, `sha3` (Keccak), `blake2`, `ripemd` |
| HD wallet | `bip32`, `bip39`, implementasi SLIP-10 (ed25519) |
| Encoding | `bs58`, `bech32`, `parity-scale-codec`, `borsh`, `prost` |
| Bitcoin | `bitcoin` (rust-bitcoin), `miniscript` (opsional) |
| Memori aman | `zeroize`, `secrecy` |

### 5.2 Strategi FFI

- **C ABI tunggal** (`cryptonet-ffi`), `crate-type = ["cdylib", "staticlib"]`.
- Header via **cbindgen**; binding C# via **csbindgen** (menghasilkan `LibraryImport`/`DllImport`).
- Aturan ABI:
  - Fungsi mengembalikan `i32` kode error; hasil lewat parameter `out`.
  - Buffer input sebagai `(ptr, len)`; buffer output dialokasikan Rust dan dibebaskan lewat `cn_free_buffer`, atau caller menyediakan buffer dan Rust mengisinya.
  - Tidak ada panic melintasi batas FFI: `catch_unwind` di semua entry point.
  - Handle opaque (`CnHandle`) dibungkus `SafeHandle` di .NET.
  - Versi ABI: `cn_abi_version()` dicek saat load.
- Operasi batch (mis. verifikasi banyak signature) memakai `rayon` di sisi Rust.

Contoh sisi Rust:

```rust
#[no_mangle]
pub extern "C" fn cn_ed25519_sign(
    secret: *const u8, secret_len: usize,
    msg: *const u8, msg_len: usize,
    sig_out: *mut u8, // 64 byte
) -> i32 {
    ffi_guard(|| {
        let sk = slice_in(secret, secret_len)?;
        let m  = slice_in(msg, msg_len)?;
        let sig = ed25519::sign(sk, m)?;
        write_out(sig_out, &sig)
    })
}
```

Sisi C#:

```csharp
internal static partial class NativeMethods
{
    [LibraryImport("cryptonet", EntryPoint = "cn_ed25519_sign")]
    internal static unsafe partial int Ed25519Sign(
        byte* secret, nuint secretLen, byte* msg, nuint msgLen, byte* sigOut);
}
```

### 5.3 Distribusi native

- Target RID: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-x64`, `osx-arm64`, `android-arm64`, `ios-arm64`, `browser-wasm` (tahap lanjut).
- Paket NuGet `Crypto.Net.Native` memuat `runtimes/{rid}/native/*`.
- `NativeLibrary.SetDllImportResolver` untuk loading per-RID dan pesan error yang jelas.
- Build matrix lewat `cross` / `cargo-zigbuild` di CI.
- **Fallback managed**: implementasi C# murni (mis. `NSec`/BouncyCastle) opsional untuk platform tanpa binary native.

---

## 6. Lapisan .NET

### 6.1 Abstraksi inti (`Crypto.Net.Core`)

```csharp
public interface IChain { ChainId Id { get; } string Name { get; } int Decimals { get; } }

public interface IAccount
{
    IChain Chain { get; }
    Address Address { get; }
    ReadOnlyMemory<byte> PublicKey { get; }
}

public interface ISigner : IAsyncDisposable
{
    ValueTask<Signature> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);
}

public interface IChainClient : IAsyncDisposable
{
    IChain Chain { get; }
    ValueTask<Amount> GetBalanceAsync(Address address, CancellationToken ct = default);
    ValueTask<TxHash> SendAsync(SignedTransaction tx, CancellationToken ct = default);
    ValueTask<TxReceipt?> GetReceiptAsync(TxHash hash, CancellationToken ct = default);
    IAsyncEnumerable<BlockInfo> SubscribeBlocksAsync(CancellationToken ct = default);
}
```

Tipe nilai: `Amount` (BigInteger + desimal chain), `Address` (validasi per chain), `TxHash`, `Signature`, semuanya `readonly struct` bila memungkinkan.

### 6.2 Cakupan per chain

| Fitur | Bitcoin | EVM | Solana | Polkadot | Cosmos |
| --- | --- | --- | --- | --- | --- |
| Kurva / skema | secp256k1, Schnorr | secp256k1 | ed25519 | sr25519/ed25519 | secp256k1 |
| Alamat | P2PKH, P2WPKH, P2TR (bech32/m) | EIP-55 | base58 | SS58 | bech32 |
| HD wallet | BIP44/49/84/86 | BIP44 | SLIP-10 | derivasi `//` `/` | BIP44 |
| Transaksi | PSBT, UTXO, fee estimation | Legacy/2930/1559 | Message v0 + ALT | Extrinsic + metadata | Direct/Amino SignDoc |
| RPC | Bitcoin Core, Electrum, Esplora | JSON-RPC, WS | JSON-RPC, WS | JSON-RPC, WS | REST/gRPC, Tendermint RPC |
| Kontrak | Script/Miniscript | ABI, EIP-712, multicall | Program, IDL, PDA | Pallet call, ink! (lanjut) | CosmWasm (lanjut) |
| Token | Ordinals/BRC-20 (lanjut) | ERC-20/721/1155 | SPL Token | Assets pallet | Bank module, IBC (lanjut) |
| Event / indexing | Block scan | Log filter, crawler | Signature/log subscribe | Event dari metadata | Tendermint events |
| Nama | — | ENS | SNS (lanjut) | — | — |

Prioritas rilis: **EVM → Bitcoin → Solana → Cosmos → Polkadot** (EVM pertama karena referensi fitur Nethereum paling jelas).

### 6.3 Paket pendukung

- **Crypto.Net.Wallet**: HD wallet (BIP39), keystore terenkripsi (scrypt/argon2 + AES-GCM), vault in-memory, adapter hardware wallet (Ledger/Trezor, tahap 2), WalletConnect v2.
- **Crypto.Net.Indexer**: block crawler, log processor, resume checkpoint, storage provider (EF Core, SQLite, PostgreSQL, MongoDB).
- **Crypto.Net.Extensions**: `AddCryptoNet(...)`, `IOptions`, `HttpClientFactory`, `ILogger`, retry/circuit-breaker (Polly) dan rate limiting.
- **Crypto.Net.Testing**: fake RPC, replay fixture, helper untuk Anvil/solana-test-validator/bitcoind regtest/local Substrate/simd.

### 6.4 Contoh API

```csharp
// DI
builder.Services.AddCryptoNet(c => c
    .AddEvm("eth", o => o.Rpc = "https://rpc.example/eth")
    .AddSolana("sol", o => o.Rpc = "https://api.devnet.solana.com")
    .AddBitcoin("btc", o => o.Network = BitcoinNetwork.Testnet));

// Wallet HD dari mnemonic
var wallet = HdWallet.FromMnemonic("...", passphrase: null);
var eth = wallet.Derive(Chains.Evm, index: 0);
var sol = wallet.Derive(Chains.Solana, index: 0);

// Kirim di EVM
var client = provider.GetRequiredKeyedService<IEvmClient>("eth");
var tx = await client.Transactions.TransferAsync(eth, to, Amount.FromEther(0.01m));
var receipt = await client.WaitForReceiptAsync(tx);

// Kirim di Solana
var sc = provider.GetRequiredKeyedService<ISolanaClient>("sol");
var hash = await sc.TransferAsync(sol, dest, Amount.FromSol(0.5m));

// Typed contract (EVM), dihasilkan oleh code generator
var usdc = new Erc20Service(client, tokenAddress);
var bal = await usdc.BalanceOfAsync(eth.Address);
```

### 6.5 Code generation

- **Source generator** Roslyn: ABI Solidity → service/DTO C# (EVM), IDL Anchor → client (Solana), metadata → pallet client (Polkadot), proto → message (Cosmos).
- Tersedia juga lewat CLI (`cnet gen`) dan VS Code extension.

---

## 7. Keamanan

- Secret key disimpan di `SecureBuffer` (pinned, di-zeroize saat dispose, `mlock` bila tersedia). Tidak pernah `string`.
- Rust: `zeroize` + `secrecy`, operasi constant-time untuk perbandingan dan signing.
- `ISigner` memisahkan kunci dari logika aplikasi, sehingga mudah diganti ke HSM, hardware wallet, atau remote signer.
- Opsi **simulate-before-send** dan **spending policy** (limit, allowlist alamat) di level client.
- Supply chain: `cargo audit`/`cargo deny`, `dotnet list package --vulnerable`, SBOM (CycloneDX), package signing, build reproducible, provenance (SLSA).
- Fuzzing (`cargo-fuzz`) untuk semua parser/decoder, test vector resmi (BIP, EIP, Solana, Substrate, Cosmos).
- Audit keamanan eksternal sebelum rilis 1.0. Kebijakan pelaporan kerentanan di `SECURITY.md`.
- Peringatan jelas di docs: pakai testnet/devnet untuk sample, jangan commit mnemonic.

---

## 8. Aplikasi Crypto.Net Gallery (Avalonia UI)

**Target**: Windows, macOS, Linux (desktop), Android, iOS, dan browser (WASM) memakai Avalonia 11+.

**Teknologi**: MVVM (CommunityToolkit.Mvvm), FluentAvalonia/tema Fluent, `AvaloniaEdit` untuk viewer kode, DI lewat `Crypto.Net.Extensions`, Serilog.

**Struktur**

```
Crypto.Net.Gallery/            # shared UI
Crypto.Net.Gallery.Desktop/
Crypto.Net.Gallery.Android/
Crypto.Net.Gallery.iOS/
Crypto.Net.Gallery.Browser/
```

**Halaman utama**: setiap sample punya tab **Demo** (interaktif) dan tab **Code** (kode C# yang dijalankan, bisa disalin) plus link ke docs.

| Kategori | Use case sample |
| --- | --- |
| Dasar | Generate mnemonic, derive alamat multi-chain, konversi unit, validasi alamat |
| Wallet | Buat/impor wallet, keystore terenkripsi, tanda tangan pesan, EIP-712, SIWE |
| Transfer | Kirim ETH/SOL/BTC testnet, estimasi fee, pantau konfirmasi |
| Token | Saldo & transfer ERC-20, SPL token, NFT viewer (ERC-721/1155, Metaplex) |
| Smart contract | Deploy & panggil kontrak EVM, baca event, program Solana via IDL |
| DeFi | Quote swap (Uniswap), staking Cosmos, nominasi Polkadot |
| Explorer | Block/tx viewer real-time via WebSocket, mini block explorer |
| Indexer | Crawler log ERC-20 ke SQLite, dashboard |
| Multi-sig | Gnosis Safe, PSBT multisig Bitcoin |
| Performa | Benchmark Rust vs managed (hashing, signing, derivasi) dengan chart |
| Keamanan | Demo SecureBuffer, simulate-before-send, spending policy |

Mode **Safe Sandbox** aktif secara default: hanya testnet/devnet, mainnet butuh konfirmasi eksplisit.

---

## 9. Notebook (Polyglot Notebooks / .NET Interactive)

Folder `samples/notebooks/` (`.ipynb`, dua bahasa: berkas `*.en.ipynb` dan `*.id.ipynb`):

1. `01-getting-started` — instalasi, koneksi RPC, saldo
2. `02-keys-and-mnemonic` — BIP39/BIP32, SLIP-10, SS58, bech32
3. `03-evm-transactions` — transfer, EIP-1559, estimasi gas
4. `04-evm-contracts` — ABI, deploy, event, multicall
5. `05-bitcoin-psbt` — UTXO, PSBT, fee, Taproot
6. `06-solana-programs` — transfer, SPL, PDA, IDL
7. `07-cosmos-staking` — bank, staking, SignDoc
8. `08-polkadot-extrinsics` — metadata, extrinsic, event
9. `09-indexer` — crawler dan query data
10. `10-benchmarks` — BenchmarkDotNet + chart
11. `11-security-practices` — SecureBuffer, signer eksternal

Setiap notebook dimulai dengan `#r "nuget: Crypto.Net.Evm, *-*"` dan bisa dijalankan di VS Code, Jupyter, atau GitHub Codespaces. CI menjalankan notebook (via `dotnet repl`/`papermill`) untuk memastikan tidak rusak.

---

## 10. Template Project (`dotnet new`)

Paket: `Crypto.Net.Templates`

| Short name | Isi |
| --- | --- |
| `cnet-console` | Console minimal: koneksi, saldo, transfer testnet |
| `cnet-worker` | Worker Service + Indexer (event listener) |
| `cnet-webapi` | ASP.NET Core Minimal API: wallet service, endpoint saldo/kirim, OpenAPI |
| `cnet-blazor` | Blazor dengan koneksi wallet browser (EIP-1193, Phantom, Polkadot.js) |
| `cnet-avalonia` | Aplikasi Avalonia multi-platform dengan wallet dasar |
| `cnet-maui` | .NET MAUI wallet starter |
| `cnet-lib` | Class library dengan DI dan test project |
| `cnet-contract-client` | Proyek source generator dari ABI/IDL |
| `cnet-test` | xUnit + `Crypto.Net.Testing` + docker-compose node lokal |

Semua template memuat `README.md` (EN/ID), `.editorconfig`, `Directory.Packages.props`, dan konfigurasi `appsettings.json` berisi profil testnet.

---

## 11. CLI Tool — `cnet`

Dipasang dengan `dotnet tool install -g Crypto.Net.Cli`. Dibangun dengan `System.CommandLine`, mendukung NativeAOT agar start cepat, output `--json` untuk scripting.

```
cnet wallet new [--chain evm|sol|btc|dot|cosmos] [--words 12|24]
cnet wallet import --mnemonic ... | --keystore file.json
cnet wallet derive --path "m/44'/501'/0'/0'"
cnet address validate <addr> --chain sol
cnet balance <addr> --chain evm --rpc <url>
cnet send --from <wallet> --to <addr> --amount 0.1 --chain evm --simulate
cnet tx <hash> --chain btc
cnet sign message "hello" --wallet main
cnet sign typed-data file.json
cnet convert 1.5 ether wei
cnet gen contract --abi Token.json --out ./Generated   # code generation
cnet gen idl --idl program.json
cnet node start --chain evm        # Anvil lokal / devnet via Docker
cnet bench                         # benchmark native vs managed
cnet doctor                        # cek native lib, RID, versi
cnet new <template>                # shortcut dotnet new
```

Profil jaringan disimpan di `~/.cnet/config.toml`; secret via OS keychain (DPAPI, Keychain, libsecret), bukan file plaintext.

---

## 12. VS Code Extension — "Crypto.Net Tools"

TypeScript, memanggil `cnet` sebagai backend (JSON-RPC lewat stdio) agar logika tidak diduplikasi.

**Fitur**

- **Explorer panel**: daftar jaringan, wallet (testnet), kontrak yang di-deploy.
- **Generate client**: klik kanan `.abi.json` / `.idl.json` → generate service C#.
- **Snippets**: `cnet-transfer`, `cnet-contract`, `cnet-event`, `cnet-psbt`, dll.
- **Validasi & hover**: validasi alamat dan unit di kode C#, hover menampilkan konversi Wei/Gwei/Lamports.
- **Quick commands**: "Crypto.Net: New Wallet", "Check Balance", "Send Testnet Transaction", "Open Gallery Sample".
- **Notebook integration**: template notebook dan kernel setup otomatis.
- **Local node**: start/stop Anvil, solana-test-validator, bitcoind regtest dari status bar.
- **Diagnostics**: analyzer peringatan jika mnemonic/private key hardcoded (terhubung dengan Roslyn analyzer `Crypto.Net.Analyzers`).
- Lokalisasi UI: English dan Bahasa Indonesia (`package.nls.json`, `package.nls.id.json`).

---

## 13. Roslyn Analyzers

Paket `Crypto.Net.Analyzers`:

- `CNET001` Private key/mnemonic hardcoded
- `CNET002` Secret disimpan sebagai `string`
- `CNET003` `Amount` dibuat dari `double`/`float`
- `CNET004` Mainnet RPC dipakai di project Debug
- `CNET005` Tidak memakai `CancellationToken` pada panggilan RPC

---

## 14. Dokumentasi (`docs/`)

```
docs/
├─ en/
│  ├─ index.md
│  ├─ getting-started/ (install, quickstart, platform-support)
│  ├─ concepts/ (accounts, signing, transactions, units, errors, native-interop)
│  ├─ chains/ (evm.md, bitcoin.md, solana.md, polkadot.md, cosmos.md)
│  ├─ guides/ (wallet, indexer, hd-derivation, security, nativeaot, testing)
│  ├─ tools/ (cli.md, vscode.md, templates.md, notebooks.md, gallery.md)
│  ├─ architecture/ (overview, rust-ffi, abi-versioning)
│  ├─ migration/ (from-nethereum.md, from-solnet.md)
│  ├─ contributing/ (build, release, coding-style)
│  └─ faq.md
├─ id/   # struktur identik, bahasa Indonesia
├─ api/  # referensi API (DocFX dari XML doc) + rustdoc
├─ adr/  # Architecture Decision Records
└─ assets/
```

- **Alat**: DocFX (atau MkDocs Material) dengan language switcher EN/ID; di-deploy ke GitHub Pages.
- **Aturan**: setiap PR yang mengubah API publik wajib memperbarui `docs/en` dan `docs/id` (dicek CI via skrip paritas berkas).
- **Root**: `README.md` (EN), `README.id.md` (ID), `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`, `CHANGELOG.md`, `LICENSE` (MIT atau Apache-2.0).
- Contoh kode di docs diambil langsung dari proyek sample yang di-compile CI (snippet include), sehingga tidak ketinggalan.

---

## 15. Testing & Kualitas

| Level | Alat |
| --- | --- |
| Unit Rust | `cargo test`, `proptest`, test vector resmi |
| Fuzzing | `cargo-fuzz` untuk parser/decoder |
| Unit .NET | xUnit v3, FluentAssertions/Shouldly |
| Interop | Test round-trip FFI, uji leak memori, uji multi-thread |
| Integrasi | Testcontainers: Anvil, bitcoind regtest, solana-test-validator, Substrate dev node, simd |
| Cross-check | Bandingkan hasil dengan implementasi referensi (ethers, bitcoinjs, @solana/web3.js, polkadot-js) |
| Performa | BenchmarkDotNet + `criterion`; gate regresi di CI |
| Kualitas | Coverage (coverlet, tarpaulin), analyzer, `clippy`, `rustfmt` |

**Target performa awal** (disesuaikan setelah benchmark nyata): signing ECDSA/EdDSA, derivasi HD, dan hashing batch dibandingkan implementasi managed murni; tujuannya peningkatan signifikan pada operasi batch dan zero-allocation pada jalur panas.

---

## 16. CI/CD & Rilis

- **GitHub Actions**: matrix build Rust per RID → artefak native → pack NuGet → test di OS asli → publish.
- **Versioning**: SemVer, `Nerdbank.GitVersioning`; ABI native berversi terpisah dan dicek runtime.
- **Rilis**: NuGet (library, CLI tool, template, analyzer), Visual Studio Marketplace + Open VSX (extension), GitHub Releases (binary CLI NativeAOT, Gallery desktop), Google Play/TestFlight (Gallery mobile), crates.io (crate Rust yang dapat dipakai mandiri).
- Keamanan rilis: package signing, SBOM, provenance attestation, `cargo deny` dan `dotnet list package --vulnerable` sebagai gate.

---

## 17. Roadmap

| Fase | Cakupan |
| --- | --- |
| **0 — Fondasi** (4–6 minggu) | Monorepo, CI, Rust workspace + FFI skeleton, `Core`, `Native`, SecureBuffer, hashing + secp256k1 + ed25519, docs scaffold EN/ID |
| **1 — EVM** (6–8 minggu) | Akun, keystore, tx 1559, ABI, EIP-712, RPC HTTP/WS, event, generator, ERC-20/721/1155, ENS; CLI dasar; notebook 01–04 |
| **2 — Bitcoin & Solana** (8–10 minggu) | PSBT/Taproot, Esplora/Core RPC; Solana tx v0, SPL, PDA, IDL; Gallery v0.1; template dasar |
| **3 — Cosmos & Polkadot** (8–10 minggu) | SignDoc/gRPC, SS58/sr25519, metadata dinamis, extrinsic; VS Code extension v0.1 |
| **4 — Ekosistem** (6–8 minggu) | Indexer, WalletConnect, hardware wallet, analyzers, Gallery mobile/WASM, semua template |
| **5 — Hardening & 1.0** | Audit eksternal, tuning performa, NativeAOT penuh, dokumentasi final, rilis 1.0 |
| **Setelahnya** | Chain tambahan (TON, Sui/Aptos, Near, Cardano), CosmWasm/ink!, account abstraction (ERC-4337), IBC |

---

## 18. Risiko & Mitigasi

| Risiko | Mitigasi |
| --- | --- |
| Cakupan terlalu luas | Rilis bertahap per chain, API inti stabil dulu |
| Bug kriptografi | Pakai crate teruji, test vector, fuzzing, audit eksternal |
| Kompleksitas distribusi native (banyak RID) | Matrix CI otomatis, fallback managed, `cnet doctor` |
| Perubahan protokol/RPC chain | Adapter terisolasi per chain, test integrasi terhadap node nyata |
| Beban memelihara dokumen dua bahasa | Cek paritas di CI, glosarium istilah EN↔ID |
| Penyalahgunaan sample di mainnet | Safe Sandbox default, analyzer, peringatan di docs |
| Kompatibilitas mobile/WASM untuk native | Tahap lanjut, fallback managed untuk WASM |

---

## 19. Keputusan Terbuka

1. Lisensi: MIT vs Apache-2.0 (Apache-2.0 memberi perlindungan paten).
2. Pakai `csbindgen` atau generator FFI sendiri (uniffi tidak mendukung C# secara resmi).
3. Dukungan WASM: Rust → `wasm32` langsung atau tetap managed fallback.
4. Nama paket NuGet dan prefix org (`Crypto.Net.*` mungkin sudah dipakai, cek ketersediaan).
5. Apakah Indexer menjadi paket inti atau add-on terpisah.

---

## Lampiran A — Pemetaan dari Nethereum

| Fitur Nethereum | Padanan di Crypto.Net |
| --- | --- |
| Web3 / RPC client | `IChainClient` + client per chain |
| Account, HD wallet, keystore | `Crypto.Net.Wallet` + `IAccount`/`ISigner` |
| Function Message / Event DTO | Typed message per chain + source generator |
| Code generation (Autogen) | `cnet gen` + Roslyn source generator |
| Transaction manager & nonce | `ITransactionManager` per chain (nonce / blockhash / sequence) |
| Block crawler / log processor | `Crypto.Net.Indexer` |
| ERC-20/721/1155, ENS, Uniswap, Safe | Paket `Crypto.Net.Evm.*` (Tokens, Ens, Defi, Safe) |
| Blazor/MAUI/Unity | Template + Gallery; Unity pada fase lanjut |
| Testing (Hardhat/Anvil) | `Crypto.Net.Testing` + Testcontainers |