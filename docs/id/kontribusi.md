# Build dan kontribusi

[← Dokumentasi](index.md) · [English](../en/contributing.md)

## Prasyarat

- .NET 10 SDK
- Rust stable (`rustup`), untuk library native
- PowerShell 7 untuk skrip (Windows PowerShell 5.1 juga bisa)

## Build dan test

```powershell
./scripts/build-native.ps1                 # cargo test --release, cargo build --release, salin ke runtimes/<rid>/native
dotnet build Crypto.Net.slnx
dotnet test tests/Crypto.Net.Tests         # unit test, vector resmi, paritas backend, RPC mock
dotnet test tests/Crypto.Net.Tests --filter "FullyQualifiedName~EvmTests.Eip712_MailExample"   # satu test
$env:CRYPTONET_LIVE_TESTS = "1"; dotnet test tests/Crypto.Net.Tests --filter "FullyQualifiedName~Live"  # butuh internet

cd rust; cargo test -p cryptonet-crypto; cargo clippy --all-targets; cargo fmt --all
```

Cross-compile library native: `./scripts/build-native.ps1 -Target aarch64-pc-windows-msvc -Rid win-arm64`
(pasang target dengan `rustup target add` terlebih dahulu). Binary Linux dan macOS dibangun dengan cara yang sama di
sistem tersebut dan diletakkan di `runtimes/linux-x64/native/libcryptonet.so`, `runtimes/osx-arm64/native/libcryptonet.dylib`, dst.

## Continuous integration

`.github/workflows/ci.yml` berjalan di setiap push dan pull request: membangun library Rust untuk `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` dan `osx-arm64`, memeriksa `cargo fmt`/`clippy`, menjalankan
test .NET di Windows, Linux dan macOS dengan library native, dan membuat paket NuGet yang berisi keenam binary
(bisa diunduh sebagai artifact `packages`).

## Merilis

```bash
git tag v1.0.0 && git push origin v1.0.0     # menjalankan CI, push ke nuget.org, membuat GitHub release
```

`.github/workflows/publish.yml` juga bisa dijalankan manual dengan versi tertentu. Workflow ini memakai secret
repositori `NUGET_API_KEY`. Naikkan `<Version>` di `Directory.Build.props` dan `CHANGELOG.md` terlebih dahulu.

## Packaging dan publikasi lokal

```powershell
./scripts/pack.ps1 [-Version 1.0.1]        # test, lalu .nupkg + .snupkg di ./artifacts
./scripts/publish-nuget.ps1 -WhatIf        # menampilkan apa yang akan di-push
./scripts/publish-nuget.ps1                # meminta konfirmasi; versi NuGet tidak bisa dihapus
```

Skrip publish membaca API key dari `-ApiKey`, `NUGET_API_KEY`, atau baris `Nuget Api Key:` di berkas kredensial
di luar repositori, dan tidak pernah menampilkannya.

## Pedoman

- Setiap perubahan kriptografi butuh test vector resmi (BIP, EIP, RFC, SLIP, implementasi referensi chain) dan,
  untuk primitif, kasus di `BackendParityTests`.
- Ikuti aturan C ABI di [arsitektur.md](arsitektur.md); naikkan minor ABI untuk penambahan, major untuk perubahan
  yang memutus kompatibilitas.
- API publik wajib punya dokumentasi XML. Perubahan yang terlihat pengguna memperbarui `docs/en` dan `docs/id`,
  serta `CHANGELOG.md`.
- Catat progres di `Progress.md`; rencanakan pekerjaan besar di `PLAN.md`.
