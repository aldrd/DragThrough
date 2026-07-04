#Requires -Version 7.0
<#
.SYNOPSIS
    Tags the last commit with the release version, then pushes the branch and the tag to the remote.

.DESCRIPTION
    1. Derives the version from the ZombieBar csproj <Version> (the single source of truth) unless
       one is passed explicitly - the same rule installer\build.ps1 uses.
    2. Creates an annotated git tag named after the version (e.g. "1.0.15.0") on the current HEAD
       (the last commit).
    3. Pushes the current branch to the remote (all commits).
    4. Pushes the new tag to the remote.

    The tag name matches the release tag build.ps1 and the auto-updater expect, i.e. the
    https://github.com/<repo>/releases/download/<version>/... download url.

.EXAMPLE
    pwsh installer\tag-release.ps1
    pwsh installer\tag-release.ps1 -Version 1.2.3.0
    pwsh installer\tag-release.ps1 -Force        # move the tag if it already exists
#>
param(
    # Release version / tag name. Defaults to the csproj <Version>.
    [string]$Version = "",
    # Remote to push to.
    [string]$Remote  = "origin",
    # Overwrite (move) the tag locally and on the remote if it already exists.
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot "ZombieBar\ZombieBar.csproj"

# Run every git command against the repo root regardless of the caller's working directory.
$G = "-C", $repoRoot

# --- Derive the version from the csproj <Version> when not supplied (same as build.ps1) ---------
if (-not $Version) {
    $csproj = Get-Content $project -Raw
    if     ($csproj -match '<Version>([^<]+)</Version>')         { $Version = $Matches[1] }
    elseif ($csproj -match '<FileVersion>([^<]+)</FileVersion>') { $Version = $Matches[1] }
    else   { throw "Could not find <Version> in $project; pass -Version explicitly." }
}
$Version = $Version.Trim()

# --- Sanity checks ----------------------------------------------------------------------------
& git @G rev-parse --is-inside-work-tree 1>$null 2>$null
if ($LASTEXITCODE -ne 0) { throw "Not a git repository: $repoRoot" }

$branch = (& git @G rev-parse --abbrev-ref HEAD).Trim()
$commit = (& git @G rev-parse --short HEAD).Trim()
Write-Host "Tagging $Version on '$branch' @ $commit -> $Remote" -ForegroundColor Cyan

# --- 1. Tag the last commit -------------------------------------------------------------------
$tagExists = [bool](& git @G tag --list $Version)
if ($tagExists -and -not $Force) {
    throw "Tag '$Version' already exists. Re-run with -Force to move it to the current commit."
}
if ($tagExists) {
    Write-Host "  Tag '$Version' already exists - moving it (-Force)." -ForegroundColor Yellow
}

$tagArgs = @("tag", "-a", $Version, "-m", "Release $Version")
if ($Force) { $tagArgs = @("tag", "-f", "-a", $Version, "-m", "Release $Version") }
& git @G @tagArgs
if ($LASTEXITCODE -ne 0) { throw "git tag failed." }
Write-Host "  Tagged $Version." -ForegroundColor Green

# --- 2. Push the branch (all commits) ---------------------------------------------------------
Write-Host "`nPushing branch '$branch' to $Remote..." -ForegroundColor Cyan
& git @G push $Remote $branch
if ($LASTEXITCODE -ne 0) { throw "git push (branch) failed." }

# --- 3. Push the tag --------------------------------------------------------------------------
Write-Host "Pushing tag '$Version' to $Remote..." -ForegroundColor Cyan
$pushTagArgs = @("push", $Remote, "refs/tags/$Version")
if ($Force) { $pushTagArgs = @("push", "-f", $Remote, "refs/tags/$Version") }
& git @G @pushTagArgs
if ($LASTEXITCODE -ne 0) { throw "git push (tag) failed." }

Write-Host "`nDone. $Version tagged on '$branch' and pushed to $Remote." -ForegroundColor Green
