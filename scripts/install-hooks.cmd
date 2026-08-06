@echo off
REM Installs the repository's git hooks. Hooks are per-clone and not versioned, so this has to be
REM run once after cloning if you want the pre-push privacy check.
setlocal
cd /d "%~dp0.."

if not exist ".git\hooks" (
  echo Not a git repository ^(no .git\hooks^).
  exit /b 1
)

copy /Y "scripts\hooks\pre-push" ".git\hooks\pre-push" >nul
if errorlevel 1 (
  echo Failed to install the pre-push hook.
  exit /b 1
)

echo Installed .git\hooks\pre-push
echo It runs scripts\check-no-secrets.ps1, which needs a .secret-patterns file to do anything.
endlocal
exit /b 0
