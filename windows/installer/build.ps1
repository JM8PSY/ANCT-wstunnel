# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club
#
# Builds the installer: downloads the WireGuard MSI (windows/VERSIONS) and verifies its sha256,
# builds wstunnel from the pinned commit, publishes App + Service self-contained single-file,
# then compiles WgVpn.iss with Inno Setup. Nothing is executed from the MSI; the wstunnel build
# runs upstream's and its dependencies' build scripts, and the result is run once (--version).
# Fails hard on any error.
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

# git and cargo write progress to stderr, which Windows PowerShell can turn into a terminating error.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$What failed ($LASTEXITCODE)" }
}

function Find-Cargo {
    $cmd = Get-Command cargo.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidate = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
    if (Test-Path $candidate) { return $candidate }
    throw @"
cargo.exe was not found. Install rustup (winget install Rustlang.Rustup) and the Visual Studio
C++ build tools, then run this script again.
"@
}

# Returns the path of wstunnel.exe, built from exactly the pinned commit.
function Build-Wstunnel($Versions, [string]$SourceDir) {
    $commit = $Versions.WSTUNNEL_COMMIT
    $cargo = Find-Cargo
    New-Item -ItemType Directory -Force $SourceDir | Out-Null
    if (-not (Test-Path (Join-Path $SourceDir '.git'))) {
        Invoke-Native 'git init' { git -C $SourceDir init --quiet }
    }
    # autocrlf off so Cargo.lock and the sources are byte-identical to the commit.
    Invoke-Native 'git config' { git -C $SourceDir config core.autocrlf false }
    $present = git -C $SourceDir rev-parse --quiet --verify "$commit^{commit}"
    if (-not $present) {
        Write-Host "Fetching wstunnel $($Versions.WSTUNNEL_VERSION) ($commit)"
        Invoke-Native 'git fetch' { git -C $SourceDir fetch --quiet --depth 1 $Versions.WSTUNNEL_GIT_URL $commit }
    }
    Invoke-Native 'git checkout' { git -C $SourceDir checkout --quiet --force --detach $commit }
    Invoke-Native 'git clean' { git -C $SourceDir clean -qfdx -e target }
    $head = git -C $SourceDir rev-parse HEAD
    if ($head -ne $commit) { throw "wstunnel checkout is at $head, expected $commit" }

    # The v11.1.0 tag's Cargo.lock still says 11.0.0 for the two workspace crates, which --locked
    # rejects. Correct only those two versions; --locked then still refuses any dependency change.
    $packageVersion = $Versions.WSTUNNEL_VERSION.TrimStart('v')
    $lockPath = Join-Path $SourceDir 'Cargo.lock'
    $lock = [IO.File]::ReadAllText($lockPath)
    $lock = [regex]::Replace($lock, '(name = "wstunnel(?:-cli)?"\r?\nversion = ")[^"]+(")', "`${1}$packageVersion`${2}")
    [IO.File]::WriteAllText($lockPath, $lock, (New-Object Text.UTF8Encoding $false))

    # Run from the source root: cargo only reads upstream's .cargo/config.toml from there.
    $targetDir = Join-Path $SourceDir 'target'
    $env:RUSTUP_TOOLCHAIN = $Versions.WSTUNNEL_RUST_TOOLCHAIN
    Push-Location $SourceDir
    try {
        Invoke-Native 'cargo build (wstunnel)' {
            & $cargo build --release --locked --no-default-features --features $Versions.WSTUNNEL_CARGO_FEATURES `
                --bin wstunnel --target-dir $targetDir
        }
    } finally {
        Pop-Location
        Remove-Item Env:RUSTUP_TOOLCHAIN
    }

    # Running it catches a wrong version and a block by Smart App Control or Defender on this PC.
    # NO_COLOR is cleared because wstunnel rejects a value such as 1 inherited from the shell.
    $exe = Join-Path $targetDir 'release\wstunnel.exe'
    $savedNoColor = $env:NO_COLOR
    Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
    try { $reported = & $exe --version } finally { if ($savedNoColor) { $env:NO_COLOR = $savedNoColor } }
    if ($LASTEXITCODE -ne 0 -or "$reported" -notmatch "\b$([regex]::Escape($packageVersion))\b") {
        throw "Built wstunnel.exe reported '$reported' (exit $LASTEXITCODE), expected version $packageVersion"
    }
    Write-Host "wstunnel built: $reported sha256=$((Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant())"
    $exe
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

# 1. Pinned download (verified before anything is copied).
$wgMsi = Get-Pinned $v.WIREGUARD_URL $v.WIREGUARD_SHA256_AMD64

# 2. Build wstunnel from the pinned commit. Drop what the old official-binary flow left behind.
$legacyWsDir = Join-Path $build 'wstunnel'
if (Test-Path $legacyWsDir) { Remove-Item $legacyWsDir -Recurse -Force }
Remove-Item (Join-Path $build 'wstunnel_*_windows_amd64.tar.gz') -Force -ErrorAction SilentlyContinue
$wsSrc = Join-Path $build 'wstunnel-src'
$wsExe = Build-Wstunnel $v $wsSrc

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
Copy-Item $wsExe (Join-Path $stage 'wstunnel.exe')
Copy-Item (Join-Path $wsSrc 'LICENSE') (Join-Path $stage 'LICENSE-wstunnel.txt')
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
