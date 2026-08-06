<#
.SYNOPSIS
    Installs the published SUPERDOC SMS Manager for the current user.

.DESCRIPTION
    The app ships as an unpackaged build, because a sideloaded unsigned MSIX cannot receive SMS
    (every registration returns 0xD0000022 - see README). "Installing" therefore means:
      - a Start menu shortcut, so it can be launched like any other app
      - optionally a desktop shortcut
    Starting with Windows is handled by the app itself: on every run it points
    HKCU\...\Run at its own executable, so it survives being moved or republished.

    No elevation required - everything is per-user.
#>
param(
    # Defaults to the app\ folder beside this repository, so the script works wherever
    # the repository has been cloned.
    [string]$AppDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "app"),
    [switch]$Desktop,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $AppDir "SuperDoc.SmsManager.exe"
if (-not (Test-Path $exe)) {
    throw "Not found: $exe`nBuild it first with: scripts\build.cmd"
}

$shell = New-Object -ComObject WScript.Shell
$name = "SUPERDOC SMS Manager"

# Before v1.1 the executable was Message_T480s.WinUI.exe. Publishing does not delete files it no
# longer produces, so the old one would sit in app\ still being launched by the old Run entry -
# two copies competing for one modem registration. The Run value name has not changed, so the
# app repoints it at itself on first launch; only the stale executable has to be cleared out.
$legacyExe = Join-Path $AppDir "Message_T480s.WinUI.exe"
if (Test-Path $legacyExe) {
    Get-Process -Name "Message_T480s.WinUI" -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $legacyExe -Force -ErrorAction SilentlyContinue
    Write-Host "Removed the pre-rename executable from $AppDir."
}

function New-Shortcut([string]$path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $AppDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = "Send and receive SMS through the built-in WWAN modem"
    $lnk.Save()
    Write-Host "Shortcut: $path"
}

$startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "$name.lnk"
New-Shortcut $startMenu

if ($Desktop) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath("Desktop")) "$name.lnk")
}

$run = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue).$name
if ($run) {
    Write-Host "Start with Windows: $run"
}
else {
    Write-Host "Start with Windows: not registered yet - it is set the first time the app runs."
}

if (-not $NoLaunch) {
    $running = Get-Process -Name "SuperDoc.SmsManager" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Already running (PID $($running.Id))."
    }
    else {
        Start-Process $exe
        Write-Host "Launched."
    }
}
