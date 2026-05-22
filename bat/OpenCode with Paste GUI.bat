@echo off
setlocal
cd /d "%~dp0.."
title OpenCode + Paste Helper
echo ============================================
echo   OpenCode + Paste Helper
echo ============================================
echo.
echo   Запускаю opencode и Paste Helper...
echo   Ctrl+V работает в окне Paste Helper
echo.
start "OpenCode" opencode --agent free-qwen3-coder --dangerously-skip-permissions
timeout /t 3 /nobreak >nul
start "Paste Helper" powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\opencode-paste-gui.ps1" -NoLaunch
echo.
echo   Paste Helper запущен. Вставь текст в его окне (Ctrl+V)
echo   и нажми «Отправить» — текст попадёт в opencode.
echo   Это окно можно закрыть.
echo.
pause
