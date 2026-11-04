# Removes Wrenlet: its Claude Code hooks (only its own), its startup entry, and
# %LOCALAPPDATA%\Wrenlet. Same as Settings > Uninstall.
$exe = "$env:LOCALAPPDATA\Wrenlet\Wrenlet.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $PSScriptRoot 'bin\Wrenlet.exe' }
if (-not (Test-Path $exe)) { Write-Host 'Wrenlet is not installed.'; exit }
Start-Process $exe -ArgumentList '--uninstall' -Wait
