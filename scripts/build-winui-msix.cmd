@echo off
setlocal

rem Builds the MSIX package.
rem
rem NOTE: a sideloaded unsigned MSIX cannot receive SMS on any machine tested - every
rem registration returns 0xD0000022. This script is kept because the package still builds
rem and installs, and because a properly signed Store build with an approved
rem cellularMessaging capability may behave differently. For everyday use build the
rem unpackaged app instead: scripts\build.cmd

set "REPO=%~dp0.."
set "CONFIG=Release"
if not "%~1"=="" set "CONFIG=%~1"

set "OUTDIR=%REPO%\artifacts\msix\"
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

set "DOTNET=dotnet"
if exist "%REPO%\.dotnet\dotnet.exe" set "DOTNET=%REPO%\.dotnet\dotnet.exe"

"%DOTNET%" publish "%REPO%\WinUI\Message_T480s.WinUI.csproj" ^
  -c %CONFIG% ^
  -p:Platform=x64 ^
  -p:RuntimeIdentifier=win-x64 ^
  -p:GenerateAppxPackageOnBuild=true ^
  -p:AppxPackageSigningEnabled=false ^
  -p:AppxPackageDir="%OUTDIR%"

exit /b %ERRORLEVEL%
