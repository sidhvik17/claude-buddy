# Builds bin\ClaudeBuddy.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or package install needed.
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found: .NET Framework 4.x is required (it ships with Windows 10/11).' }

$bin = Join-Path $root 'bin'
$exe = Join-Path $bin 'ClaudeBuddy.exe'
$ico = Join-Path $root 'assets\buddy.ico'
New-Item -ItemType Directory -Force $bin | Out-Null
New-Item -ItemType Directory -Force (Join-Path $root 'assets') | Out-Null

function Invoke-Csc([string]$icon) {
    $cscArgs = @(
        '/nologo', '/target:winexe', '/optimize+', '/warn:4', '/platform:anycpu',
        "/out:$exe",
        "/win32manifest:$(Join-Path $root 'src\app.manifest')",
        '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
    )
    if ($icon) { $cscArgs += "/win32icon:$icon" }
    $cscArgs += (Join-Path $root 'src\*.cs')
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
}

function Invoke-Buddy([string[]]$buddyArgs) {
    $p = Start-Process -FilePath $exe -ArgumentList $buddyArgs -Wait -PassThru -WindowStyle Hidden
    return $p.ExitCode
}

# The icon is drawn by the program itself, so the first build bootstraps it.
if (-not (Test-Path $ico)) {
    Invoke-Csc $null
    if ((Invoke-Buddy @('--make-icon', "`"$ico`"")) -ne 0 -or -not (Test-Path $ico)) { throw 'icon generation failed' }
}
Invoke-Csc $ico
Write-Host "built $exe"

if (-not $SkipTests) {
    $report = Join-Path $bin 'selftest.txt'
    $failed = Invoke-Buddy @('--selftest', "`"$report`"")
    Get-Content $report | Where-Object { $_ -notlike 'PASS*' } | ForEach-Object { Write-Host $_ }
    if ($failed -ne 0) { throw "self-test: $failed check(s) failed (see $report)" }
}
