param(
    [string]$Version = "",
    [string]$ReleaseNotes = "",
    [switch]$SkipUpload,
    [switch]$NoOpen
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$repository = "Grollex/session-perf-tracker"

function Add-LocalToolPaths {
    $paths = @(
        (Join-Path $env:LOCALAPPDATA "Programs\GitHub CLI\bin"),
        "C:\Program Files\GitHub CLI",
        "C:\Program Files\GitHub CLI\bin",
        "C:\Program Files\Git\cmd"
    )

    foreach ($path in $paths) {
        if ((Test-Path -LiteralPath $path) -and (($env:Path -split ";") -notcontains $path)) {
            $env:Path = "$path;$env:Path"
        }
    }
}

function Get-CommandPathOrNull([string]$name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

function Ensure-GitHubCliAuth {
    $ghPath = Get-CommandPathOrNull "gh"
    if (-not $ghPath) {
        throw "GitHub CLI was not found. Install it from https://cli.github.com/ or run the prepared portable setup again."
    }

    Write-Host "Using GitHub CLI: $ghPath" -ForegroundColor DarkGray
    & gh auth status --hostname github.com *> $null
    if ($LASTEXITCODE -eq 0) {
        return
    }

    Write-Host ""
    Write-Host "GitHub CLI is not authenticated yet." -ForegroundColor Yellow
    Write-Host "A browser login will open. Sign in as an account that can publish releases to $repository." -ForegroundColor Yellow
    Write-Host ""
    & gh auth login --hostname github.com --web --scopes "repo"
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI authentication failed."
    }

    & gh auth status --hostname github.com
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI is still not authenticated."
    }
}

function Publish-GitHubRelease {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$ReleaseNotes,
        [Parameter(Mandatory = $true)][string]$Repository
    )

    Push-Location $repoRoot
    try {
        $gitPath = Get-CommandPathOrNull "git"
        if ($gitPath) {
            $insideGit = $false
            & git rev-parse --is-inside-work-tree *> $null
            $insideGit = $LASTEXITCODE -eq 0

            if ($insideGit) {
                Write-Host "Pushing current branch before release..." -ForegroundColor Cyan
                & git push
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "Git push failed or was skipped. Continuing with release upload." -ForegroundColor Yellow
                }

                & git rev-parse -q --verify "refs/tags/$Tag" *> $null
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "Creating local tag $Tag..." -ForegroundColor Cyan
                    & git tag -a $Tag -m "Session Perf Tracker $Version"
                    if ($LASTEXITCODE -ne 0) {
                        throw "Could not create local tag $Tag."
                    }
                }

                Write-Host "Pushing tag $Tag..." -ForegroundColor Cyan
                & git push origin $Tag
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "Tag push failed, possibly because it already exists remotely. Continuing." -ForegroundColor Yellow
                }
            }
        }

        & gh release view $Tag --repo $Repository *> $null
        $releaseExists = $LASTEXITCODE -eq 0

        if ($releaseExists) {
            Write-Host "GitHub Release $Tag already exists. Uploading assets with --clobber..." -ForegroundColor Cyan
            & gh release upload $Tag $InstallerPath $ManifestPath --repo $Repository --clobber
            if ($LASTEXITCODE -ne 0) {
                throw "GitHub Release asset upload failed."
            }
        }
        else {
            Write-Host "Creating GitHub Release $Tag and uploading assets..." -ForegroundColor Cyan
            & gh release create $Tag $InstallerPath $ManifestPath `
                --repo $Repository `
                --title "Session Perf Tracker $Version" `
                --notes $ReleaseNotes `
                --latest
            if ($LASTEXITCODE -ne 0) {
                throw "GitHub Release creation failed."
            }
        }
    }
    finally {
        Pop-Location
    }
}

Add-LocalToolPaths

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-Host "Release version, for example 0.1.2"
}

$Version = $Version.Trim().TrimStart("v")
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 0.1.2. Pre-release labels are not supported by the current Windows installer metadata."
}

if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $ReleaseNotes = "Session Perf Tracker $Version"
}

$tag = "v$Version"
$installerBaseUrl = "https://github.com/$repository/releases/download/$tag"
$packageScript = Join-Path $repoRoot "scripts\package-windows.ps1"

Write-Host "Building Session Perf Tracker $Version..." -ForegroundColor Cyan
& $packageScript `
    -Version $Version `
    -BuildInstaller `
    -InstallerBaseUrl $installerBaseUrl `
    -ReleaseNotes $ReleaseNotes

if ($LASTEXITCODE -ne 0) {
    throw "Release packaging failed with exit code $LASTEXITCODE"
}

$installerPath = Join-Path $repoRoot "artifacts\release\installer\SessionPerfTracker-$Version-win-x64-setup.exe"
$manifestPath = Join-Path $repoRoot "artifacts\release\update\version.json"

if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Expected installer was not created: $installerPath"
}

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Expected update manifest was not created: $manifestPath"
}

$releaseUrl = "https://github.com/$repository/releases/new?tag=$tag&title=Session%20Perf%20Tracker%20$Version"
$publishedReleaseUrl = "https://github.com/$repository/releases/tag/$tag"

if (-not $SkipUpload) {
    Ensure-GitHubCliAuth
    Publish-GitHubRelease `
        -Tag $tag `
        -Version $Version `
        -InstallerPath $installerPath `
        -ManifestPath $manifestPath `
        -ReleaseNotes $ReleaseNotes `
        -Repository $repository
}

Write-Host ""
Write-Host "Release files are ready:" -ForegroundColor Green
Write-Host "  Installer: $installerPath"
Write-Host "  Manifest:  $manifestPath"
Write-Host ""
if ($SkipUpload) {
    Write-Host "Upload was skipped. Create a GitHub Release with tag $tag and upload both files as assets." -ForegroundColor Yellow
}
else {
    Write-Host "GitHub Release was created/updated:" -ForegroundColor Green
    Write-Host "  $publishedReleaseUrl"
}
Write-Host "Update manifest URL used by the app:"
Write-Host "  https://github.com/$repository/releases/latest/download/version.json"
Write-Host ""

if (-not $NoOpen) {
    Start-Process explorer.exe "/select,`"$installerPath`""
    if ($SkipUpload) {
        Start-Process $releaseUrl
    }
    else {
        Start-Process $publishedReleaseUrl
    }
}
