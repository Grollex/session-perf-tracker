# Windows Release Packaging

Session Perf Tracker is distributed as a Windows x64 self-contained desktop app. End users do not need Python, the .NET SDK, or a manually installed .NET runtime.

## Default Update URL

The app defaults to the GitHub Releases manifest:

```text
https://github.com/Grollex/session-perf-tracker/releases/latest/download/version.json
```

New installs show this URL in Settings -> Storage -> Updates. Older settings files with an empty manifest URL are normalized to this default. A manually changed non-empty URL is preserved.

## User Data

The app stores per-user data under:

```text
%LocalAppData%\SessionPerfTracker
```

This includes SQLite storage, settings, profiles, history, exports, and update downloads. The installer does not place user data in Program Files, and uninstall does not delete existing user data automatically.

## One-Button Local Release

For the current packaging machine, use:

```text
Release Session Perf Tracker.bat
```

Or call the PowerShell helper directly:

```powershell
.\scripts\release-one-click.ps1 -Version 0.1.2 -ReleaseNotes "Fixes and UI polish"
```

The helper:

- builds a Windows x64 self-contained publish;
- compiles the Inno Setup installer;
- generates `version.json` with the GitHub Release installer URL;
- verifies required app and SQLite runtime files through `package-windows.ps1`;
- opens the installer output folder;
- opens the GitHub new release page for the version tag.

Upload these two generated files to the GitHub Release assets:

```text
artifacts\release\installer\SessionPerfTracker-0.1.2-win-x64-setup.exe
artifacts\release\update\version.json
```

Use release tag:

```text
v0.1.2
```

Because the current Codex GitHub connector does not expose release-asset upload, the drag-and-drop upload step remains manual in the local flow.

## Manual Release Steps

1. Run `Release Session Perf Tracker.bat`.
2. Enter the version, for example `0.1.2`.
3. Wait for the installer and manifest to be generated.
4. On the GitHub Release page, use tag `v0.1.2`.
5. Upload:
   - `SessionPerfTracker-0.1.2-win-x64-setup.exe`
   - `version.json`
6. Publish the release.
7. In an older installed app, click Settings -> Storage -> Updates -> Check for updates.

## GitHub Actions Release

The repository includes `.github/workflows/release.yml`.

After the full source project is in GitHub, either:

- push a tag:

```powershell
git tag v0.1.2
git push origin v0.1.2
```

- or run the workflow manually from GitHub Actions with version `0.1.2`.

The workflow:

- runs on `windows-latest`;
- installs .NET 10;
- installs Inno Setup with Chocolatey;
- runs `scripts/package-windows.ps1 -BuildInstaller`;
- generates SHA256-backed `version.json`;
- publishes the installer and manifest as GitHub Release assets.

The generated manifest points to:

```text
https://github.com/Grollex/session-perf-tracker/releases/download/v<version>/SessionPerfTracker-<version>-win-x64-setup.exe
```

## Build A Release Publish Folder

From the repository root:

```powershell
.\scripts\package-windows.ps1
```

Output:

```text
artifacts\release\win-x64\publish
```

## Build The Installer

Install Inno Setup 6 on the packaging machine, then run:

```powershell
.\scripts\package-windows.ps1 -Version 0.2.0 -BuildInstaller
```

Output:

```text
artifacts\release\installer\SessionPerfTracker-0.2.0-win-x64-setup.exe
artifacts\release\update\version.json
```

For hosted GitHub Releases, prefer:

```powershell
.\scripts\package-windows.ps1 `
  -Version 0.2.0 `
  -BuildInstaller `
  -InstallerBaseUrl "https://github.com/Grollex/session-perf-tracker/releases/download/v0.2.0" `
  -ReleaseNotes "Session Perf Tracker 0.2.0"
```

## In-App Update Path

The app uses a lightweight, version-aware update flow:

1. Settings shows the installed app version.
2. The app checks the configured `version.json`.
3. If a newer version exists, the app downloads the full installer.
4. The downloaded installer is verified against the manifest SHA256.
5. Inno Setup upgrades the existing installation because `AppId` stays constant.

There is no background updater service, forced update, or delta patching.

## Release Readiness Checklist

- Start the app from the publish folder.
- Confirm the app icon is visible.
- Confirm `%LocalAppData%\SessionPerfTracker\sessionperftracker.db` is created or reused.
- Confirm Settings -> Storage -> Updates contains the default GitHub manifest URL.
- Install with the generated setup exe.
- Confirm Start Menu shortcut works.
- Confirm manual update check reaches the GitHub Release manifest.
- Confirm uninstall removes Program Files files but keeps user data.
