param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$BuildInstaller,
    [switch]$NoRestore,
    [string]$InnoCompilerPath = "",
    [string]$InstallerBaseUrl = "",
    [string]$ReleaseNotes = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$appProject = Join-Path $repoRoot "src\SessionPerfTracker.App\SessionPerfTracker.App.csproj"
$publishRoot = Join-Path $repoRoot "artifacts\release\$Runtime\publish"
$installerRoot = Join-Path $repoRoot "artifacts\release\installer"
$distRoot = Join-Path $repoRoot "artifacts\release\dist"
$updateRoot = Join-Path $repoRoot "artifacts\release\update"
$innoScript = Join-Path $repoRoot "installer\inno\SessionPerfTracker.iss"
$exePath = Join-Path $publishRoot "SessionPerfTracker.App.exe"
$zipPath = Join-Path $distRoot "SessionPerfTracker-$Version-$Runtime-self-contained.zip"

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
New-Item -ItemType Directory -Force -Path $installerRoot | Out-Null
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null
New-Item -ItemType Directory -Force -Path $updateRoot | Out-Null

Write-Host "Publishing Session Perf Tracker $Version for $Runtime..."
$publishArgs = @(
    "publish",
    $appProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:PublishReadyToRun=true",
    "-p:PublishTrimmed=false",
    "-p:Version=$Version",
    "-p:AssemblyVersion=$Version.0",
    "-p:FileVersion=$Version.0",
    "-p:InformationalVersion=$Version",
    "-o", $publishRoot,
    "/m:1"
)

if ($NoRestore) {
    $publishArgs += "--no-restore"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$requiredFiles = @(
    "SessionPerfTracker.App.exe",
    "SessionPerfTracker.Domain.dll",
    "SessionPerfTracker.Infrastructure.dll",
    "Microsoft.Data.Sqlite.dll",
    "SQLitePCLRaw.core.dll"
)

foreach ($file in $requiredFiles) {
    $path = Join-Path $publishRoot $file
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required publish file is missing: $file"
    }
}

$sqliteNative = Get-ChildItem -Path $publishRoot -Recurse -Filter "e_sqlite3.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $sqliteNative) {
    throw "SQLite native runtime e_sqlite3.dll was not found in publish output."
}

Write-Host "Publish output: $publishRoot"
Write-Host "SQLite native runtime: $($sqliteNative.FullName)"
Write-Host "App executable: $exePath"

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath -Force
Write-Host "Self-contained zip output: $zipPath"

if (-not $BuildInstaller) {
    Write-Host "Installer build skipped. Re-run with -BuildInstaller to compile the Inno Setup installer."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) {
        $InnoCompilerPath = $command.Source
    }
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath) -or -not (Test-Path -LiteralPath $InnoCompilerPath)) {
    $commonPaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $InnoCompilerPath = $commonPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath) -or -not (Test-Path -LiteralPath $InnoCompilerPath)) {
    throw "Inno Setup compiler was not found. Install Inno Setup 6, then re-run: .\scripts\package-windows.ps1 -BuildInstaller"
}

Write-Host "Building installer with $InnoCompilerPath..."
& $InnoCompilerPath `
    "/DSourceDir=$publishRoot" `
    "/DOutputDir=$installerRoot" `
    "/DMyAppVersion=$Version" `
    $innoScript

$installer = Join-Path $installerRoot "SessionPerfTracker-$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Installer build finished, but expected output was not found: $installer"
}

Write-Host "Installer output: $installer"

$installerFileName = Split-Path $installer -Leaf
$installerUrl = if ([string]::IsNullOrWhiteSpace($InstallerBaseUrl)) {
    "../installer/$installerFileName"
} else {
    "$($InstallerBaseUrl.TrimEnd('/'))/$installerFileName"
}
$sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
$manifest = [ordered]@{
    version = $Version
    installerUrl = $installerUrl
    releaseNotes = if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) { "Session Perf Tracker $Version" } else { $ReleaseNotes }
    publishedAt = (Get-Date).ToUniversalTime().ToString("o")
    sha256 = $sha256
}
$manifestPath = Join-Path $updateRoot "version.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Update manifest output: $manifestPath"
