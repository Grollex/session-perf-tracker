# Gemini CLI Launcher Script
$ErrorActionPreference = 'Continue'
$InstallDir = $PSScriptRoot
$ConfigPath = Join-Path $InstallDir "config.json"

Write-Host "--- Gemini CLI Autonomous Agent ---" -ForegroundColor Cyan

# 1. Check Node.js
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Host "[!] Node.js not found!" -ForegroundColor Yellow
    Write-Host "[*] Please install Node.js from https://nodejs.org/"
    Write-Host "[*] Press any key to open the download page or Ctrl+C to cancel..."
    $null = [Console]::ReadKey()
    Start-Process "https://nodejs.org/en/download/"
    exit
}

# 2. Check/Install Gemini CLI
if (-not (Get-Command gemini -ErrorAction SilentlyContinue)) {
    Write-Host "[*] Gemini CLI not found globally. Installing..." -ForegroundColor Cyan
    npm install -g @google/gemini-cli@latest
}

# 3. Auth Choice (if not configured and not logged in)
if (-not (Test-Path $ConfigPath)) {
    Write-Host "`nChoose your authentication method:" -ForegroundColor Green
    Write-Host "1) Google Account Login (Recommended, uses browser)"
    Write-Host "2) API Key (For headless/background usage)"
    
    $choice = Read-Host "Select [1 or 2]"
    
    if ($choice -eq "2") {
        $key = Read-Host "Please enter your Google AI Studio API Key"
        if ($key) {
            @{ "GOOGLE_API_KEY" = $key } | ConvertTo-Json | Out-File $ConfigPath
            Write-Host "[+] API Key saved to config.json" -ForegroundColor Green
        }
    } else {
        Write-Host "[*] Launching Google Login..." -ForegroundColor Cyan
        gemini login
        # Create an empty config to mark as 'configured'
        @{ "AUTH_TYPE" = "google_login" } | ConvertTo-Json | Out-File $ConfigPath
    }
}

# 4. Load Environment if using API Key
if (Test-Path $ConfigPath) {
    $config = Get-Content $ConfigPath | ConvertFrom-Json
    if ($config.GOOGLE_API_KEY) {
        $env:GOOGLE_API_KEY = $config.GOOGLE_API_KEY
        Write-Host "[INFO] Using API Key authentication." -ForegroundColor Gray
    } else {
        Write-Host "[INFO] Using Google Account authentication." -ForegroundColor Gray
    }
}

# 5. Launch
Write-Host "[+] Starting Gemini CLI..." -ForegroundColor Green
gemini --skip-trust
