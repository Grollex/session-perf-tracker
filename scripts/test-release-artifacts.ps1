param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRoot = Join-Path $repoRoot "artifacts\release\$Runtime\publish"
$installerRoot = Join-Path $repoRoot "artifacts\release\installer"
$distRoot = Join-Path $repoRoot "artifacts\release\dist"
$updateRoot = Join-Path $repoRoot "artifacts\release\update"

$exePath = Join-Path $publishRoot "SessionPerfTracker.App.exe"
$zipPath = Join-Path $distRoot "SessionPerfTracker-$Version-$Runtime-self-contained.zip"
$installerPath = Join-Path $installerRoot "SessionPerfTracker-$Version-win-x64-setup.exe"
$manifestPath = Join-Path $updateRoot "version.json"
$sha256SumsPath = Join-Path $distRoot "SHA256SUMS.txt"

function Assert-FileExists {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release artifact is missing: $Path"
    }
}

function Get-Sha256 {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Read-Sha256Sums {
    param([string]$Path)

    $entries = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*([A-Fa-f0-9]{64})\s+\*?(.+?)\s*$') {
            $entries[(Split-Path $matches[2] -Leaf)] = $matches[1].ToUpperInvariant()
        }
    }

    return $entries
}

function Assert-Sha256Sum {
    param(
        [hashtable]$Entries,
        [string]$Path
    )

    $fileName = Split-Path $Path -Leaf
    if (-not $Entries.ContainsKey($fileName)) {
        throw "SHA256SUMS.txt does not contain $fileName"
    }

    $actual = Get-Sha256 -Path $Path
    if ($Entries[$fileName] -ne $actual) {
        throw "SHA256 mismatch for $fileName. Expected $($Entries[$fileName]), got $actual"
    }
}

function Assert-Signed {
    param([string]$Path)

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature.SignerCertificate) {
        throw "Artifact is not Authenticode signed: $Path"
    }

    if ($signature.Status -eq "NotSigned") {
        throw "Artifact signature status is NotSigned: $Path"
    }
}

Assert-FileExists -Path $exePath
Assert-FileExists -Path $zipPath
Assert-FileExists -Path $installerPath
Assert-FileExists -Path $manifestPath
Assert-FileExists -Path $sha256SumsPath

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.version -ne $Version) {
    throw "version.json version mismatch. Expected $Version, got $($manifest.version)"
}

if ([string]::IsNullOrWhiteSpace([string]$manifest.installerUrl)) {
    throw "version.json is missing installerUrl."
}

if ([string]::IsNullOrWhiteSpace([string]$manifest.sha256)) {
    throw "version.json is missing sha256."
}

$installerHash = Get-Sha256 -Path $installerPath
if ($installerHash -ne ([string]$manifest.sha256).ToUpperInvariant()) {
    throw "version.json sha256 does not match installer. Expected $($manifest.sha256), got $installerHash"
}

$sha256Entries = Read-Sha256Sums -Path $sha256SumsPath
Assert-Sha256Sum -Entries $sha256Entries -Path $exePath
Assert-Sha256Sum -Entries $sha256Entries -Path $zipPath
Assert-Sha256Sum -Entries $sha256Entries -Path $installerPath
Assert-Sha256Sum -Entries $sha256Entries -Path $manifestPath

if ($RequireSignature) {
    Assert-Signed -Path $exePath
    Assert-Signed -Path $installerPath
}

Write-Host "Release artifacts smoke test passed."
Write-Host "Executable: $exePath"
Write-Host "Installer:  $installerPath"
Write-Host "Manifest:   $manifestPath"
Write-Host "SHA256:     $sha256SumsPath"
