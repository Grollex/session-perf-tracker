# Session Perf Tracker

A free Windows tool that records what an app or game was doing while it ran: CPU, RAM, GPU, video memory, disk, startup time, hangs and crash-like exits. Start a session, use the program as usual, stop it, and check the timeline afterwards.

> Early version. If something breaks or looks confusing, use **Report bug** or **Settings → Feedback** in the app. Rough reports help too.

## What it does

- **Records a session** for a running process, an assigned app or an executable. Child processes are followed, also after the launcher itself exits (game launchers, installers, browsers).
- **Flags problems**: crash-like exits, hangs (windows that stop responding), slow startups, spikes and limit breaches. They are saved with the session.
- **GPU per process**: the busiest engine like in Task Manager, dedicated and shared video memory, adapter totals, and GPU temperature when the driver reports it. Works on NVIDIA, AMD and Intel through Windows counters, no vendor tools needed.
- **Compares two runs** side by side, for example before and after changing graphics settings, mods, drivers or app versions.
- **Global Watch**: a light scan of all running processes with CPU, GPU, RAM and disk, publisher, start time, top consumers and recently started processes.
- **Watched and banned executables** are stored by path plus a fingerprint (SHA-256, signer, product, version, size), so the app finds them again after they move or update. Bans stop a chosen executable from launching again while the tracker is running.
- **Profiles** with different limits for games, browsers, background apps and heavy workloads, and a suggestion when the same limit warning keeps coming back.
- **Reports**: export a session to HTML (with timeline charts) or CSV; send or save a bug report with app and version info and an optional screenshot.

The interface is in English and Russian.

## Overhead

It takes one process snapshot per tick, writes sessions incrementally and doesn't draw the UI while minimized. On my 16-thread desktop (v1.7.0) that is about 0.3% CPU while recording every second, under 0.2% idle, and around 150–160 MB of RAM.

## Install

1. Download the latest Windows x64 installer from [Releases](https://github.com/Grollex/session-perf-tracker/releases).
2. Run it and pick the language on first launch.

Install updates only from GitHub Releases: the app checks downloaded installers against SHA-256 before running them. Windows SmartScreen may warn about the installer because it isn't signed with a paid certificate.

## Limits

- GPU temperature appears only if the graphics driver exposes it to Windows. CPU temperature is not collected (that would need a kernel driver).
- Some protected system processes don't allow reading every detail; the app shows what Windows permits.

## Development

C# / .NET 10 / WPF, x64. The solution is split into App (UI), Domain and Infrastructure projects, with xUnit tests.

```
dotnet test SessionPerfTracker.slnx -c Release
```

- [docs/architecture.md](docs/architecture.md): how the layers fit together
- [docs/release-packaging.md](docs/release-packaging.md): building the installer and publishing releases
- [docs/commands.md](docs/commands.md): useful commands
- UI strings: `src/SessionPerfTracker.App/Localization` (en-US, ru-RU)
