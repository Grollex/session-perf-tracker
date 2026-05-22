@echo off
setlocal
cd /d "%~dp0.."
title OpenCode Full Access + Paste Helper
echo ============================================
echo   OpenCode Full Access + Paste Helper
echo ============================================
echo.
echo   Запускаю opencode (Full Access) и Paste Helper...
echo   Ctrl+V работает в окне Paste Helper
echo.
start "OpenCode" opencode --model omniroute/codex/gpt-5.4-mini --dangerously-skip-permissions
timeout /t 3 /nobreak >nul
start "Paste Helper" powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\opencode-paste-gui.ps1" -NoLaunch -FullAccess
echo.
echo   Paste Helper запущен. Вставь текст в его окне (Ctrl+V)
echo   и нажми «Отправить» — текст попадёт в opencode.
echo   Это окно можно закрыть.
echo.
pause
