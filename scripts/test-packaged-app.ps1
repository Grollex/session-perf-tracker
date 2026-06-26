param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,
    [int]$StartupTimeoutSeconds = 8
)

$ErrorActionPreference = "Stop"

$resolvedExecutable = Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop
$temporaryProfile = Join-Path ([System.IO.Path]::GetTempPath()) ("SessionPerfTracker-Smoke-" + [guid]::NewGuid().ToString("N"))
$originalLocalAppData = $env:LOCALAPPDATA
$process = $null

try {
    New-Item -ItemType Directory -Path $temporaryProfile -Force | Out-Null
    $env:LOCALAPPDATA = $temporaryProfile

    Write-Host "Starting packaged application: $resolvedExecutable"
    $process = Start-Process -FilePath $resolvedExecutable -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) {
            throw "Packaged application exited during startup with code $($process.ExitCode)."
        }
    }

    Write-Host "Packaged application remained alive for $StartupTimeoutSeconds seconds. Smoke test passed."
}
finally {
    if ($process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }

        $process.Dispose()
    }

    $env:LOCALAPPDATA = $originalLocalAppData
    if (Test-Path -LiteralPath $temporaryProfile) {
        Remove-Item -LiteralPath $temporaryProfile -Recurse -Force
    }
}
