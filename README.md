# Session Perf Tracker

Session Perf Tracker is a Windows desktop utility for recording, reviewing, and comparing application performance sessions.

It is built for a simple workflow:

1. Choose an application or running process.
2. Record CPU, RAM, and disk activity during the session.
3. Review spikes, threshold events, and stability signals.
4. Compare saved sessions later.

This is not a replacement for Task Manager. It is a compact session-based tool for understanding what happened during a specific app run.

## Download

Download the latest Windows installer from:

[Latest Release](https://github.com/Grollex/session-perf-tracker/releases/latest)

Run the installer:

```text
SessionPerfTracker-<version>-win-x64-setup.exe
```

The app is self-contained. You do not need to install Python, the .NET SDK, Visual Studio, or any developer tools.

## Features

- Monitor a selected app or running process
- Include child processes for multi-process apps
- Track CPU, RAM, Disk Read, and Disk Write
- Save sessions automatically
- Review session details and event history
- Compare two saved sessions
- Use threshold profiles for different app types
- View Global Watch for a lightweight system overview
- Mark suspicious processes and review launch history
- Export reports to HTML / CSV
- Check app overhead with self-monitoring
- Receive in-app update notifications

## Updates

Session Perf Tracker can check for updates from inside the app.

When a new version is available, the app can:

- update now;
- remind you later;
- skip that version.

User data is kept during updates.

## User Data

Sessions, settings, profiles, exports, and local history are stored here:

```text
%LocalAppData%\SessionPerfTracker
```

Uninstalling the app does not intentionally delete saved session history.

## Requirements

- Windows 10 / Windows 11
- x64 system

No developer runtime is required for normal use.
