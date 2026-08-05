param(
    [string]$PackageName = "MessageT480s.WinUI"
)

$pkg = Get-AppxPackage -Name $PackageName | Select-Object -First 1
if ($null -eq $pkg) {
    throw "Package '$PackageName' is not installed."
}

$appId = "$($pkg.PackageFamilyName)!App"
Start-Process "shell:AppsFolder\$appId"
Write-Host "Launched:" $appId
