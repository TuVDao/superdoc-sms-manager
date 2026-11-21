<#
.SYNOPSIS
    Re-initializes the Intel XMM 7360 modem driver and WWAN service to detect hot-plugged SIM cards.
.DESCRIPTION
    On HP EliteBook laptops with Intel XMM 7360 LTE modem, inserting a SIM card while
    Windows is running requires restarting the PnP device, cycling the radio, and restarting
    the WWAN AutoConfig service (WwanSvc) with Administrator privileges.
#>
[CmdletBinding()]
param(
    [switch]$NonInteractive
)

# --- 1. Auto-Elevation check ---
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Administrator privileges required to reset cellular hardware." -ForegroundColor Yellow
    Write-Host "Requesting elevation via Windows UAC..." -ForegroundColor Cyan
    $scriptPath = $MyInvocation.MyCommand.Path
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    if ($NonInteractive) {
        $argList += " -NonInteractive"
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  HP EliteBook 840 G6 - Cellular Modem & SIM Reset Tool   " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "[!] Note: Resetting cellular drivers and WWAN services may" -ForegroundColor Yellow
Write-Host "    momentarily cycle network connections." -ForegroundColor Yellow
Write-Host ""

# --- 2. Power on cellular radio via netsh ---
Write-Host "[1/5] Enabling cellular software radio..." -ForegroundColor White
try {
    netsh mbn set powerstate interface="Cellular" state=on | Out-Null
    Write-Host "      Software radio state set to ON." -ForegroundColor Green
}
catch {
    Write-Warning "      Could not set powerstate: $($_.Exception.Message)"
}

# --- 3. Restart WWAN AutoConfig service ---
Write-Host "[2/5] Restarting Windows WWAN AutoConfig service (WwanSvc)..." -ForegroundColor White
try {
    Restart-Service -Name WwanSvc -Force -ErrorAction Stop
    Write-Host "      WwanSvc restarted successfully." -ForegroundColor Green
}
catch {
    Write-Warning "      Could not restart WwanSvc: $($_.Exception.Message)"
}

# --- 4. Restart Intel XMM PnP Devices ---
Write-Host "[3/5] Power-cycling Intel XMM 7360 hardware devices..." -ForegroundColor White
$xmmDevices = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -like "*XMM*" }

if ($xmmDevices) {
    foreach ($dev in $xmmDevices) {
        Write-Host "      Restarting: $($dev.FriendlyName)..." -ForegroundColor Gray
        try {
            & pnputil /restart-device "$($dev.InstanceId)" | Out-Null
            Write-Host "      Device $($dev.FriendlyName) restarted." -ForegroundColor Green
        }
        catch {
            try {
                Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop
                Start-Sleep -Milliseconds 800
                Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop
                Write-Host "      Device $($dev.FriendlyName) toggled via PnP." -ForegroundColor Green
            }
            catch {
                Write-Warning "      Could not reset device $($dev.FriendlyName): $($_.Exception.Message)"
            }
        }
    }
}
else {
    Write-Host "      No Intel XMM device found by friendly name, checking network adapters..." -ForegroundColor Gray
}

# --- 5. Recycle Network Adapter (Target primary Cellular adapter only) ---
Write-Host "[4/5] Recycling Cellular network adapter..." -ForegroundColor White
$primaryAdapter = Get-NetAdapter -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq "Cellular" -or ($_.InterfaceDescription -match "XMM|Mobile Broadband" -and $_.Virtual -ne $true) } |
    Select-Object -First 1

if ($primaryAdapter) {
    Write-Host "      Restarting primary adapter: $($primaryAdapter.Name) ($($primaryAdapter.InterfaceDescription))..." -ForegroundColor Gray
    try {
        Restart-NetAdapter -Name $primaryAdapter.Name -Confirm:$false -ErrorAction Stop
        Write-Host "      Adapter $($primaryAdapter.Name) recycled." -ForegroundColor Green
    }
    catch {
        Write-Warning "      Could not restart adapter: $($_.Exception.Message)"
    }
}
else {
    Write-Host "      No active primary cellular adapter found." -ForegroundColor Gray
}

# Wait for modem firmware to re-enumerate SIM card
Write-Host "[5/5] Waiting for modem to read SIM card (5 seconds)..." -ForegroundColor White
Start-Sleep -Seconds 5

# Set radio on again after device reset
netsh mbn set powerstate interface="Cellular" state=on | Out-Null

Write-Host ""
Write-Host "==================== Diagnostic Status ====================" -ForegroundColor Cyan

$readyInfo = netsh mbn show readyinfo interface=* | Out-String
Write-Host $readyInfo

if ($readyInfo -match "State\s*:\s*Ready|SIM inserted") {
    Write-Host "SUCCESS: SIM card detected! You can now use SUPERDOC SMS Manager." -ForegroundColor Green
}
elseif ($readyInfo -match "State\s*:\s*SIM not inserted") {
    Write-Host "HARDWARE NOTE:" -ForegroundColor Yellow
    Write-Host "Windows still reports 'SIM not inserted'." -ForegroundColor Yellow
    Write-Host "On HP EliteBook 840 G6, if the hardware reset did not power the SIM tray," -ForegroundColor Yellow
    Write-Host "a standard Windows Restart is required to power the SIM tray pins." -ForegroundColor Yellow
}

Write-Host ""
if (-not $NonInteractive) {
    Write-Host "Press any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
else {
    Start-Sleep -Seconds 2
}
