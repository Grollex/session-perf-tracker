param(
    [string]$Version = "",
    [string]$ReleaseNotes = "",
    [switch]$NoOpen
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$repository = "Grollex/session-perf-tracker"

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

Write-Host ""
Write-Host "Release files are ready:" -ForegroundColor Green
Write-Host "  Installer: $installerPath"
Write-Host "  Manifest:  $manifestPath"
Write-Host ""
Write-Host "Create a GitHub Release with tag $tag and upload both files as assets." -ForegroundColor Yellow
Write-Host "Update manifest URL used by the app:"
Write-Host "  https://github.com/$repository/releases/latest/download/version.json"
Write-Host ""

if (-not $NoOpen) {
    Start-Process explorer.exe "/select,`"$installerPath`""
    Start-Process $releaseUrl
}
