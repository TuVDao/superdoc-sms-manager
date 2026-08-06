<#
.SYNOPSIS
    Regenerates the documentation screenshots from invented data.

.DESCRIPTION
    Publishes a throwaway copy of the app, fills a separate database with fictional contacts and
    conversations, photographs the window and shuts the copy down again.

    Nothing here touches the installed app in app\, the real database, or the modem: the demo
    build runs with SUPERDOC_SMS_DEMO=1, which skips modem initialisation entirely so it cannot
    take the receive registration away from the copy you actually use.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\capture-screenshots.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [int]    $SettleSeconds = 6
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not reliably populated in a param block default under PS 5.1.
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $scriptDirectory '..')

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo 'docs\screenshots'
}
$staging = Join-Path $repo 'artifacts\demo'
$demoDb = Join-Path $env:LOCALAPPDATA 'smsmanager-demo.db'

Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Win32Capture
{
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

function Save-WindowImage {
    param(
        [IntPtr] $Handle,
        [string] $Path
    )

    $rect = New-Object Win32Capture+RECT
    if (-not [Win32Capture]::GetWindowRect($Handle, [ref] $rect)) {
        throw 'Could not measure the window.'
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        throw "The window reported an unusable size ($width x $height)."
    }

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $hdc = $graphics.GetHdc()
            try {
                # Flag 2 is PW_RENDERFULLCONTENT, which is required for composited WinUI 3
                # windows; without it the capture comes back black.
                if (-not [Win32Capture]::PrintWindow($Handle, $hdc, 2)) {
                    throw 'PrintWindow refused to render the window.'
                }
            }
            finally { $graphics.ReleaseHdc($hdc) }
        }
        finally { $graphics.Dispose() }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

Write-Host 'Publishing a throwaway build (app\ is left alone)...'
& dotnet publish (Join-Path $repo 'WinUI\SuperDoc.SmsManager.csproj') `
    -c Release -p:Platform=x64 -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host 'Writing demo data...'
foreach ($suffix in @('', '-wal', '-shm')) {
    $file = "$demoDb$suffix"
    if (Test-Path $file) { Remove-Item $file -Force }
}

$env:SUPERDOC_SMS_DEMO = '1'
& dotnet run --project (Join-Path $repo 'Cli\SuperDoc.Sms.Cli.csproj') `
    -c Release -p:Platform=x64 --no-build -- seed-demo
if ($LASTEXITCODE -ne 0) {
    # The CLI is not published above, so build it on demand rather than failing here.
    & dotnet run --project (Join-Path $repo 'Cli\SuperDoc.Sms.Cli.csproj') `
        -c Release -p:Platform=x64 -- seed-demo
    if ($LASTEXITCODE -ne 0) { throw 'Seeding failed.' }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = Resolve-Path $OutputDirectory

Write-Host 'Launching the demo window...'
$exe = Join-Path $staging 'SuperDoc.SmsManager.exe'
if (-not (Test-Path $exe)) { throw "Not found: $exe" }

$process = Start-Process -FilePath $exe -PassThru
try {
    # The window has to exist, render and finish its first data load before it is worth
    # photographing; polling for a handle is not enough on its own.
    $deadline = (Get-Date).AddSeconds(30)
    while ($process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    }

    if ($process.MainWindowHandle -eq 0) { throw 'The demo window never appeared.' }

    Start-Sleep -Seconds $SettleSeconds
    [void][Win32Capture]::SetForegroundWindow($process.MainWindowHandle)
    Start-Sleep -Milliseconds 800

    $target = Join-Path $OutputDirectory 'conversations.png'
    Save-WindowImage -Handle $process.MainWindowHandle -Path $target
    Write-Host "Saved $target"
}
finally {
    if (-not $process.HasExited) {
        # By process id only: the copy the user actually runs must survive this script.
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    Remove-Item Env:\SUPERDOC_SMS_DEMO -ErrorAction SilentlyContinue
}

Write-Host 'Done.'
