# TODO - Session Perf Tracker

## Agent Memory Follow-Ups

- [x] Confirm folder roles: `New project` is the current v1.5.0 main copy; `New project - Development` is a legacy v0.1.4 worktree.
- [x] Add and verify unit tests with `dotnet test SessionPerfTracker.slnx -c Release`.
- [x] Verify packaging outputs: self-contained publish, ZIP, installer, and update manifest.
- [x] Add CI restore/build/test/package smoke with packaged-app startup check and ZIP artifact upload.
- [x] Gate release workflow with restore/build/test and packaged-app startup smoke.
- [x] Add destructive process action guardrails: safety policy, confirmation prompt, kill/ban audit trail, and blocked-action logging.
- [x] Confirm the release workflow with `release-one-click.ps1 -SkipUpload -NoOpen`.

## Product Follow-Ups

- [x] Split the six main screens into focused UserControls with shared resources.
- [x] Split MainWindowViewModel into core, Live Monitoring, Global Watch, Sessions, and Settings partial modules.
- [x] Update the vulnerable SQLite native dependency.
- Preserve current MVP behavior while improving localized UI and process inspection.
- Keep performance monitoring lightweight.
- Keep process-control actions explicit and recoverable.

## Feedback Review Backlog

- [ ] Replace public hardcoded ntfy topic with a relay/tokenized endpoint. Status: Нет денег, пока не работаем.
- [x] Add optional bug-report attachments: latest session summary and/or current screen snapshot.
- [x] Implement real hang detection or hide the live `Hangs` counter until it is backed by real data.
- [x] Decide what to do with the hidden session header: revive it as a compact dashboard or remove the dead XAML.
- [ ] Implement reliable GPU/temperature collectors, or move unavailable metrics out of the main user-facing flow. Status: Future note, skipped for now.
- [x] Add a first-run trust/release explainer for self-signed builds, SHA256 verification, and GitHub Releases.
- [x] Add feedback delivery status/history for recently sent and failed reports.
- [x] Add `Copy report` and `Open latest report` actions for saved feedback files.
- [x] Add privacy copy near feedback submit explaining what is sent remotely and what stays local.
- [x] Add a release smoke-test script for signing, `SHA256SUMS.txt`, `version.json` hash match, and installer existence.

## Visual Polish Backlog

- [x] General UI polish: align spacing, buttons, panels, scrollbars, tables, and status colors.
- [x] Main window: improve top status row, left navigation, `Report bug`, and trust banner.
- [x] Live tab: make Start/Stop primary, turn metrics into clear KPI blocks, keep diagnostics secondary.
- [x] Global Watch tab: make process table central, move filters/actions into a toolbar, separate dangerous actions.
- [x] Sessions tab: make saved sessions denser, strengthen selected state, organize summary and export.
- [x] Details tab: present events as a readable timeline/list with compact expandable context.
- [x] Compare tab: show paired session selectors and a clear metric-difference table.
- [x] Settings tab: split into short sections, standardize save buttons, isolate dangerous storage actions, polish Feedback.
- [x] Visual QA: build, test, verify all tabs, RU/EN text, and minimum window size `1220x720`.
