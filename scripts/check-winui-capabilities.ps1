param(
    [string]$PackageName = "MessageT480s.WinUI",
    [string]$ManifestPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "WinUI\Package.appxmanifest")
)

$pkg = Get-AppxPackage -Name $PackageName | Select-Object -First 1
if ($null -ne $pkg) {
    $ManifestPath = Join-Path $pkg.InstallLocation "AppxManifest.xml"
    Write-Host "Using installed package manifest."
}
else {
    if (-not (Test-Path $ManifestPath)) {
        throw "Package '$PackageName' is not installed and fallback manifest '$ManifestPath' was not found."
    }

    Write-Warning "Package '$PackageName' is not installed. Checking source manifest instead."
}

if (-not (Test-Path $ManifestPath)) {
    throw "Manifest not found at '$ManifestPath'."
}

[xml]$xml = Get-Content -Path $ManifestPath
$capNodes = $xml.SelectNodes("//*[local-name()='Capabilities']/*")
$capEntries = @()
$capValues = @()
foreach ($node in $capNodes) {
    $element = $node.LocalName
    $value = $node.Attributes["Name"].Value
    $capEntries += ("{0}:{1}" -f $element, $value)
    $capValues += $value
}

if ($null -ne $pkg) {
    Write-Host "Package:" $pkg.Name
    Write-Host "Family :" $pkg.PackageFamilyName
}
Write-Host "Manifest:" $ManifestPath
Write-Host "Capabilities:"
$capEntries | Sort-Object | ForEach-Object { Write-Host " - $_" }

$required = @(
    "internetClient",
    "cellularMessaging",
    "mobileBroadband",
    "runFullTrust"
)

$missing = @()
foreach ($req in $required) {
    if ($capValues -notcontains $req) {
        $missing += $req
    }
}

if ($missing.Count -gt 0) {
    Write-Warning ("Missing required capabilities: " + ($missing -join ", "))
    exit 2
}

Write-Host "Capability check passed."
