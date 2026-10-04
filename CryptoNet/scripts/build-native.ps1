<#
.SYNOPSIS
  Builds the Rust core (cryptonet) in release mode, runs its tests and copies the library into runtimes/<rid>/native.

.EXAMPLE
  ./scripts/build-native.ps1
  ./scripts/build-native.ps1 -Target aarch64-pc-windows-msvc -Rid win-arm64
#>
param(
    [string]$Target = "",
    [string]$Rid = "",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location (Join-Path $root "rust")
try {
    if (-not $SkipTests) {
        cargo test --release
        if ($LASTEXITCODE -ne 0) { throw "cargo test failed" }
    }
    $cargoArgs = @("build", "--release")
    if ($Target) { $cargoArgs += @("--target", $Target) }
    cargo @cargoArgs
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }

    if (-not $Rid) {
        $os = if ($IsLinux) { "linux" } elseif ($IsMacOS) { "osx" } else { "win" }
        $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) { "Arm64" { "arm64" } default { "x64" } }
        $Rid = "$os-$arch"
    }
    $file = if ($Rid.StartsWith("win")) { "cryptonet.dll" } elseif ($Rid.StartsWith("osx")) { "libcryptonet.dylib" } else { "libcryptonet.so" }
    $built = if ($Target) { Join-Path "target/$Target/release" $file } else { Join-Path "target/release" $file }
    $dest = Join-Path $root "runtimes/$Rid/native"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $built $dest -Force
    Write-Host "Copied $built -> $dest" -ForegroundColor Green
}
finally {
    Pop-Location
}
