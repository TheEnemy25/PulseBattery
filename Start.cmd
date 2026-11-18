@echo off
set "DOTNET_CLI_HOME=%~dp0.local"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
if exist "%~dp0artifacts\PulseBattery\PulseBattery.exe" (
    start "" "%~dp0artifacts\PulseBattery\PulseBattery.exe" --tray
) else (
    dotnet run --project "%~dp0src\PulseBattery.App" -c Release -- --tray
)
