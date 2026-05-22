# Architecture - Session Perf Tracker

## Layers

- `SessionPerfTracker.Domain`: models, metric definitions, services, abstractions, comparison/summarization logic.
- `SessionPerfTracker.Infrastructure`: collectors, Windows process control, WMI/WinAPI adapters, storage, settings stores, update service, export services.
- `SessionPerfTracker.App`: WPF shell, windows, XAML resources, localization, MVVM viewmodels.

## Dependency Direction

- Domain must remain independent.
- Infrastructure can depend on Domain.
- App can depend on Domain and Infrastructure.
- UI should not reach directly into low-level Windows APIs when an Infrastructure service exists or should exist.

## Important UI Constraints

- Keep long-running work off the UI thread.
- Use MVVM patterns for state and commands.
- Keep localization consistent across `Strings.ru-RU.xaml` and `Strings.en-US.xaml`.

## Packaging

- Packaging scripts live in `scripts`.
- Inno Setup assets live under `installer\inno`.
- Build artifacts belong in output/artifact folders, not in source folders.
