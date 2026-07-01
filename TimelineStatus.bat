@echo off
set "TIMELINE_ROOT=%~dp0"
dotnet run --project "%TIMELINE_ROOT%launcher\Timeline.Launcher.csproj" -- --root "%~dp0." status
pause
