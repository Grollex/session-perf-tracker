# Bugs And Risks - Session Perf Tracker

## Known Risks

- Localization changes must stay synchronized between Russian and English resources (tests enforce key parity and existence).
- Process monitoring can become expensive if polling or event handling is too aggressive; re-measure overhead after changes to sampling, Global Watch or rendering.
- UI can freeze if process inspection, storage, update checks, or export work runs on the UI thread.
- `SystemProcessSnapshot` uses x64 structure offsets; an ARM64 build would need them re-verified.

## Fixed In 1.6.0

- Live and Global Watch used 5-8% of one core even when idle: per-process `Process` handle queries, repeated window enumeration and UI redraws while hidden. Replaced with one snapshot per tick, batched GPU counters and render-only-when-visible.
- Saving a long session rewrote all samples; sessions are now appended incrementally and the session list loads headers only.
- A crash or power loss left sessions half-written; interrupted sessions are recovered on startup.
- Recording a launcher ended when the launcher exited even though the game (child) kept running; children are now followed until the whole tree exits.
- Crash-like exits were misclassified when the exit code was an NTSTATUS value; exit codes are now classified (access violation, stack overflow, heap corruption, etc.).
- Launching an executable that requires elevation failed with Win32 error 740; it now retries with elevation.
- Global Watch showed `Unavailable` paths for many service/protected processes; the kernel image-name fallback resolves almost all of them.
- Signature detection reported catalog-signed Windows binaries as unsigned; catalog signatures are now checked, and legacy entries are refreshed.
- Suspicious/ban entries broke when an executable moved, updated or was reinstalled; entries now keep a fingerprint, verify presence and relocate automatically, with a path history.
- `dwm.exe` sometimes reported tens of GB of dedicated GPU memory; values larger than the adapter VRAM are dropped.
- Vertical scrollbars were squeezed to 8 px by the theme; only horizontal bars get the fixed height now.
- Generic file descriptions such as `Microsoft® Windows® Operating System` hid real app names in Global Watch.
- Around 110 status and hint messages were English-only in the Russian UI; they are localized now.
- Exit reasons, stability reasons, session notes, recommendation evidence and identity values (signer, `Unavailable`) were stored and shown in English; they are translated at display time now.
- The tag-triggered GitHub release workflow overwrote the locally signed installer and `version.json`, leaving `SHA256SUMS.txt` of 1.5.4/1.5.5 out of sync with the published installer; the workflow now leaves existing releases alone.
- Settings writes rewrote every key; only changed keys are written.

## Open Bugs / Limitations

- The system CPU/GPU strip shows `—` for the first few seconds after start until two Global Watch scans have run.
- A few Process Inspector detail lines (process tree summary, saved-entry explanations) and metric evidence lines such as `CPU avg ..., peak ...` are still English-only.
- The published 1.5.4/1.5.5 `SHA256SUMS.txt` assets do not match their installers (see above); `version.json`, which the in-app updater uses, is correct.
- GPU temperature depends on the driver exposing it through D3DKMT; many integrated GPUs and older drivers do not.
