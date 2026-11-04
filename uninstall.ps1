# Removes Clawdock: its Claude Code hooks (only its own), its startup entry, and
# %LOCALAPPDATA%\Clawdock. Same as Settings > Uninstall.
$exe = "$env:LOCALAPPDATA\Clawdock\Clawdock.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $PSScriptRoot 'bin\Clawdock.exe' }
if (-not (Test-Path $exe)) { Write-Host 'Clawdock is not installed.'; exit }
Start-Process $exe -ArgumentList '--uninstall' -Wait
