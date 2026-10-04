<#
.SYNOPSIS
  Publishes the packages in ./artifacts to NuGet.

.DESCRIPTION
  Releases normally go through GitHub Actions (.github/workflows/publish.yml, triggered by a v* tag) using the
  NUGET_API_KEY repository secret. This script is the local fallback.

  The API key is read, in order, from -ApiKey, the NUGET_API_KEY environment variable, or the
  "Nuget Api Key: <key>" line of -CredentialsFile (keep that file outside the repository). The key is never printed.

  NuGet versions are immutable: a version can be unlisted but never deleted or re-uploaded.
  The script therefore asks for confirmation unless -Force is given.

.EXAMPLE
  ./scripts/pack.ps1
  ./scripts/publish-nuget.ps1            # interactive confirmation
  ./scripts/publish-nuget.ps1 -WhatIf    # show what would be pushed
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ApiKey = "",
    [string]$CredentialsFile = "",
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [switch]$Force
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"

$packages = @(Get-ChildItem $artifacts -Filter *.nupkg -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike "*.symbols.nupkg" })
if ($packages.Count -eq 0) { throw "No packages in $artifacts. Run ./scripts/pack.ps1 first." }

if (-not $ApiKey) { $ApiKey = $env:NUGET_API_KEY }
if (-not $ApiKey) {
    if (-not $CredentialsFile -or -not (Test-Path $CredentialsFile)) { throw "No API key: pass -ApiKey, set NUGET_API_KEY or provide -CredentialsFile." }
    $line = Get-Content $CredentialsFile | Where-Object { $_ -match '^\s*nuget\s+api\s+key\s*:' } | Select-Object -First 1
    if (-not $line) { throw "No 'Nuget Api Key:' line found in $CredentialsFile" }
    $ApiKey = ($line -split ':', 2)[1].Trim()
}

Write-Host "Packages to publish to ${Source}:" -ForegroundColor Cyan
$packages | ForEach-Object { Write-Host "  $($_.Name)" }

if (-not $Force -and -not $WhatIfPreference) {
    $answer = Read-Host "NuGet versions cannot be deleted once published. Type 'publish' to continue"
    if ($answer -ne "publish") { Write-Host "Cancelled."; return }
}

foreach ($pkg in $packages) {
    if ($PSCmdlet.ShouldProcess($pkg.Name, "dotnet nuget push")) {
        dotnet nuget push $pkg.FullName --api-key $ApiKey --source $Source --skip-duplicate
        if ($LASTEXITCODE -ne 0) { throw "Push failed for $($pkg.Name)" }
    }
}
Write-Host "Done. Packages appear on nuget.org after validation and indexing (usually a few minutes)." -ForegroundColor Green
