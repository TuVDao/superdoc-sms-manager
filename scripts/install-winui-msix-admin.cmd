@echo off
setlocal

rem Installs the MSIX package. Unsigned packages need elevation, so this relaunches
rem PowerShell through UAC and waits for it.

set "REPO=%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process PowerShell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','%~dp0admin-install-winui-msix.ps1','-MsixRoot','%REPO%\artifacts\msix','-LogPath','%REPO%\artifacts\admin-install-winui-msix.log' -Wait"

exit /b %ERRORLEVEL%
