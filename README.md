# Session Perf Tracker

> Early version: Session Perf Tracker is still young. If it crashes, misses something, looks confusing, or gets in your way, please send it through **Report bug** or **Settings -> Feedback** in the app. Rough reports are useful too.

Session Perf Tracker is a small, free Windows tool for figuring out what happened while an app or game was running.

Start a session for the program you care about, play or work normally, and stop the session when you are done. The app keeps a timeline of CPU, memory, GPU, video memory, disk activity, startup time, exits, and suspicious spikes so you can look back without guessing.

## What It Helps With

- See whether a game, editor, browser, launcher, or helper process was eating CPU, RAM, GPU, video memory, or disk.
- Follow a launcher's child processes even after the launcher itself exits (game launchers, installers, browsers).
- Compare two runs after changing graphics settings, mods, drivers, or app versions.
- Keep an eye on background processes with Global Watch.
- Keep suspicious, banned, and watched executables tracked even when they move, update, or reinstall.
- Save useful bug reports with app/version info, optional screenshots, and the latest session summary.
- Export session reports to HTML (with timeline charts) or CSV.

## Main Features

- **Live dashboard**: pick a running process, an assigned app or an executable and record it; child processes are followed by default and stay tracked after the root exits. The dashboard shows KPI tiles, a performance timeline with event markers, recent events, the system's top processes, memory details and a session summary. When nothing is recorded it shows the whole system.
- **Detection**: crash-like exits, hangs (not-responding windows), long startups, spikes and limit breaches are detected and saved with the session.
- **GPU monitoring**: per-process GPU load (busiest engine, like Task Manager), dedicated/shared video memory, adapter totals, and GPU temperature when the driver reports it. Works with NVIDIA, AMD, and Intel through Windows' own counters; no vendor tools or drivers needed.
- **Global Watch**: a lightweight scan of every running process with load-tinted CPU, GPU, RAM and disk cells, start time, publisher and profile health, a process inspector with actions, top consumers, recently started processes and watch alerts.
- **Tracked executables**: the suspicious watchlist and process bans store the executable path up front, plus a fingerprint (SHA-256, signer, product, version, size). The app verifies whether each entry is present, finds it again after it moves or updates, and keeps a history of path changes.
- **Profiles**: different limits for games, browsers, light background apps, hardcore workloads, and custom targets (including GPU limits).
- **Recommendations**: turn repeated profile-limit warnings into suggested profile assignments.
- **Session comparison**: compare two saved sessions side by side, including GPU metrics.
- **Feedback reports**: send or save problem reports and feature ideas from inside the app.
- **Process bans**: block repeat launches of selected executables while the app is running. Use with care.

## Lightweight By Design

The app is meant to stay out of the way while it watches other programs:

- One kernel process snapshot per tick instead of per-process queries; GPU counters are read in one batch.
- Sessions are written incrementally, so long recordings do not rewrite the database.
- The UI only renders while it is visible; when minimized or in the tray, background work continues without drawing.
- Measured on a 16-thread desktop with the 1.7.0 dashboard: about 0.16% of total CPU with the Live tab open and idle, about 0.03% minimized, and about 0.3% while recording at 1-second sampling (v1.5.5 used about 0.5% while idle). Memory stays around 150-160 MB.

## Install

1. Download the latest Windows x64 installer from [Releases](https://github.com/Grollex/session-perf-tracker/releases).
2. Run the installer.
3. Launch Session Perf Tracker.
4. Pick your language on first launch (English or Russian).

## Notes And Limits

- This is an early build, so some edges are still being tested in real use.
- Install updates only from GitHub Releases. Downloaded installers are checked against SHA-256 before launch.
- Windows SmartScreen may warn about the installer because the project uses a self-signed/free signing path.
- GPU temperature appears only when the graphics driver exposes it to Windows. CPU temperature is not collected (it would require a kernel driver).
- Some protected system processes do not allow reading every detail; the app shows what Windows permits.

## Development

The app is built with WPF and .NET 10 using a clean separation between the UI, domain logic, and infrastructure layers.

- Architecture overview: [docs/architecture.md](./docs/architecture.md).
- Release packaging notes: [docs/release-packaging.md](./docs/release-packaging.md).
- Useful commands: [docs/commands.md](./docs/commands.md).
- UI localization is stored in XAML resource dictionaries under `src/SessionPerfTracker.App/Localization`.
- Tests: `dotnet test SessionPerfTracker.slnx -c Release`.
