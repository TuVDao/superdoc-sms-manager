<#
.SYNOPSIS
    Re-initializes the cellular modem driver and WWAN service to detect hot-plugged SIM cards.
.DESCRIPTION
    On many Windows laptops (such as HP with Intel XMM7360 LTE modem), inserting a SIM card
    while the system is running is not immediately picked up by the radio driver until
    the WWAN AutoConfig service (WwanSvc) and the cellular network adapter are recycled.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

Write-Host "== Re-initializing Cellular Modem and SIM ==" -ForegroundColor Cyan

# 1. Restart the Windows WWAN AutoConfig service
Write-Host "1. Restarting WWAN AutoConfig service (WwanSvc)..."
try {
    Restart-Service -Name WwanSvc -Force -ErrorAction Stop
    Write-Host "   WwanSvc restarted successfully." -ForegroundColor Green
}
catch {
    Write-Warning "   Could not restart WwanSvc service directly: $($_.Exception.Message)"
}

# 2. Cycle the Cellular Network Adapter
Write-Host "2. Recycling Cellular network adapter..."
$adapters = Get-NetAdapter -ErrorAction SilentlyContinue |
    Where-Object { $_.InterfaceDescription -match "WWAN|Mobile|Cellular|Broadband|XMM" -or $_.Name -match "Cellular" }

if ($adapters) {
    foreach ($adapter in $adapters) {
        Write-Host "   Cycling adapter: $($adapter.Name) ($($adapter.InterfaceDescription))..."
        try {
            Restart-NetAdapter -Name $adapter.Name -Confirm:$false -ErrorAction Stop
            Write-Host "   Adapter $($adapter.Name) recycled." -ForegroundColor Green
        }
        catch {
            Write-Warning "   Note: Running as Administrator may be required to restart the network adapter."
        }
    }
}
else {
    Write-Warning "   No active cellular adapter found."
}

Write-Host "`nModem refresh completed. Please check the SMS Manager window." -ForegroundColor Cyan
