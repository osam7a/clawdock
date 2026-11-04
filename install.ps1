# Builds Wrenlet and runs it. The exe installs itself: it copies itself to
# %LOCALAPPDATA%\Wrenlet, starts with Windows, installs the Claude Code hooks, and
# restarts any copy that is already running (so this is also how you update).
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'build.ps1')
Start-Process (Join-Path $PSScriptRoot 'bin\Wrenlet.exe')
Write-Host 'Wrenlet is installing itself and will start in a moment.'
