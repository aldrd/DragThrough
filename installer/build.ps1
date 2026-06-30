#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes DragThrough as a self-contained single-file exe and builds the installer.

.DESCRIPTION
    1. dotnet publish  -> one self-contained ZombieBar.exe (no .NET runtime needed on the target).
    2. Inno Setup ISCC -> installer\Output\DragThrough-Setup-<version>.exe

    Everything a release needs ends up in installer\Output:
      - DragThrough-Setup-<version>.exe   the installer
      - ZombieBar.exe                     the raw app exe (the auto-update download asset)
      - publish.json                      the auto-update manifest, with version, url and sha256
                                          filled in for this build

    The auto-update manifest is generated here from scratch, so the repo does not need to keep a
    publish.json checked in.

    Requires Inno Setup 6 (https://jrsoftware.org/isdl.php). The script looks for ISCC.exe
    in the usual install folders and on PATH.

.EXAMPLE
    pwsh installer\build.ps1
    pwsh installer\build.ps1 -Version 1.2.3.0
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime       = "win-x64",
    [string]$Version       = "",
    # GitHub "owner/repo" whose release hosts the update asset, used to build the manifest url.
    [string]$RepoSlug      = "aldrd/DragThrough"
)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot "ZombieBar\ZombieBar.csproj"
$issScript  = Join-Path $PSScriptRoot "DragThrough.iss"
$publishDir = Join-Path $repoRoot "ZombieBar\bin\$Configuration\net10.0-windows\$Runtime\publish"
$outputDir  = Join-Path $PSScriptRoot "Output"

# Name of the release asset (the exe the auto-updater downloads). Must match the project's
# published exe and the file name in the manifest url.
$AssetName = "ZombieBar.exe"

# Derive the version from the csproj <Version> (the single source of truth) when not supplied.
if (-not $Version) {
    $csproj  = Get-Content $project -Raw
    if ($csproj -match '<Version>([^<]+)</Version>') { $Version = $Matches[1] }
    elseif ($csproj -match '<FileVersion>([^<]+)</FileVersion>') { $Version = $Matches[1] }
    else { $Version = "1.0.0.0" }
}
Write-Host "Building DragThrough $Version ($Configuration / $Runtime)" -ForegroundColor Cyan

# --- 1. Publish a self-contained single-file exe -------------------------------------------
Write-Host "`n[1/3] dotnet publish..." -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publishDir $AssetName
if (-not (Test-Path $exe)) { throw "Published exe not found: $exe" }

# --- 2. Stage the release assets in Output (exe + generated update manifest) ----------------
Write-Host "`n[2/3] Staging release assets..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# Copy the raw exe (the auto-update download asset).
Copy-Item -LiteralPath $exe -Destination (Join-Path $outputDir $AssetName) -Force

# Generate the auto-update manifest for this build: version, download url (with the version
# substituted into the release tag) and the sha256 of the exact exe we just published.
$hash        = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToLowerInvariant()
$downloadUrl = "https://github.com/$RepoSlug/releases/download/v$Version/$AssetName"
$manifestJson = @"
{
  "version": "$Version",
  "url": "$downloadUrl",
  "sha256": "$hash"
}
"@
Set-Content -LiteralPath (Join-Path $outputDir "publish.json") -Value $manifestJson -Encoding utf8
Write-Host "  ZombieBar.exe  -> Output\$AssetName" -ForegroundColor Green
Write-Host "  publish.json   -> version $Version, sha256 $hash" -ForegroundColor Green

# --- 3. Compile the installer --------------------------------------------------------------
Write-Host "`n[3/3] Compiling installer with Inno Setup..." -ForegroundColor Cyan

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $iscc) {
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) {
    throw "ISCC.exe (Inno Setup 6) not found. Install it from https://jrsoftware.org/isdl.php"
}

& $iscc "/DMyAppVersion=$Version" "/DPublishDir=$publishDir" $issScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

Write-Host "`nDone. Output folder: $outputDir" -ForegroundColor Green
Write-Host "  DragThrough-Setup-$Version.exe" -ForegroundColor Green
Write-Host "  $AssetName" -ForegroundColor Green
Write-Host "  publish.json" -ForegroundColor Green
