# Architecture - Session Perf Tracker

## Layers

- `SessionPerfTracker.Domain`: models, metric definitions, services, abstractions, comparison/summarization logic, tracked-executable matching.
- `SessionPerfTracker.Infrastructure`: collectors, native Windows adapters, GPU monitoring, process identity, storage, settings stores, update service, export services.
- `SessionPerfTracker.App`: WPF shell, windows, XAML resources, localization, MVVM view models, coordination services.
- `SessionPerfTracker.UnitTests`: xUnit tests, including end-to-end recordings of real short-lived processes.

## Dependency Direction

- Domain must remain independent.
- Infrastructure can depend on Domain.
- App can depend on Domain and Infrastructure.
- UI should not reach directly into low-level Windows APIs when an Infrastructure service exists or should exist.

## Monitoring Pipeline

- `Native/SystemProcessSnapshot`: one `NtQuerySystemInformation(SystemProcessInformation)` call per tick gives every process's CPU times, memory, I/O counters, parent PID and create time. CPU% and MB/s are deltas between snapshots keyed by `(pid, createTime)`, so PID reuse cannot mix processes.
- `Native/ProcessImagePath`: full paths via `QueryFullProcessImageName`, falling back to `SystemProcessIdInformation` + device-path mapping for protected and service processes.
- `Native/ProcessTreeTracker`: keeps the recorded process tree; children stay tracked after the root exits (launchers), and the session ends when the whole tree is gone.
- `Gpu/GpuMonitor`: PDH wildcard counters (`GPU Engine`, `GPU Process Memory`, `GPU Adapter Memory`) read in one batch, English counter names so localized Windows works. Per-process GPU% is the busiest engine type (Task Manager semantics). `D3dkmtAdapterInfo` adds adapter names, VRAM totals and temperature when the driver reports it. Values larger than the adapter's VRAM are dropped as driver noise.
- `Collectors/ProcessCpuMemoryCollector` builds session samples from the shared snapshot and GPU sample; `Runner/ProcessSessionRunner` drives sampling, spike/limit detection, hang detection (`IsHungAppWindow`), exit classification (NTSTATUS crash codes), and always finalizes the session.
- `GlobalWatch/LightweightGlobalProcessScanner` reuses the same snapshot for the Global Watch table; `GlobalProcessPathCache` remembers paths per `(pid, createTime)`.
- `SelfMonitoring/SystemLoadProvider` supplies system CPU/RAM/GPU/VRAM totals for the status strips.

## Storage

- SQLite (`%LocalAppData%\SessionPerfTracker\sessionperftracker.db`, or `--data-dir`).
- Sessions are appended incrementally (samples/events in batches); the session list reads headers only.
- Sessions left open by a crash or power loss are recovered on startup as interrupted sessions.
- Settings are stored per key and written only when a key changes; background writers go through `IThresholdSettingsStore.UpdateAsync` so they cannot overwrite UI edits.

## Tracked Executables

- Suspicious watchlist entries and process bans store the path up front plus an `ExecutableFingerprint` (SHA-256, size, signer status/subject, product, company, file description, version).
- `Identity/AuthenticodeInspector` checks embedded and catalog signatures (`WinVerifyTrust` + `CryptCATAdmin`).
- `Identity/ExecutableLocator` searches likely locations (original folder tree, Program Files, registry uninstall entries, App Paths) when a tracked file disappears.
- `Domain/Services/TrackedExecutableMatcher` scores candidates: identical hash = 100, metadata match up to 95, relocation accepted at 75+. Path changes are recorded in the entry history.
- `App/Services/TrackedExecutableCoordinator` verifies presence periodically and applies path updates found by Global Watch scans.

## UI Threading

- Sampling, scanning, storage and identity work run off the UI thread (`Task.Run`, `ConfigureAwait(false)`).
- The UI renders only while the window is visible (`SetUiVisible`); collections are replaced with a single `Reset` notification.
- Localized strings are cached (`UiText`) and must exist in both `Strings.en-US.xaml` and `Strings.ru-RU.xaml` (enforced by tests).

## QA Switches

- `--data-dir <folder>`: run on an isolated data folder (own single-instance slot).
- `--visual-qa-dir <folder>`: render every tab to PNG and exit.
- `--visual-qa-record`: with `--visual-qa-dir`, record a scripted load generator first (live and finished Live screenshots).

## Packaging

- Packaging scripts live in `scripts`.
- Inno Setup assets live under `installer\inno`.
- Build artifacts belong in output/artifact folders, not in source folders.
