# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club
#
# Builds the installer: downloads the pinned artifacts (windows/VERSIONS), verifies sha256,
# publishes App + Service self-contained single-file, then compiles WgVpn.iss with Inno Setup.
# Nothing is executed from the downloaded files. Fails hard on any error.
#   pwsh windows/installer/build.ps1            (or: powershell -File windows\installer\build.ps1)
#   -SkipInstaller   stop after publish (no Inno Setup needed)

[CmdletBinding()]
param([switch]$SkipInstaller)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here    = $PSScriptRoot
$winRoot = Split-Path $here -Parent
$repo    = Split-Path $winRoot -Parent
$build   = Join-Path $here 'build'
$stage   = Join-Path $build 'stage'

function Read-Versions {
    $map = @{}
    foreach ($line in Get-Content (Join-Path $winRoot 'VERSIONS')) {
        if ($line -match '^\s*([A-Z0-9_]+)=(.+)$') { $map[$Matches[1]] = $Matches[2].Trim() }
    }
    $map
}

function Get-Pinned([string]$Url, [string]$Sha256) {
    $file = Join-Path $build (Split-Path $Url -Leaf)
    if (-not (Test-Path $file)) {
        Write-Host "Downloading $Url"
        Invoke-WebRequest -Uri $Url -OutFile $file -UseBasicParsing
    }
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        Remove-Item $file -Force
        throw "sha256 mismatch for $(Split-Path $Url -Leaf): expected $Sha256, got $actual. File deleted."
    }
    Write-Host "sha256 ok: $(Split-Path $Url -Leaf)"
    $file
}

function Find-Iscc {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $null
}

New-Item -ItemType Directory -Force $build, $stage | Out-Null
$v = Read-Versions

# 1. Pinned downloads (verified before anything is extracted or copied).
$wsTar = Get-Pinned $v.WSTUNNEL_URL $v.WSTUNNEL_SHA256_AMD64
$wgMsi = Get-Pinned $v.WIREGUARD_URL $v.WIREGUARD_SHA256_AMD64

# 2. Extract wstunnel.exe and its LICENSE.
$wsDir = Join-Path $build 'wstunnel'
if (Test-Path $wsDir) { Remove-Item $wsDir -Recurse -Force }
New-Item -ItemType Directory $wsDir | Out-Null
# Windows bsdtar explicitly: a GNU tar earlier on PATH misreads "C:" as a remote host.
& (Join-Path $env:SystemRoot 'System32\tar.exe') -xzf $wsTar -C $wsDir wstunnel.exe LICENSE
if ($LASTEXITCODE -ne 0) { throw "tar extraction failed ($LASTEXITCODE)" }
foreach ($f in 'wstunnel.exe', 'LICENSE') {
    if (-not (Test-Path (Join-Path $wsDir $f))) { throw "$f missing after extraction" }
}

# 3. Publish App and Service.
$appOut = Join-Path $build 'app'
$svcOut = Join-Path $build 'service'
foreach ($d in $appOut, $svcOut) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }

dotnet publish (Join-Path $winRoot 'WgVpn.App\WgVpn.App.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $appOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (App) failed ($LASTEXITCODE)" }
dotnet publish (Join-Path $winRoot 'WgVpn.Service\WgVpn.Service.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o $svcOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Service) failed ($LASTEXITCODE)" }

foreach ($exe in (Join-Path $appOut 'WgVpn.App.exe'), (Join-Path $svcOut 'WgVpn.Service.exe')) {
    if (-not (Test-Path $exe)) { throw "Expected publish output missing: $exe" }
    '{0}: {1:N1} MiB' -f $exe, ((Get-Item $exe).Length / 1MB) | Write-Host
}

# 4. Stage exactly what the installer packs.
Remove-Item "$stage\*" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $appOut 'WgVpn.App.exe') $stage
Copy-Item (Join-Path $svcOut 'WgVpn.Service.exe') $stage
Copy-Item (Join-Path $wsDir 'wstunnel.exe') $stage
Copy-Item (Join-Path $wsDir 'LICENSE') (Join-Path $stage 'LICENSE-wstunnel.txt')
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $stage 'LICENSE-GPL-3.0.txt')
Copy-Item $wgMsi (Join-Path $stage 'wireguard-amd64.msi')

if ($SkipInstaller) { Write-Host "Publish done (installer skipped). Staged in $stage"; return }

# 5. Inno Setup.
$iscc = Find-Iscc
if (-not $iscc) {
    Write-Error @"
ISCC.exe (Inno Setup 6) was not found on PATH or in the standard install folders.
Install it (no admin needed):  winget install JRSoftware.InnoSetup --scope user
or download from https://jrsoftware.org/isdl.php, then run this script again.
"@
    exit 1
}
& $iscc "/DStageDir=$stage" "/DWgVpnVersion=$($v.WGVPN_VERSION)" (Join-Path $here 'WgVpn.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Write-Host "Installer written to $(Join-Path $here 'output')"
