# Decisions - Session Perf Tracker

## 2026-05-10 - Markdown Memory System

- Use Markdown files as agent memory instead of model fine-tuning.
- Keep memory files in the project root and global memory in `C:\Users\ThatSameHrian\Documents\_agent_memory`.
- Do not auto-update memory with guesses; only record inspected or verified facts.

## 2026-10-03 - v1.6.0 Monitoring Core

- Read process data from one `NtQuerySystemInformation` snapshot per tick instead of per-process `System.Diagnostics.Process` queries: one syscall for every process, no handles, far lower CPU.
- GPU data uses Windows' own PDH GPU counters (vendor-neutral, free) with English counter paths, plus D3DKMT for adapter names, VRAM totals and temperature. No vendor SDKs, no kernel drivers, no LibreHardwareMonitor.
- Per-process GPU% follows Task Manager: the busiest engine type, not a sum of engines.
- CPU temperature is out of scope: it requires a kernel driver, which conflicts with the lightweight and trust goals.
- Tracked executables (suspicious/bans) store the path plus a fingerprint; relocation requires a score of at least 75, metadata alone can never reach the 100 reserved for an identical hash.
- Labels persisted with sessions (threshold source, notes) stay English so filters keep working; they are translated only for display where practical.
- Sessions are stored incrementally; interrupted sessions are recovered rather than discarded.
- QA runs use `--data-dir` so they never touch the user's real database.

## Existing Architectural Decisions

- Use Clean Architecture with Domain, Infrastructure, and WPF App layers.
- Use MVVM for UI behavior.
- Keep Domain free from Windows-specific implementation details.
- Keep localization resources in App localization XAML files.
