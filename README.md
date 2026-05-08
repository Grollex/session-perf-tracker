# Session Perf Tracker

Windows desktop utility for recording, reviewing, and comparing application performance sessions.

Session Perf Tracker is not another Task Manager. It is a compact session-based performance tracker focused on answering a practical question: what happened to an application during a specific run, where did resource spikes appear, and how does this session compare to another one?

## Current Features

- Target application monitoring by running process or executable
- Session-based recording from start to process exit
- CPU, RAM, Disk Read, and Disk Write tracking
- Child process aggregation for multi-process apps
- Threshold profiles for different app types
- Event detection for spikes and threshold breaches
- Global Watch overview for finding suspicious or heavy applications
- Watch Journal and profile recommendations
- Suspicious process watchlist and launch history
- SQLite-backed session history
- Session Details / Review mode
- Compare two saved sessions
- HTML / CSV export
- Self-monitoring / overhead diagnostics
- Windows installer and in-app update foundation

## Installation

Download the latest installer from GitHub Releases:

```text
https://github.com/Grollex/session-perf-tracker/releases/latest
```

Run:

```text
SessionPerfTracker-<version>-win-x64-setup.exe
```

The app is published as a Windows x64 self-contained build. Users do not need to install Python, the .NET SDK, or a separate .NET runtime.

## Updates

The app ships with this update manifest URL by default:

```text
https://github.com/Grollex/session-perf-tracker/releases/latest/download/version.json
```

To update from inside the app:

1. Open Settings.
2. Go to Storage / Updates.
3. Click `Check for updates`.
4. If a newer version is available, click `Download and run installer`.

The app downloads the latest installer from GitHub Releases, verifies its SHA256 hash from `version.json`, and launches the installer over the existing installation.

## User Data

Session data, settings, profiles, exports, update downloads, and local history are stored per user:

```text
%LocalAppData%\SessionPerfTracker
```

Uninstalling the app does not intentionally delete user history.

## Building Locally

```powershell
dotnet restore SessionPerfTracker.slnx
dotnet build SessionPerfTracker.slnx
dotnet run --project src\SessionPerfTracker.App\SessionPerfTracker.App.csproj
```

## Making A Release

On the packaging machine, use the one-button helper:

```text
Release Session Perf Tracker.bat
```

The helper reads the last released version from `VERSION`, suggests the next patch version, and accepts Enter as "use the suggested version".

Or run PowerShell directly:

```powershell
.\scripts\release-one-click.ps1 -Version 0.1.2 -ReleaseNotes "Fixes and UI polish"
```

The script builds the installer, generates `version.json`, creates or updates the GitHub Release, and uploads both release assets automatically through GitHub CLI:

```text
artifacts\release\installer\SessionPerfTracker-<version>-win-x64-setup.exe
artifacts\release\update\version.json
```

On first use, GitHub CLI may open a browser login. Sign in with an account that can publish releases to `Grollex/session-perf-tracker`.

For a local build without uploading assets, run:

```powershell
.\scripts\release-one-click.ps1 -Version 0.1.2 -SkipUpload
```

When the project is fully hosted in this repository, pushing a tag like `v0.1.2` can also run the GitHub Actions release workflow.
