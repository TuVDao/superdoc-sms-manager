param(
    [string]$MsixRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\msix"),
    [string]$LogPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\admin-install-winui-msix.log"),
    [switch]$WhatIf
)

try {
    # Earlier builds leave their version folders behind without a .msix in them, and the output
    # root also holds unrelated folders such as 'runtimes'. Only consider folders that actually
    # contain a package, otherwise the newest-looking folder can be an empty one.
    $candidates = Get-ChildItem -Path $MsixRoot -Directory -ErrorAction Stop | Where-Object {
        Get-ChildItem -Path $_.FullName -File -Filter "*.msix" -ErrorAction SilentlyContinue
    }

    if ($null -eq $candidates -or @($candidates).Count -eq 0) {
        throw "No folder under '$MsixRoot' contains a .msix. Build the package first: scripts\build-winui-msix.cmd"
    }

    $latestDir = $candidates |
        Sort-Object {
            if ($_.Name -match 'SuperDoc\.SmsManager_(\d+\.\d+\.\d+\.\d+)_x64_Test') {
                [version]$matches[1]
            }
            else {
                [version]"0.0.0.0"
            }
        }, LastWriteTime -Descending |
        Select-Object -First 1

    $msix = Get-ChildItem -Path $latestDir.FullName -File -Filter "*.msix" | Select-Object -First 1
    Write-Host "Selected package:" $msix.FullName

    $dependencyPaths = @()
    $x64Deps = Join-Path $latestDir.FullName "Dependencies\\x64"
    if (Test-Path $x64Deps) {
        $dependencyPaths += Get-ChildItem -Path $x64Deps -Filter "*.msix" -File | Select-Object -ExpandProperty FullName
    }

    if ($dependencyPaths.Count -gt 0) {
        Add-AppxPackage -Path $msix.FullName -DependencyPath $dependencyPaths -AllowUnsigned -ForceApplicationShutdown -WhatIf:$WhatIf -ErrorAction Stop
    }
    else {
        Add-AppxPackage -Path $msix.FullName -AllowUnsigned -ForceApplicationShutdown -WhatIf:$WhatIf -ErrorAction Stop
    }

    if ($WhatIf) {
        "WhatIf succeeded for package: $($msix.FullName)" | Out-File -FilePath $LogPath -Encoding utf8
    }
    else {
        "Installed package: $($msix.FullName)" | Out-File -FilePath $LogPath -Encoding utf8
    }
    exit 0
}
catch {
    $_ | Format-List * -Force | Out-File -FilePath $LogPath -Encoding utf8
    exit 1
}
