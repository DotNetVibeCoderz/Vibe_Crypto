# Building and contributing

[← Documentation](index.md) · [Bahasa Indonesia](../id/kontribusi.md)

## Prerequisites

- .NET 10 SDK
- Rust stable (`rustup`), for the native library
- PowerShell 7 for the scripts (Windows PowerShell 5.1 also works)

## Build and test

```powershell
./scripts/build-native.ps1                 # cargo test --release, cargo build --release, copy to runtimes/<rid>/native
dotnet build Crypto.Net.slnx
dotnet test tests/Crypto.Net.Tests         # unit tests, official vectors, backend parity, mocked RPC
dotnet test tests/Crypto.Net.Tests --filter "FullyQualifiedName~EvmTests.Eip712_MailExample"   # one test
$env:CRYPTONET_LIVE_TESTS = "1"; dotnet test tests/Crypto.Net.Tests --filter "FullyQualifiedName~Live"  # needs internet

cd rust; cargo test -p cryptonet-crypto; cargo clippy --all-targets; cargo fmt --all
```

Cross-compiling the native library: `./scripts/build-native.ps1 -Target aarch64-pc-windows-msvc -Rid win-arm64`
(install the target with `rustup target add` first). Linux and macOS binaries are built the same way on those
systems and placed in `runtimes/linux-x64/native/libcryptonet.so`, `runtimes/osx-arm64/native/libcryptonet.dylib`, etc.

## Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request: it builds the Rust library for `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`, checks `cargo fmt`/`clippy`, runs the .NET
tests on Windows, Linux and macOS with the native library, and packs NuGet packages that contain all six binaries
(downloadable as the `packages` artifact).

## Releasing

```bash
git tag v1.0.0 && git push origin v1.0.0     # runs CI, pushes to nuget.org, creates a GitHub release
```

`.github/workflows/publish.yml` can also be started manually with a version. It uses the `NUGET_API_KEY`
repository secret. Bump `<Version>` in `Directory.Build.props` and `CHANGELOG.md` first.

## Packaging and publishing locally

```powershell
./scripts/pack.ps1 [-Version 1.0.1]        # tests, then .nupkg + .snupkg in ./artifacts
./scripts/publish-nuget.ps1 -WhatIf        # shows what would be pushed
./scripts/publish-nuget.ps1                # asks for confirmation; NuGet versions cannot be deleted
```

The publish script reads the API key from `-ApiKey`, `NUGET_API_KEY`, or the `Nuget Api Key:` line of a
credentials file outside the repository, and never prints it.

## Guidelines

- Every cryptographic change needs an official test vector (BIP, EIP, RFC, SLIP, chain reference implementation)
  and, for primitives, a `BackendParityTests` case.
- Keep the C ABI rules in [architecture.md](architecture.md); bump the ABI minor for additions, major for breaking
  changes.
- Public APIs get XML documentation. User-visible changes update both `docs/en` and `docs/id`, and `CHANGELOG.md`.
- Track progress in `Progress.md`; plan larger work in `PLAN.md`.
