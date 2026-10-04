<#
.SYNOPSIS
  Runs the test suite and packs every Crypto.Net NuGet package into ./artifacts.

.EXAMPLE
  ./scripts/pack.ps1
  ./scripts/pack.ps1 -Version 1.0.1 -SkipTests
#>
param(
    [string]$Version = "",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"
Push-Location $root
try {
    if (-not (Test-Path "runtimes/win-x64/native/cryptonet.dll")) {
        Write-Warning "runtimes/win-x64/native/cryptonet.dll is missing; run ./scripts/build-native.ps1 first."
    }
    if (-not $SkipTests) {
        dotnet test tests/Crypto.Net.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was packed." }
    }
    if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
    $props = @("-c", "Release", "-o", $artifacts)
    if ($Version) { $props += "-p:Version=$Version" }
    dotnet pack Crypto.Net.slnx @props
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }
    Get-ChildItem $artifacts -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor Green }
}
finally {
    Pop-Location
}
