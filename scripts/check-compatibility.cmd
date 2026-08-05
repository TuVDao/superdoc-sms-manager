@echo off
setlocal

rem Reports whether this machine's modem can send and receive SMS.
rem Read-only: it never transmits a message.

set "REPO=%~dp0.."

set "DOTNET=dotnet"
if exist "%REPO%\.dotnet\dotnet.exe" set "DOTNET=%REPO%\.dotnet\dotnet.exe"

"%DOTNET%" run --project "%REPO%\Tools\SmsCompatibilityCheck\SmsCompatibilityCheck.csproj" -c Release

echo.
pause
exit /b %ERRORLEVEL%
