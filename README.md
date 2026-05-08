# Session Perf Tracker

Session Perf Tracker is a Windows desktop performance watcher for people who want to know what is actually slowing their system down.

It combines two workflows in one compact app:

- **Global Watch** shows a lightweight, profile-aware overview of running apps, grouped by application, with CPU, RAM, disk activity, health states, recommendations, suspicious marks, and launch history.
- **Session Capture** records one selected app in detail, saves the run, highlights spikes and threshold breaches, and lets you review or compare sessions later.

It is useful when a game stutters, a browser starts eating memory, Discord or Telegram suddenly spikes, an unknown helper process appears, or you simply want proof of what changed between two runs.

Session Perf Tracker is not an antivirus and not a full system tracing suite. It is a low-overhead visibility tool for finding likely culprits, reviewing app behavior, and making performance problems easier to explain.

## Who It Is For

- Gamers who want to catch what was running during lag, stutter, or slow launches
- Power users who want a clearer process overview than Task Manager provides
- QA testers and support people who need saved evidence from app sessions
- Streamers, modders, and heavy multitaskers who need to spot noisy background apps
- Anyone who wants to compare “before vs after” performance after changing settings, drivers, mods, or app versions

## Download

Download the latest Windows installer from:

[Latest Release](https://github.com/Grollex/session-perf-tracker/releases/latest)

Run the installer:

```text
SessionPerfTracker-<version>-win-x64-setup.exe
```

The app is self-contained. You do not need to install Python, the .NET SDK, Visual Studio, or any developer tools.

## Features

### Global Watch

- See running apps grouped by application instead of drowning in subprocesses
- Sort and filter by CPU, RAM, disk activity, health state, profile, and assignment state
- Compare processes against profile-aware limits instead of one global threshold
- View top offenders by CPU, RAM, and disk with clear OK / Near / Over / Critical states
- Inspect process identity: path, publisher/company, signer status, parent process, related app, and process tree context
- Mark apps or executables as suspicious and track future launches
- Keep a watch journal of repeated Near / Over / Critical states
- Get profile recommendations when an app repeatedly exceeds its current limits

### Session Capture

- Monitor a selected running process or executable
- Include child processes for browsers, launchers, games, chat apps, and other multi-process software
- Track CPU, RAM, Disk Read, and Disk Write during the session
- Record sessions automatically and save them to local history
- Detect threshold breaches and spikes with anti-noise filtering
- Capture lightweight system context around important events
- Review one saved session in detail with metrics, events, context, and stability information
- Compare two saved sessions to see what got better or worse

### Practical Tools

- Threshold profiles for light apps, browsers/chats, games, hardcore apps, and custom use cases
- App-to-profile assignments by executable name
- HTML and CSV reports
- Self-monitoring block to show the overhead of the utility itself
- In-app update notifications with update now, later, or skip version choices

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
