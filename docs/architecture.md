# Session Perf Tracker Architecture

## Product Shape

This is a session-based Windows performance tracker and comparison utility. It is intentionally not a Task Manager. The main workflow is:

1. Select or launch a target app.
2. Track that target from start to exit.
3. Collect lightweight metric samples.
4. Detect notable events.
5. Save the session.
6. Compare two saved sessions.

The MVP focuses on compact summaries and honest comparisons. It does not include media, FPS, quality analysis, or continuous full-system monitoring.

## Current Stack

The foundation is now Windows-native:

- .NET 10
- WPF
- Plain MVVM-style view models without a heavy framework
- JSON-backed `ISessionStore` as a temporary storage implementation

SQLite remains the preferred internal storage target once real recording is added. CSV and HTML should be export formats, not the primary user-facing storage.

## Projects

- `SessionPerfTracker.Domain`
  - session, metric, event, summary, comparison models;
  - contracts for targeting, collectors, event detection, storage, comparison, export, self-monitoring;
  - pure summary and comparison services.
- `SessionPerfTracker.Infrastructure`
  - JSON session store;
  - mock session seed data;
  - placeholder collectors, event pipeline, targeting, hang detector, self-monitoring.
- `SessionPerfTracker.App`
  - WPF shell;
  - Live, Sessions, Compare, Settings screens;
  - compact summary-first UI scaffold.

## Boundaries

- Process targeting: target selection and lifecycle.
- Metric collection: CPU, RAM, GPU, Disk I/O, optional temperature.
- Event detection: thresholds, spikes, long startup, crash-like exit, hang provider.
- Session storage: saved records, samples, events, summaries.
- Session summaries: min, avg, max, counts, duration.
- Comparison engine: two saved sessions only.
- Export layer: CSV/HTML later.
- UI: compact WPF screens over domain/view-model data.

## MVP Reliability

- CPU and RAM are reliable MVP candidates via process snapshots.
- Disk I/O is realistic but should use a Windows-specific adapter with fallback.
- GPU is best-effort because Windows GPU engine counters and ETW vary by system and driver.
- Temperature is optional and provider-based. It should not block the MVP.
- Hang detection is intentionally abstracted behind `IHangDetector`. A naive process-status check is not enough.

## Low-Overhead Policy

- Default sampling interval: 1000 ms.
- Optional faster sampling: 500 ms.
- No deep full-system tracing during normal sessions.
- Spike context snapshots are event-triggered or future-mode only.
- Live UI refresh is separate from sample collection and should be slower by default.
- Storage writes should be batched once real recording is implemented.

## Overhead Budget

Initial MVP target:

- average CPU under 2% during normal sampling;
- memory under 150 MB for the utility shell;
- no constant full-system scan;
- disk writes batched by sample count or time window.

Self-monitoring has a dedicated `ISelfMonitoringProvider` extension point.

## Foundation Now

- WPF app shell.
- Shared data models.
- Shared comparison engine.
- Shared summary generation.
- JSON session store seeded with mock sessions.
- UI shell for Live, Sessions, Compare, Settings.
- Running-process attach as the primary target flow.
- Executable launch as a secondary target flow.
- CPU/RAM root-process and child-process aggregation.
- RAM aggregation uses Private Working Set via Windows process memory counters where available, with Private Bytes fallback.
- One-shot RAM accounting diagnostic snapshot lists root PID, parent PID, included descendants, per-PID RAM, and aggregate.
- CPU/RAM threshold and spike detectors over aggregated samples.
- Session runner loop that records until target exit or manual stop.
- Buffered JSON saves during long sessions.
- Placeholder event pipeline, hang detector, self-monitoring.

## Future Modules

- Disk I/O collector.
- GPU collector with fallback chain.
- Optional temperature provider.
- Spike context snapshot provider.
- Reliable hang detector provider.
- SQLite-backed store.
- CSV and HTML exports.
- Self-benchmark mode.
