# Builds Claude Buddy, copies it to a permanent folder, puts a shortcut on the Desktop
# and starts it. Safe to run again: it replaces the installed copy.
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:USERPROFILE '.claude-buddy'),
    [switch]$NoShortcut,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $root 'build.ps1')

# Absolute path, so it can be compared with the path of a running copy.
$InstallDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDir)
$source = Join-Path $root 'bin\ClaudeBuddy.exe'
$target = Join-Path $InstallDir 'ClaudeBuddy.exe'
New-Item -ItemType Directory -Force $InstallDir | Out-Null

# Ask a running copy to exit, then wait for it to release the exe.
function Get-InstalledBuddy {
    Get-Process ClaudeBuddy -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target }
}
if (Test-Path $target) {
    Start-Process -FilePath $target -ArgumentList '--quit' -Wait
    for ($i = 0; $i -lt 25 -and (Get-InstalledBuddy); $i++) { Start-Sleep -Milliseconds 200 }
    Get-InstalledBuddy | Stop-Process -Force
}
Copy-Item $source $target -Force
Write-Host "installed $target"

if (-not $NoShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $desktop 'Claude Buddy.lnk'))
    $link.TargetPath = $target
    $link.WorkingDirectory = $InstallDir
    $link.IconLocation = "$target,0"
    $link.Description = 'Claude Code status buddy'
    $link.Save()
    Write-Host "shortcut  $(Join-Path $desktop 'Claude Buddy.lnk')"
}

if (-not $NoStart) {
    # Started through Explorer so it runs as an ordinary desktop process even when this
    # script is launched from a sandboxed terminal. On start it registers itself to run
    # at sign-in (HKCU Run).
    Start-Process explorer.exe -ArgumentList "`"$target`""
    Write-Host 'started'
}
