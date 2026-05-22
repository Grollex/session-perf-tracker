# Agent Guide - Session Perf Tracker

## Project Goal

Session Perf Tracker is a Windows desktop application for monitoring process/session performance, inspecting noisy processes, exporting reports, and managing process-related workflows for power users and gamers.

## Architecture Rules

- Follow Clean Architecture:
  - `SessionPerfTracker.Domain`: pure business logic and abstractions.
  - `SessionPerfTracker.Infrastructure`: WMI, WinAPI, storage, collectors, settings, update services.
  - `SessionPerfTracker.App`: WPF UI and MVVM presentation layer.
- Check Domain abstractions before changing Infrastructure implementations.
- Follow MVVM. Do not put business logic in `.xaml.cs`; keep code-behind limited to UI glue.
- Localization lives in `src\SessionPerfTracker.App\Localization`; update RU and EN resources together.

## Agent Work Rules

- Answer the user in Russian unless another language is requested.
- Analyze structure before patching.
- Make small, reversible patches.
- Do not perform full rewrites or broad refactors without a direct reason.
- Do not delete files or reset Git state.
- Explain what changed, why, and how it was verified.
- Preserve the current MVP and existing working behavior.

## Commands

- Restore: `dotnet restore "SessionPerfTracker.slnx"`
- Build: `dotnet build "SessionPerfTracker.slnx" -c Debug`
- Run app: `dotnet run --project "src\SessionPerfTracker.App\SessionPerfTracker.App.csproj"`
- Package: `powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\package-windows.ps1"`
- Release helper: `powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\release-one-click.ps1"`

## Paste в терминале (Ctrl+V)

- **Ctrl+V не работает в cmd.exe по умолчанию.** Это ограничение терминала Windows, не opencode.
- **Способы вставки в cmd.exe:**
  - `Shift+Insert` — работает всегда
  - Правый клик мыши — если включён QuickEdit Mode
  - `Alt+Пробел → П → В` — меню cmd.exe
- **Рекомендация:** используй Windows Terminal (`wt`) — там Ctrl+Shift+V работает из коробки.
- **GUI Paste Helper:** `bat\OpenCode with Paste GUI.bat` — открывает окно с textbox (Ctrl+V работает), текст отправляется в opencode автоматически.
- **Когда пользователь жалуется что «паста/вставка/Ctrl+V не работает»** — объясни про Shift+Insert, и предложи запустить GUI Paste Helper.

## Clipboard / Paste Handling (агент)

- **Автоматически читай буфер обмена** когда пользователь говорит что скопировал/вставил что-то. Триггеры (RU): «вот», «на», «смотри», «скопировал», «вставка», «вставить», «паста», «глоток», «буфер», а также любые жалобы что паста/вставка не работает. Триггеры (EN): «paste», «copied», «clipboard», «here».
- Когда читаешь буфер — используй `powershell -NoProfile -Command "Get-Clipboard"` через bash tool. Если буфер пуст — скажи об этом.
- После чтения буфера: если контент похож на API-ключ, токен, путь к файлу — сразу предложи его применить (куда вставить), а не просто показывай.
- Команда `/paste` — читает буфер, показывает содержимое, уточняет что делать.
- Команда `/copy <текст>` — копирует текст в буфер через `powershell -NoProfile -Command "Set-Clipboard"`.
- Когда тебя просят просто скопировать путь, PID или команду — делай это сразу, без лишних вопросов.
- Выдавай результат plain text (без форматирования в markdown-блоки), если пользователь хочет скопировать.

## Performance And Logging

- Keep UI responsive; do not block the UI thread with monitoring, storage, network, or update work.
- Avoid high-frequency polling that increases CPU usage.
- Prefer explicit, useful diagnostic logs around process monitoring, storage failures, update checks, and packaging.
- Keep logs understandable for a Windows power user.
