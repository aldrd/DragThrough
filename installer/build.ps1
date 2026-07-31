#Requires -Version 7.0
<#
.SYNOPSIS
    Builds a DragThrough release: the GitHub self-contained installer and the Microsoft Store package.

.DESCRIPTION
    With no switches both artifacts are built, because a release normally needs both. Pass -Installer
    or -Store to build just that one. The version comes from Version.props in every mode.

    GitHub installer:
      1. dotnet publish  -> one self-contained ZombieBar.exe (no .NET runtime needed on the target).
      2. Inno Setup ISCC -> installer\Output\DragThrough-Setup.exe

      Everything a release needs ends up in installer\Output:
        - DragThrough-Setup.exe             the installer
        - ZombieBar.exe                     the raw app exe (the auto-update download asset)
        - publish.json                      the auto-update manifest, with version, url and sha256
                                            filled in for this build

      The auto-update manifest is generated here from scratch, so the repo does not need to keep a
      publish.json checked in. Requires Inno Setup 6 (https://jrsoftware.org/isdl.php); the script
      looks for ISCC.exe in the usual install folders and on PATH.

    Microsoft Store package:
      A single "App package upload file" (.msixupload) bundling x86, x64 and arm64, built via MSBuild
      and ready to upload to Partner Center. Requires Visual Studio with the "Windows application
      packaging" (MSIX) component; a stand-alone Build Tools install does not include the
      DesktopBridge targets the .wapproj needs.

      This leg is built last, so that on a machine without that component you still get the installer
      before the script fails.

.EXAMPLE
    pwsh installer\build.ps1                     # both artifacts
    pwsh installer\build.ps1 -Installer          # only the GitHub installer
    pwsh installer\build.ps1 -Store              # only the Microsoft Store package
    pwsh installer\build.ps1 -Version 1.2.3.0
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime       = "win-x64",
    [string]$Version       = "",
    # GitHub "owner/repo" whose release hosts the update asset, used to build the manifest url.
    [string]$RepoSlug      = "aldrd/DragThrough",
    # Build only the Microsoft Store "App package upload file" (.msixupload).
    [switch]$Store,
    # Build only the GitHub installer.
    [switch]$Installer
)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot "ZombieBar\ZombieBar.csproj"
$issScript  = Join-Path $PSScriptRoot "DragThrough.iss"
$outputDir  = Join-Path $PSScriptRoot "Output"

# The publish path contains the target framework, so read it from the project instead of repeating it
# here - a TFM bump would otherwise leave this pointing at a folder that is never created, and the
# failure would surface much later as a confusing "published exe not found".
$tfm = if ((Get-Content $project -Raw) -match '<TargetFramework>([^<]+)</TargetFramework>') { $Matches[1] }
       else { throw "Could not read <TargetFramework> from $project" }
$publishDir = Join-Path $repoRoot "ZombieBar\bin\$Configuration\$tfm\$Runtime\publish"

# Name of the release asset (the exe the auto-updater downloads). Must match the project's
# published exe and the file name in the manifest url.
$AssetName = "ZombieBar.exe"

# Neither switch given means "build everything"; either one narrows the run to just that artifact.
$anySelected    = $Store -or $Installer
$buildInstaller = $Installer -or -not $anySelected
$buildStore     = $Store     -or -not $anySelected

# Derive the version from Version.props (the single source of truth) when not supplied.
if (-not $Version) {
    $verProps = Join-Path $repoRoot "Version.props"
    if ((Test-Path $verProps) -and ((Get-Content $verProps -Raw) -match '<DragThroughVersion>([^<]+)</DragThroughVersion>')) {
        $Version = $Matches[1]
    }
    else { $Version = "1.0.0.0" }
}

# --- GitHub installer: self-contained exe + generated update manifest + Inno Setup ----------------
function Build-Installer {
    Write-Host "Building DragThrough $Version installer ($Configuration / $Runtime)" -ForegroundColor Cyan

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

    return (Join-Path $outputDir "DragThrough-Setup.exe")
}

# Fails unless every architecture inside the .msixupload declares $Expected as its package identity
# version. Partner Center keys a submission on each package's full name (Name_Version_Arch__Publisher),
# so a package carrying a previously published version is rejected as a duplicate - and the rejection
# names the full name, not the build step that produced it.
#
# The check deliberately reads Identity/@Version out of each package's AppxManifest.xml. File names are
# regenerated from the current version even when a stale generated manifest was reused, so a bundle can
# be named 1.0.22.0 throughout while the packages inside still identify as 1.0.21.0 - which is exactly
# the failure this guards against, and exactly what checking names would miss.
function Assert-BundleIdentityVersion {
    param([Parameter(Mandatory)][string]$UploadPath, [Parameter(Mandatory)][string]$Expected)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $work = Join-Path ([IO.Path]::GetTempPath()) ("dragthrough-bundle-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        # .msixupload is a zip holding the .msixbundle (plus symbol files); the .msixbundle holds one
        # .msix per architecture; each .msix holds the AppxManifest.xml that actually defines identity.
        $outer = [IO.Compression.ZipFile]::OpenRead($UploadPath)
        try {
            $entry = $outer.Entries | Where-Object { $_.Name -like "*.msixbundle" } | Select-Object -First 1
            if (-not $entry) { throw "No .msixbundle found inside $UploadPath" }
            $bundlePath = Join-Path $work "bundle.msixbundle"
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $bundlePath, $true)
        }
        finally { $outer.Dispose() }

        $found = @()
        $bundle = [IO.Compression.ZipFile]::OpenRead($bundlePath)
        try {
            foreach ($pkg in ($bundle.Entries | Where-Object { $_.FullName -match '\.msix$' })) {
                $pkgPath = Join-Path $work $pkg.Name
                [IO.Compression.ZipFileExtensions]::ExtractToFile($pkg, $pkgPath, $true)

                $inner = [IO.Compression.ZipFile]::OpenRead($pkgPath)
                try {
                    $mf = $inner.Entries | Where-Object { $_.FullName -eq "AppxManifest.xml" } | Select-Object -First 1
                    if (-not $mf) { throw "No AppxManifest.xml inside $($pkg.Name)" }
                    $reader = New-Object IO.StreamReader($mf.Open())
                    try { $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
                }
                finally { $inner.Dispose() }

                if ($manifest -notmatch '<Identity[^>]*>') { throw "No <Identity> element in $($pkg.Name)" }
                $identity = $Matches[0]
                $version = if ($identity -match 'Version="([^"]+)"')              { $Matches[1] } else { "unknown" }
                $arch    = if ($identity -match 'ProcessorArchitecture="([^"]+)"') { $Matches[1] } else { "neutral" }
                $found += [pscustomobject]@{ Arch = $arch; Version = $version }
            }
        }
        finally { $bundle.Dispose() }

        if ($found.Count -eq 0) { throw "No .msix packages found inside the bundle." }
        foreach ($f in $found) { Write-Host ("    {0,-8} {1}" -f $f.Arch, $f.Version) -ForegroundColor DarkGray }

        $wrong = @($found | Where-Object { $_.Version -ne $Expected })
        if ($wrong.Count -gt 0) {
            $detail = ($wrong | ForEach-Object { "$($_.Arch)=$($_.Version)" }) -join ', '
            throw "Package identity version mismatch ($detail); expected $Expected everywhere. " +
                  "Partner Center would reject this as a duplicate package full name."
        }
    }
    finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

# --- Microsoft Store: one .msixupload bundling x86, x64 and arm64 ---------------------------------
# The .wapproj already builds the app with the self-updater compiled out (EnableAutoUpdate=false) and
# stamps the manifest version from Version.props, so nothing extra needs passing here. The .msixupload
# is uploaded unsigned - the Store re-signs it.
function Build-StorePackage {
    Write-Host "`nBuilding DragThrough $Version Store package (x86|x64|arm64)..." -ForegroundColor Cyan

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

    # Start from clean packaging output. MSBuild reuses the per-platform AppxManifest.xml it generated
    # into obj\<platform>\, and its incremental check does not notice a version bump in the source
    # manifest. The result is a package whose FILE NAME carries the new version while the Identity inside
    # still carries the old one - Partner Center then rejects the submission because that package's full
    # name collides with an already-published one. Everything removed here is pure build output.
    Write-Host "  Cleaning packaging output..." -ForegroundColor DarkGray
    foreach ($dir in @("obj", "bin", "AppPackages", "BundleArtifacts")) {
        $path = Join-Path $repoRoot "WindowsApplicationPackaging\$dir"
        if (Test-Path $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }

    # The wapproj's StampManifestVersion target syncs Package.appxmanifest with Version.props, but it
    # rewrites that file *during* the build. A bundle build packages the platforms one after another, so
    # the legs that run first can be packaged from the pre-bump manifest while later ones pick up the new
    # value - yielding a bundle whose packages disagree on the version, which the Store rejects. Running
    # the stamp on its own first means every leg starts from an already-correct manifest.
    Write-Host "  Stamping Package.appxmanifest to $Version..." -ForegroundColor DarkGray
    & $msbuild $wapproj -t:StampManifestVersion "-p:Configuration=$Configuration" -p:Platform=x64 -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Stamping Package.appxmanifest failed." }

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

    Write-Host "  Verifying package identities..." -ForegroundColor DarkGray
    Assert-BundleIdentityVersion -UploadPath $upload.FullName -Expected $Version

    return $upload.FullName
}

# --- Run the selected legs -----------------------------------------------------------------------
$installerArtifact = $null
$storeArtifact     = $null

if ($buildInstaller) { $installerArtifact = Build-Installer }
if ($buildStore)     { $storeArtifact     = Build-StorePackage }

Write-Host "`nDone." -ForegroundColor Green
if ($installerArtifact) {
    Write-Host "  GitHub release assets in $outputDir" -ForegroundColor Green
    Write-Host "    DragThrough-Setup.exe" -ForegroundColor Green
    Write-Host "    $AssetName" -ForegroundColor Green
    Write-Host "    publish.json" -ForegroundColor Green
}
if ($storeArtifact) {
    Write-Host "  Upload this to Partner Center:" -ForegroundColor Green
    Write-Host "    $storeArtifact" -ForegroundColor Green
}
