# Bugs And Risks - Session Perf Tracker

## Known Risks

- Git worktree had many existing modifications before memory setup. Avoid overwriting user work.
- No automated test project was found in the first pass.
- Localization changes must stay synchronized between Russian and English resources.
- Process monitoring can become expensive if polling or event handling is too aggressive.
- UI can freeze if process inspection, storage, update checks, or export work runs on the UI thread.

## Open Bugs

- No confirmed runtime bugs recorded in this memory pass.
