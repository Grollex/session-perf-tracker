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

# 2. Check for API Key
if (-not (Test-Path $ConfigPath)) {
    Write-Host "[?] No API Key found." -ForegroundColor Yellow
    $key = Read-Host "Please enter your Google AI Studio API Key (from https://aistudio.google.com/app/apikey)"
    if ($key) {
        @{ "GOOGLE_API_KEY" = $key } | ConvertTo-Json | Out-File $ConfigPath
        Write-Host "[+] API Key saved to config.json" -ForegroundColor Green
    } else {
        Write-Host "[!] No API key provided. Exiting..." -ForegroundColor Red
        exit
    }
}

# 3. Load API Key
$config = Get-Content $ConfigPath | ConvertFrom-Json
$env:GOOGLE_API_KEY = $config.GOOGLE_API_KEY

# 4. Check/Install Gemini CLI
if (-not (Get-Command gemini -ErrorAction SilentlyContinue)) {
    Write-Host "[*] Gemini CLI not found globally. Installing locally..." -ForegroundColor Cyan
    npm install -g @google/gemini-cli@latest
}

# 5. Launch
Write-Host "[+] Starting Gemini CLI..." -ForegroundColor Green
gemini --skip-trust
