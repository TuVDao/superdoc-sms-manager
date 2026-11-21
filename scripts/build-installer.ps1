<#
.SYNOPSIS
    Builds the production application, creates an Inno Setup setup.exe installer (if Inno Setup is
    installed), and packages a portable ZIP release in the dist\ directory.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $scriptDir '..')
$dist = Join-Path $repo 'dist'
$appDir = Join-Path $repo 'app'
$issFile = Join-Path $scriptDir 'installer.iss'

Write-Host "=== 1/3: Publishing Release x64 App ===" -ForegroundColor Cyan
& (Join-Path $repo 'scripts\build.cmd') Release x64
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

if (-not (Test-Path $dist)) {
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
}

Write-Host "`n=== 2/3: Packaging Portable ZIP Distribution ===" -ForegroundColor Cyan
$zipFile = Join-Path $dist "SuperDoc-SmsManager-v2.0.0-win-x64-portable.zip"
if (Test-Path $zipFile) {
    Remove-Item $zipFile -Force
}
Compress-Archive -Path "$appDir\*" -DestinationPath $zipFile -CompressionLevel Optimal
Write-Host "Created portable archive: $zipFile" -ForegroundColor Green

Write-Host "`n=== 3/3: Searching for Inno Setup Compiler (iscc.exe) ===" -ForegroundColor Cyan
$isccLocations = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    "${env:LocalAppData}\Programs\Inno Setup 7\iscc.exe",
    "${env:LocalAppData}\Programs\Inno Setup 6\iscc.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\iscc.exe",
    "${env:ProgramFiles}\Inno Setup 7\iscc.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
    "${env:ProgramFiles}\Inno Setup 6\iscc.exe"
) | Where-Object { $_ -and (Test-Path $_) }

if ($isccLocations.Count -gt 0) {
    $iscc = $isccLocations[0]
    Write-Host "Found Inno Setup compiler: $iscc" -ForegroundColor Green
    Write-Host "Compiling setup installer..."
    & $iscc $issFile
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Installer built successfully in: $dist" -ForegroundColor Green
    } else {
        Write-Warning "Inno Setup compilation returned exit code $LASTEXITCODE"
    }
} else {
    Write-Host "Inno Setup (iscc.exe) was not found in standard paths." -ForegroundColor Yellow
    Write-Host "To compile the single-file setup.exe installer:"
    Write-Host "  1. Run: winget install JRSoftware.InnoSetup"
    Write-Host "  2. Or download from: https://jrsoftware.org/isdl.php"
    Write-Host "  3. Re-run: powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1"
    Write-Host "Your portable ZIP package is ready in: $zipFile" -ForegroundColor Green
}

Write-Host "`nAll build artifacts located in: $dist" -ForegroundColor Cyan
