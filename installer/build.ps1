#Requires -Version 7.0
<#
.SYNOPSIS
    Builds a DragThrough release: the GitHub self-contained installer, or the Microsoft Store package.

.DESCRIPTION
    Default (GitHub installer):
      1. dotnet publish  -> one self-contained ZombieBar.exe (no .NET runtime needed on the target).
      2. Inno Setup ISCC -> installer\Output\DragThrough-Setup-<version>.exe

      Everything a release needs ends up in installer\Output:
        - DragThrough-Setup-<version>.exe   the installer
        - ZombieBar.exe                     the raw app exe (the auto-update download asset)
        - publish.json                      the auto-update manifest, with version, url and sha256
                                            filled in for this build

      The auto-update manifest is generated here from scratch, so the repo does not need to keep a
      publish.json checked in. Requires Inno Setup 6 (https://jrsoftware.org/isdl.php); the script
      looks for ISCC.exe in the usual install folders and on PATH.

    -Store (Microsoft Store package):
      Builds a single "App package upload file" (.msixupload) bundling x86, x64 and arm64 via MSBuild,
      ready to upload to Partner Center. Requires Visual Studio with the "Windows application
      packaging" (MSIX) component. The version comes from Version.props in both modes.

.EXAMPLE
    pwsh installer\build.ps1
    pwsh installer\build.ps1 -Version 1.2.3.0
    pwsh installer\build.ps1 -Store
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime       = "win-x64",
    [string]$Version       = "",
    # GitHub "owner/repo" whose release hosts the update asset, used to build the manifest url.
    [string]$RepoSlug      = "aldrd/DragThrough",
    # Build the Microsoft Store "App package upload file" (.msixupload) instead of the GitHub installer.
    [switch]$Store
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

# Derive the version from Version.props (the single source of truth) when not supplied.
if (-not $Version) {
    $verProps = Join-Path $repoRoot "Version.props"
    if ((Test-Path $verProps) -and ((Get-Content $verProps -Raw) -match '<DragThroughVersion>([^<]+)</DragThroughVersion>')) {
        $Version = $Matches[1]
    }
    else { $Version = "1.0.0.0" }
}

# --- Microsoft Store build (-Store): produce a single .msixupload bundling x86, x64 and arm64 --------
# This packages the app for the Store instead of the GitHub installer. The .wapproj already builds the
# app with the self-updater compiled out (EnableAutoUpdate=false) and stamps the manifest version from
# Version.props, so nothing extra needs passing here. The .msixupload is uploaded unsigned - the Store
# re-signs it. Requires Visual Studio with the "Windows application packaging" (MSIX) component; a
# stand-alone Build Tools install does not include the DesktopBridge targets the .wapproj needs.
if ($Store) {
    Write-Host "Building DragThrough $Version Store package (x86|x64|arm64)..." -ForegroundColor Cyan

    $wapproj = Join-Path $repoRoot "WindowsApplicationPackaging\WindowsApplicationPackaging.wapproj"

    # Locate MSBuild via vswhere. The .wapproj needs the DesktopBridge/MSIX packaging targets, which
    # only exist in a VS install that has the "Windows application packaging" component - NOT every VS
    # install has them (and not Build Tools). So prefer an install whose DesktopBridge.props is present,
    # rather than just the newest VS.
    $msbuild = $null
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installs = & $vswhere -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        foreach ($inst in $installs) {
            $candidate = Join-Path $inst "MSBuild\Current\Bin\MSBuild.exe"
            $bridge    = Join-Path $inst "MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.props"
            if ((Test-Path $candidate) -and (Test-Path $bridge)) { $msbuild = $candidate; break }
        }
        # Nothing with the packaging targets: fall back to the newest MSBuild so the build still runs
        # and fails with a clear MSB4019 that points at the missing component.
        if (-not $msbuild) {
            $msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
                -find "MSBuild\**\Bin\MSBuild.exe" 2>$null | Select-Object -First 1
        }
    }
    if (-not $msbuild) {
        throw "MSBuild.exe not found. Install Visual Studio with the 'Windows application packaging' component, or run this from a Developer PowerShell."
    }
    Write-Host "  MSBuild: $msbuild" -ForegroundColor DarkGray

    & $msbuild `
        $wapproj `
        -restore `
        "-p:Configuration=$Configuration" `
        -p:Platform=x64 `
        -p:AppxBundle=Always `
        "-p:AppxBundlePlatforms=x86|x64|arm64" `
        -p:UapAppxPackageBuildMode=StoreUpload
    if ($LASTEXITCODE -ne 0) { throw "MSBuild Store packaging failed." }

    $appPackages = Join-Path $repoRoot "WindowsApplicationPackaging\AppPackages"
    $upload = Get-ChildItem -Path $appPackages -Filter *.msixupload -Recurse -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $upload) { throw "Build reported success but no .msixupload was found under $appPackages" }

    Write-Host "`nDone. Upload this to Partner Center:" -ForegroundColor Green
    Write-Host "  $($upload.FullName)" -ForegroundColor Green
    return
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
$downloadUrl = "https://github.com/$RepoSlug/releases/download/$Version/$AssetName"
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
