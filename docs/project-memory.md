# Project Memory - Session Perf Tracker

## Durable Facts

- Windows desktop app built with C# / .NET 10 / WPF, x64 only.
- Solution file: `SessionPerfTracker.slnx`.
- Main app project: `src\SessionPerfTracker.App\SessionPerfTracker.App.csproj` (version comes from the root `VERSION` file).
- Domain project: `src\SessionPerfTracker.Domain\SessionPerfTracker.Domain.csproj`.
- Infrastructure project: `src\SessionPerfTracker.Infrastructure\SessionPerfTracker.Infrastructure.csproj`.
- Test project: `src\SessionPerfTracker.UnitTests` (xUnit; includes integration tests that start real `powershell`/`ping` processes, so tests must run on Windows).
- Architecture is layered: App -> Domain + Infrastructure, Infrastructure -> Domain, Domain -> no app/infrastructure dependencies.
- Localization files: `src\SessionPerfTracker.App\Localization\Strings.en-US.xaml` and `Strings.ru-RU.xaml` (UTF-8 with BOM). Tests enforce matching keys, no hardcoded visible XAML text, no Cyrillic literals in C#, and that every referenced key exists.
- User data: `%LocalAppData%\SessionPerfTracker\sessionperftracker.db` (SQLite) plus `exports`, `updates`, feedback folders. `--data-dir` overrides it.
- Releases: GitHub `Grollex/session-perf-tracker`, assets = installer, `version.json`, `SHA256SUMS.txt`. Signing secrets live in the untracked `release.local.ps1` (never print or commit it).

## Monitoring Facts (v1.6.0)

- Process data comes from one NT process snapshot per tick (x64 structure offsets); GPU from PDH English counters plus D3DKMT adapter queries.
- Per-process GPU% = busiest engine type; dedicated/shared memory per process; adapter VRAM totals; GPU temperature only where the driver reports it. CPU temperature is intentionally not collected (needs a kernel driver).
- Measured overhead (Release build, 16 logical CPUs, Live tab open): about 2.9% of one core (0.18% of the machine); minimized about 1.0% of one core (0.07%). v1.5.5 measured about 8% of one core.
- Path coverage for running processes went from about 60% to over 99% after the kernel image-name fallback.

## QA Notes

- Computer-use control of the app window was declined by the user (2026-10-03); use `--visual-qa-dir` / `--visual-qa-record` with `--data-dir` for screenshots instead.
- Never point QA runs at the real data folder; use `--data-dir` with a scratch folder.

## Agent Memory Policy

- Store durable project facts here after inspection.
- Store decisions in `decisions.md`.
- Store known bugs in `bugs.md`.
- Store executable commands in `commands.md`.
- Do not store temporary chat text or guesses.
