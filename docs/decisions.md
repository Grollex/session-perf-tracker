# Decisions - Session Perf Tracker

## 2026-05-10 - Markdown Memory System

- Use Markdown files as agent memory instead of model fine-tuning.
- Keep memory files in the project root and global memory in `C:\Users\ThatSameHrian\Documents\_agent_memory`.
- Do not auto-update memory with guesses; only record inspected or verified facts.

## Existing Architectural Decisions

- Use Clean Architecture with Domain, Infrastructure, and WPF App layers.
- Use MVVM for UI behavior.
- Keep Domain free from Windows-specific implementation details.
- Keep localization resources in App localization XAML files.
