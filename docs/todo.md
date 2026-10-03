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

## Visual Polish Backlog v2

- [x] Shell/chrome pass: make the trust banner compact, reduce first-screen height loss, and keep page headers/actions aligned.
- [x] Details/Sessions header fixes: stop stretched or clipped action buttons, keep back/details/export actions in stable right-aligned groups.
- [x] Global Watch selected panel: make long paths readable without breaking the panel; improve right-panel vertical density and clipping.
- [x] Sessions metrics density: replace tall metric cards with a tighter repeated row style and clearer summary hierarchy.
- [x] Recommendations readability: reduce card weight, add clearer severity badges, and localize the visible recommendation text or isolate it as technical guidance.
- [x] Compare empty state: replace the blank comparison table with a useful empty state and disabled export affordance.
- [x] Settings consistency: reduce nested-card feel, align section spacing, and remove mixed RU/EN status copy where user-facing.
- [x] Visual QA v2: regenerate all tab screenshots, inspect minimum window size, verify paths and recommendation screens, then update publish.

## Visual Polish Backlog v3

- [x] Global Watch sub-tabs: increase tab height/width breathing room, prevent Russian labels from looking clipped, and align the selected state.
- [x] Live tab microcopy: fix the glued `События0` header and review counter/title spacing in compact panels.
- [x] Details recommendations: remove the `РекомендацииЧто делать дальше` header collision and reduce English-heavy recommendation rows.
- [x] Sessions/Details localization polish: replace visible English status fragments in metric/recommendation demo data where the UI can localize safely.
- [x] Global Watch tables: review header/cell spacing, long process names, and right-side details panel for edge clipping at 1420x860 and smaller.
- [x] Settings/Feedback polish: verify scroll edges, button alignment, and long text wrapping after the v2 cleanup.
- [x] Visual QA v3: rebuild, regenerate all screenshots, inspect every tab, send updated screenshots to Telegram, then refresh publish.

## Serious Improvement Backlog

- [x] Update security: require a valid SHA256 hash for remote installer downloads before auto-update can launch anything.
- [x] Update UX/i18n: localize update statuses/errors and show whether the downloaded installer passed SHA256 verification.
- [x] Data retention: cap/compact Global Watch journal, ban history, recommendation history, feedback delivery history, and old screenshots so app data cannot grow forever.
- [x] Process path reliability: add a PID + start-time path cache and backfill watch/recommendation rows so `Недоступно` appears much less often.
- [x] Dangerous-action audit: add a single exportable audit report for kill/ban actions with timestamp, target, path, reason, result, and confirmation context.
- [x] Health check screen: show autostart state, update state, feedback delivery, storage availability, current version, and last successful background scan.
- [x] Update integration tests: add local manifest/package smoke tests for update check, hash match, hash mismatch, skipped version, and startup auto-install flow.

## Visual QA 2026-07-04

- [x] Settings sub-tabs risk after adding Health: apply compact Settings sub-tab style and verify tab bounds with DPI-aware UIA.
- [x] Russian UI still shows the quick `Report bug` button in English.
- [x] External screenshot automation captures WPF content as blank/white; add an internal `--visual-qa-dir` screenshot path for visual checks.

## Review Follow-Ups 2026-07-04

- [x] 1. Release version discipline: keep `VERSION`, app assembly metadata, release tags, and GitHub assets in sync.
- [ ] 2. Update settings bug: do not re-enable automatic update checks just because `LastCheckedAt` is empty.
- [x] 3. Trust defaults: make startup auto-install and Start with Windows opt-in for self-signed builds.
- [ ] 4. Settings write safety: serialize settings updates or add an atomic update API so background Global Watch writes cannot overwrite UI settings.
- [ ] 5. Update service testability: use the injected `HttpClient` for manifest checks as well as installer downloads.
- [ ] 6. Feedback privacy and retention: make remote feedback configurable/opt-in and cap local markdown feedback reports.
- [x] 7. ViewModel size reduction: extract Global Watch, update, feedback, process-ban, recommendation, and health coordination services.
- [ ] 8. Release workflow assets: publish `SHA256SUMS.txt` and run release artifact smoke checks in GitHub Actions release workflow.
