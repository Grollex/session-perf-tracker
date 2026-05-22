@echo off
setlocal
cd /d "%~dp0.."
echo.
echo   [Paste Helper] Ctrl+V не работает в cmd.exe.
echo   Используй: Shift+Insert (вставка) или правый клик мыши.
echo   Или запусти: "bat\OpenCode with Paste GUI.bat" — там есть окно с Ctrl+V.
echo.
opencode --model pollinations/openai-fast --dangerously-skip-permissions
