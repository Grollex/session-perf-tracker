param(
    [string]$Subject = "CN=Grollex Session Perf Tracker",
    [string]$OutputDirectory = "$env:USERPROFILE\Documents\SessionPerfTracker-Signing",
    [string]$FileName = "SessionPerfTracker-SelfSigned-CodeSigning.pfx"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command New-SelfSignedCertificate -ErrorAction SilentlyContinue)) {
    throw "New-SelfSignedCertificate is not available on this system."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$pfxPath = Join-Path $OutputDirectory $FileName

Write-Host "Creating self-signed code signing certificate: $Subject"
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(3)

$password = Read-Host "PFX password" -AsSecureString
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $password | Out-Null

Write-Host ""
Write-Host "PFX created:"
Write-Host "  $pfxPath"
Write-Host ""
Write-Host "Create release.local.ps1 in the repo root with:"
Write-Host ('$SigningPfxPath = "' + $pfxPath + '"')
Write-Host '$SigningPfxPassword = "YOUR_PASSWORD_HERE"'
Write-Host ""
Write-Host "Do not commit release.local.ps1 or the PFX file."
