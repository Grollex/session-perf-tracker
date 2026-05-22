@echo off
setlocal
cd /d "%~dp0.."
title OpenCode Paste Helper
echo ============================================
echo   OpenCode Paste Helper (только вставка)
echo ============================================
echo.
echo   Открывает окно для вставки текста.
echo   Ctrl+V работает в окне Paste Helper.
echo   Должен быть уже запущен opencode в другом окне.
echo.
start "Paste Helper" powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\opencode-paste-gui.ps1" -NoLaunch
echo   Paste Helper запущен. Это окно можно закрыть.
echo.
pause
