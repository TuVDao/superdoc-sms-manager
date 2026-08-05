Write-Host "== SMS device prerequisites =="

$wwanService = Get-Service -Name WwanSvc -ErrorAction SilentlyContinue
if ($null -eq $wwanService) {
    Write-Warning "WwanSvc service not found."
}
else {
    Write-Host ("WwanSvc: {0} ({1})" -f $wwanService.Status, $wwanService.StartType)
}

Write-Host ""
Write-Host "WWAN/Cellular adapters:"
$adapters = Get-NetAdapter -ErrorAction SilentlyContinue |
    Where-Object { $_.InterfaceDescription -match "WWAN|Mobile|Cellular|Broadband" -or $_.Name -match "WWAN|Mobile|Cellular|Broadband" }

if ($adapters) {
    $adapters | Select-Object Name, InterfaceDescription, Status, LinkSpeed | Format-Table -AutoSize
}
else {
    Write-Warning "No WWAN/Cellular adapter detected by Get-NetAdapter."
}

Write-Host ""
Write-Host "Modem-like PnP devices:"
$pnp = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "Modem|WWAN|Mobile|Broadband|Sierra|Fibocom|Quectel|Huawei|EM7455|T77" }

if ($pnp) {
    $pnp | Select-Object Name, Status, PNPDeviceID | Format-Table -AutoSize
}
else {
    Write-Warning "No modem-like PnP device detected."
}
