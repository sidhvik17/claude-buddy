# Stops Claude Buddy and removes its autostart entry, its Desktop shortcut and its own files.
# Only files the buddy wrote are deleted. The folder is removed only if that leaves it empty.
[CmdletBinding()]
param([string]$InstallDir)

$ErrorActionPreference = 'Stop'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if (-not $InstallDir) {
    # Default: wherever the autostart entry says the buddy lives, else the standard folder.
    $InstallDir = Join-Path $env:USERPROFILE '.claude-buddy'
    $registered = (Get-ItemProperty -Path $run -Name ClaudeBuddy -ErrorAction SilentlyContinue).ClaudeBuddy
    if ($registered) {
        $exe = $registered.Trim('"')
        if ((Split-Path -Leaf $exe) -eq 'ClaudeBuddy.exe' -and (Test-Path -LiteralPath $exe -PathType Leaf)) {
            $InstallDir = Split-Path -Parent $exe
        }
    }
}
# Absolute path, so it can be compared with the path of a running copy.
$InstallDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDir)
$target = Join-Path $InstallDir 'ClaudeBuddy.exe'

if (Test-Path -LiteralPath $target -PathType Leaf) {
    Start-Process -FilePath $target -ArgumentList '--quit' -Wait
    Start-Sleep -Milliseconds 800
    Get-Process ClaudeBuddy -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target } | Stop-Process -Force
} else {
    Write-Warning "ClaudeBuddy.exe not found in $InstallDir. If it was installed somewhere else, run uninstall.ps1 -InstallDir <that folder>; a buddy running from there has not been stopped or removed."
}

if (Get-ItemProperty -Path $run -Name ClaudeBuddy -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $run -Name ClaudeBuddy
    Write-Host 'autostart entry removed'
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude Buddy.lnk'
if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
    Remove-Item -LiteralPath $shortcut -Force
    Write-Host 'desktop shortcut removed'
}

if (Test-Path -LiteralPath $InstallDir -PathType Container) {
    foreach ($name in 'ClaudeBuddy.exe', 'config.ini', 'config.dev.ini', 'buddy.log', 'buddy.log.old') {
        $file = Join-Path $InstallDir $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force }
    }
    if (@(Get-ChildItem -LiteralPath $InstallDir -Force).Count -eq 0) {
        Remove-Item -LiteralPath $InstallDir -Force
        Write-Host "removed $InstallDir"
    } else {
        Write-Host "removed Claude Buddy's files; $InstallDir holds other files and was left in place"
    }
}
