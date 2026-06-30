param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$BuildInstaller,
    [switch]$NoRestore,
    [string]$InnoCompilerPath = "",
    [string]$InstallerBaseUrl = "",
    [string]$ReleaseNotes = "",
    [string]$SigningPfxPath = "",
    [string]$SigningPfxPassword = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipSigning
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
$sha256SumsPath = Join-Path $distRoot "SHA256SUMS.txt"

function Find-SignTool {
    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $sdkRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    )

    foreach ($sdkRoot in $sdkRoots) {
        if (-not (Test-Path -LiteralPath $sdkRoot)) {
            continue
        }

        $candidate = Get-ChildItem -Path $sdkRoot -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($candidate) {
            return $candidate.FullName
        }
    }

    return $null
}

function Initialize-CodeSigning {
    if ($SkipSigning) {
        Write-Host "Code signing skipped by -SkipSigning." -ForegroundColor Yellow
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($SigningPfxPath) -and [string]::IsNullOrWhiteSpace($SigningPfxPassword)) {
        Write-Host "Code signing skipped. Pass -SigningPfxPath and -SigningPfxPassword to sign release artifacts." -ForegroundColor Yellow
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($SigningPfxPath) -or [string]::IsNullOrWhiteSpace($SigningPfxPassword)) {
        throw "Both -SigningPfxPath and -SigningPfxPassword are required for code signing, or use -SkipSigning."
    }

    $resolvedPfx = Resolve-Path -LiteralPath $SigningPfxPath -ErrorAction SilentlyContinue
    if (-not $resolvedPfx) {
        throw "Signing PFX was not found: $SigningPfxPath"
    }

    $script:SigningPfxPath = $resolvedPfx.Path
    $signToolPath = Find-SignTool
    if ($signToolPath) {
        Write-Host "Using signtool: $signToolPath"
        return [pscustomobject]@{
            Mode = "SignTool"
            SignToolPath = $signToolPath
            Certificate = $null
        }
    }

    Write-Host "signtool.exe was not found. Falling back to Set-AuthenticodeSignature." -ForegroundColor Yellow
    $securePassword = ConvertTo-SecureString $SigningPfxPassword -AsPlainText -Force
    $certificate = Import-PfxCertificate `
        -FilePath $script:SigningPfxPath `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -Password $securePassword

    if (-not $certificate -or -not $certificate.HasPrivateKey) {
        throw "Could not import a code signing certificate with a private key from $script:SigningPfxPath"
    }

    return [pscustomobject]@{
        Mode = "PowerShell"
        SignToolPath = $null
        Certificate = $certificate
    }
}

function Invoke-CodeSigning {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)]$SigningContext
    )

    if (-not (Test-Path -LiteralPath $FilePath)) {
        throw "Cannot sign missing file: $FilePath"
    }

    Write-Host "Signing: $FilePath"
    if ($SigningContext.Mode -eq "SignTool") {
        & $SigningContext.SignToolPath sign /f $SigningPfxPath /p $SigningPfxPassword /fd SHA256 /tr $TimestampUrl /td SHA256 $FilePath
        if ($LASTEXITCODE -ne 0) {
            throw "signtool sign failed for $FilePath with exit code $LASTEXITCODE"
        }

        & $SigningContext.SignToolPath verify /pa /v $FilePath
        if ($LASTEXITCODE -ne 0) {
            throw "signtool verify failed for $FilePath with exit code $LASTEXITCODE"
        }

        return
    }

    $signature = Set-AuthenticodeSignature `
        -FilePath $FilePath `
        -Certificate $SigningContext.Certificate `
        -HashAlgorithm SHA256 `
        -TimestampServer $TimestampUrl

    if ($signature.Status -eq "NotSigned") {
        throw "Set-AuthenticodeSignature did not sign $FilePath"
    }

    $verification = Get-AuthenticodeSignature -FilePath $FilePath
    if ($verification.Status -eq "NotSigned") {
        throw "Authenticode signature verification failed for $FilePath"
    }

    Write-Host "Signature status: $($verification.Status)"
}

function Write-Sha256Sums {
    param(
        [Parameter(Mandatory = $true)][string[]]$Paths,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $lines = foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path) {
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $(Split-Path -Leaf $path)"
        }
    }

    $lines | Set-Content -LiteralPath $OutputPath -Encoding ASCII
    Write-Host "SHA256 sums output: $OutputPath"
}

$codeSigning = Initialize-CodeSigning

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

if ($codeSigning) {
    Invoke-CodeSigning -FilePath $exePath -SigningContext $codeSigning
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath -Force
Write-Host "Self-contained zip output: $zipPath"

if (-not $BuildInstaller) {
    Write-Sha256Sums -Paths @($exePath, $zipPath) -OutputPath $sha256SumsPath
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
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $installerRoot "SessionPerfTracker-$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Installer build finished, but expected output was not found: $installer"
}

Write-Host "Installer output: $installer"

if ($codeSigning) {
    Invoke-CodeSigning -FilePath $installer -SigningContext $codeSigning
}

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

Write-Sha256Sums -Paths @($exePath, $zipPath, $installer, $manifestPath) -OutputPath $sha256SumsPath
