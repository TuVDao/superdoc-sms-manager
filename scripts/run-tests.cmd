@echo off
REM Runs the unit test suite. Needs only the .NET SDK - no modem, no SIM, no admin rights.
setlocal
cd /d "%~dp0.."
dotnet test Tests\SuperDoc.Sms.Tests.csproj -c Release --nologo
endlocal
