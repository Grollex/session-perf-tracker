# Commands - Session Perf Tracker

Run these from:

```powershell
cd "C:\Users\ThatSameHrian\Documents\New project"
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
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\release-one-click.ps1"
```

## Tests

```powershell
dotnet test "SessionPerfTracker.slnx" -c Release
```

The solution currently includes `SessionPerfTracker.UnitTests`.

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
