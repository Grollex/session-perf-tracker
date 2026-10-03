# Commands - Session Perf Tracker

Run these from:

```powershell
cd "C:\Users\ThatSameHrian\Documents\Session Per Tracker Development"
```

## Project Inspection

```powershell
git status --short --branch
Get-ChildItem -LiteralPath ".\src" -Directory
Get-ChildItem -LiteralPath ".\src" -Recurse -Filter "*.csproj"
```

## Restore And Build

```powershell
dotnet restore "SessionPerfTracker.slnx"
dotnet build "SessionPerfTracker.slnx" -c Debug
dotnet build "SessionPerfTracker.slnx" -c Release
```

## Run

```powershell
dotnet run --project "src\SessionPerfTracker.App\SessionPerfTracker.App.csproj"
```

## Packaging

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\package-windows.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\test-packaged-app.ps1" -ExecutablePath ".\artifacts\release\win-x64\publish\SessionPerfTracker.App.exe"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\release-one-click.ps1"
```

## Tests

```powershell
dotnet test "SessionPerfTracker.slnx" -c Release
```

The solution currently includes `SessionPerfTracker.UnitTests`. Some tests start short-lived `powershell`/`ping` processes, so run them on Windows.

## Visual QA (isolated data)

```powershell
$qa = "$env:TEMP\spt-qa"
.\src\SessionPerfTracker.App\bin\Release\net10.0-windows\SessionPerfTracker.App.exe --data-dir "$qa\data" --visual-qa-dir "$qa\shots" --visual-qa-record
```

`--data-dir` keeps QA away from `%LocalAppData%\SessionPerfTracker`; `--visual-qa-record` records a scripted load generator before the tab screenshots.

## Tool Diagnostics

```powershell
dotnet --version
git --version
python --version
node -v
npm -v
ffmpeg -version
ffprobe -version
```

Note: `ffmpeg` and `ffprobe` were not found in PATH during the memory setup pass.
