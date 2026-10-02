# Stops Claude Buddy and removes its autostart entry, Desktop shortcut and install folder.
[CmdletBinding()]
param([string]$InstallDir = (Join-Path $env:USERPROFILE '.claude-buddy'))

$ErrorActionPreference = 'Stop'
$target = Join-Path $InstallDir 'ClaudeBuddy.exe'

if (Test-Path $target) {
    Start-Process -FilePath $target -ArgumentList '--quit' -Wait
    Start-Sleep -Milliseconds 800
    Get-Process ClaudeBuddy -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target } | Stop-Process -Force
}

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $run -Name ClaudeBuddy -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $run -Name ClaudeBuddy
    Write-Host 'autostart entry removed'
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude Buddy.lnk'
if (Test-Path $shortcut) {
    Remove-Item $shortcut -Force
    Write-Host 'desktop shortcut removed'
}

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "removed $InstallDir"
}
