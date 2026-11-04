# Removes Claude Notch: its Claude Code hooks (only its own), its startup entry, and
# %LOCALAPPDATA%\ClaudeNotch. Same as Settings > Uninstall.
$exe = "$env:LOCALAPPDATA\ClaudeNotch\ClaudeNotch.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $PSScriptRoot 'bin\ClaudeNotch.exe' }
if (-not (Test-Path $exe)) { Write-Host 'Claude Notch is not installed.'; exit }
Start-Process $exe -ArgumentList '--uninstall' -Wait
