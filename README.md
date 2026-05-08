# Session Perf Tracker

Session Perf Tracker is a Windows desktop performance watcher for people who want to see what is running, understand what it is, and quickly deal with noisy or suspicious processes.

It combines several practical workflows in one compact app:

- **Global Watch** shows a lightweight, profile-aware overview of running apps, grouped by application, with CPU, RAM, disk activity, health states, process identity, suspicious marks, launch history, and quick process actions.
- **Process Inspector** helps answer the important questions: where is this file, who published it, is it signed, what parent launched it, and what related processes belong to it?
- **Process Control** lets you close a selected process, a process tree, or an application group when normal closing is too slow or does not work.
- **Session Capture** records one selected app in detail, saves the run, and highlights spikes, threshold breaches, and stability signals for later review.

It is useful when a game stutters, a browser starts eating memory, Discord or Telegram suddenly spikes, an unknown helper process appears, or an app refuses to close normally.

Session Perf Tracker is not an antivirus and not a full system tracing suite. It is a low-overhead visibility and control tool for finding likely culprits, inspecting process identity, closing unwanted process trees, and making performance problems easier to explain.

## Who It Is For

- Gamers who want to catch what was running during lag, stutter, or slow launches
- Power users who want a clearer process overview and faster actions than Task Manager
- QA testers and support people who need saved evidence from app sessions and process behavior
- Streamers, modders, and heavy multitaskers who need to spot and stop noisy background apps
- Anyone who wants to find where a suspicious executable lives before deciding what to do with it

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
- Inspect process identity: full path, publisher/company, file description, signer status, parent process, related app, and process tree context
- Open file location or copy the executable path when you need to investigate manually
- Keep a watch journal of repeated Near / Over / Critical states
- Get profile recommendations when an app repeatedly exceeds its current limits

### Process Control And Watchlist

- Close a selected process directly when it will not close normally
- Close a process tree when helper processes keep relaunching or hanging around
- Close an application group when a multi-process app needs to be shut down together
- Mark an executable as suspicious using its normalized full path, not just its display name
- Track suspicious launch history when the same executable appears again
- Create temporary process bans so selected executables can be blocked while the utility is running
- Remove suspicious marks or process bans when they are no longer needed
- Use details, file location, publisher info, and signer status together to decide whether a process is expected, noisy, or worth blocking

### Session Capture

- Monitor a selected running process or executable
- Include child processes for browsers, launchers, games, chat apps, and other multi-process software
- Track CPU, RAM, Disk Read, and Disk Write during the session
- Record sessions automatically and save them to local history
- Detect threshold breaches and spikes with anti-noise filtering
- Capture lightweight system context around important events
- Review one saved session in detail with metrics, events, context, and stability information
- Compare saved sessions when you need a basic before/after view

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
