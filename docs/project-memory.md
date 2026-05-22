# Project Memory - Session Perf Tracker

## Durable Facts

- Windows desktop app built with C# / .NET / WPF.
- Solution file: `SessionPerfTracker.slnx`.
- Main app project: `src\SessionPerfTracker.App\SessionPerfTracker.App.csproj`.
- Domain project: `src\SessionPerfTracker.Domain\SessionPerfTracker.Domain.csproj`.
- Infrastructure project: `src\SessionPerfTracker.Infrastructure\SessionPerfTracker.Infrastructure.csproj`.
- Architecture is layered: App -> Domain + Infrastructure, Infrastructure -> Domain, Domain -> no app/infrastructure dependencies.
- Localization files exist under `src\SessionPerfTracker.App\Localization`.

## Current State Notes

- Git worktree was already dirty before this memory setup.
- Existing changes include App XAML/code-behind/viewmodels, Domain settings model, Infrastructure settings stores, README/VERSION, localization files, scripts, and OpenCode config.
- No test project was found in the first pass.

## Agent Memory Policy

- Store durable project facts here after inspection.
- Store decisions in `DECISIONS.md`.
- Store known bugs in `BUGS.md`.
- Store executable commands in `COMMANDS.md`.
- Do not store temporary chat text or guesses.
