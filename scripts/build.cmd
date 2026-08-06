@echo off
setlocal

rem Builds the shipping (unpackaged) app into the repo's app\ folder.
rem
rem   build.cmd [Configuration] [Platform]
rem   build.cmd Release ARM64
rem
rem Paths are derived from this script's own location, so the repository works
rem wherever it is cloned. %~dp0 ends with a backslash.

set "REPO=%~dp0.."
set "CONFIG=Release"
if not "%~1"=="" set "CONFIG=%~1"

rem x64 unless asked otherwise. ARM64 is for the Snapdragon and X13s class of machine, where an
rem x64 build would only run under emulation.
set "PLATFORM=x64"
if not "%~2"=="" set "PLATFORM=%~2"

rem The app locks its own executable while running, which makes the build fail
rem with MSB3027. Stop it first.
taskkill /IM SuperDoc.SmsManager.exe /F >nul 2>&1
rem The executable was called Message_T480s.WinUI.exe before v1.1. An installation from
rem before the rename holds the single-instance mutex under the old name, which would make
rem the newly built copy exit on startup instead of showing a window.
taskkill /IM Message_T480s.WinUI.exe /F >nul 2>&1
ping -n 3 127.0.0.1 >nul

rem Prefer a repo-local SDK if one has been placed here, otherwise use the one on PATH.
set "DOTNET=dotnet"
if exist "%REPO%\.dotnet\dotnet.exe" set "DOTNET=%REPO%\.dotnet\dotnet.exe"

"%DOTNET%" publish "%REPO%\WinUI\SuperDoc.SmsManager.csproj" ^
  -c %CONFIG% ^
  -p:Platform=%PLATFORM% ^
  -o "%REPO%\app"

if errorlevel 1 (
  echo.
  echo Build FAILED.
  exit /b 1
)

echo.
echo Published to %REPO%\app
echo Next: powershell -ExecutionPolicy Bypass -File "%~dp0install.ps1"
exit /b 0
