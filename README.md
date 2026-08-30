# Session Perf Tracker

> Early version: Session Perf Tracker is still young. If it crashes, misses something, looks confusing, or gets in your way, please send it through **Report bug** or **Settings -> Feedback** in the app. Rough reports are useful too.

Session Perf Tracker is a small Windows tool for figuring out what happened while an app or game was running.

Start a session for the program you care about, play or work normally, and stop the session when you are done. The app keeps a timeline of CPU, memory, disk activity, startup time, exits, and suspicious spikes so you can look back without guessing.

## What It Helps With

- See whether a game, editor, browser, launcher, or helper process was eating CPU, RAM, or disk.
- Compare two runs after changing graphics settings, mods, drivers, or app versions.
- Keep an eye on background processes with Global Watch.
- Save useful bug reports with app/version info, optional screenshots, and the latest session summary.
- Export session reports to HTML or CSV when you want to share or archive the data.

## Main Features

- **Live sessions**: record a target process with optional child-process tracking.
- **Global Watch**: scan running processes and highlight unusual load.
- **Profiles**: use different thresholds for games, browsers, light background apps, hardcore workloads, and custom targets.
- **Recommendations**: turn repeated profile-limit warnings into suggested profile assignments.
- **Session comparison**: compare two saved sessions side by side.
- **Feedback reports**: send or save problem reports and feature ideas from inside the app.
- **Process bans**: block repeat launches of selected processes while the app is running. Use with care.

## Install

1. Download the latest Windows x64 installer from [Releases](https://github.com/Grollex/session-perf-tracker/releases).
2. Run the installer.
3. Launch Session Perf Tracker.
4. Pick your language on first launch, or leave the default English UI.

## Notes

- This is an early build, so some edges are still being tested in real use.
- Install updates only from GitHub Releases.
- Windows SmartScreen may warn about the installer because the project uses a self-signed/free signing path.

## Development

The app is built with WPF and .NET using a clean separation between the UI, domain logic, and infrastructure layers.

- Agent/development notes live in [AGENTS.md](./AGENTS.md).
- Release packaging notes live in [docs/release-packaging.md](./docs/release-packaging.md).
- UI localization is stored in XAML resource dictionaries under `src/SessionPerfTracker.App/Localization`.
