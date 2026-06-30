# Free code signing for Session Perf Tracker

This project can sign Windows release artifacts with a local self-signed PFX.

Important: a self-signed certificate does not remove Microsoft SmartScreen warnings for other users. It only gives the files an Authenticode signature and a stable publisher identity that users can inspect. Public trust requires a paid OV/EV code signing certificate.

## One-time setup

Run:

```powershell
.\scripts\create-self-signed-codecert.ps1
```

Create `release.local.ps1` in the repo root:

```powershell
$SigningPfxPath = "$env:USERPROFILE\Documents\SessionPerfTracker-Signing\SessionPerfTracker-SelfSigned-CodeSigning.pfx"
$SigningPfxPassword = "your-pfx-password"
```

`release.local.ps1` and `*.pfx` are ignored by git.

## Build signed release

Use the existing one-click release flow:

```powershell
.\scripts\release-one-click.ps1
```

Or call the package script directly:

```powershell
.\scripts\package-windows.ps1 -BuildInstaller -SigningPfxPath "C:\path\cert.pfx" -SigningPfxPassword "password"
```

The build signs:

- `SessionPerfTracker.App.exe`
- `SessionPerfTracker-<version>-win-x64-setup.exe`

It also writes:

- `artifacts\release\dist\SHA256SUMS.txt`
- `artifacts\release\update\version.json`

## Suggested release note

This build is signed with a free self-signed developer certificate. Windows may still show a SmartScreen warning because the certificate is not issued by a commercial certificate authority. To verify integrity, compare the installer SHA256 with `SHA256SUMS.txt` from this release.
