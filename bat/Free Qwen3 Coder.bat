@echo off
setlocal
cd /d "%~dp0.."
echo.
echo   [Paste Helper] Ctrl+V не работает в cmd.exe.
echo   Используй: Shift+Insert (вставка) или правый клик мыши.
echo   Или запусти: "bat\OpenCode with Paste GUI.bat" — там есть окно с Ctrl+V.
echo.
opencode --agent free-qwen3-coder --dangerously-skip-permissions
