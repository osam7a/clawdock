# Builds Claude Notch, copies it to %LOCALAPPDATA%\ClaudeNotch, makes it start at login, and starts it.
# Also removes the older standalone CopilotToClaude program, which this replaces.
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'build.ps1')

$dest    = "$env:LOCALAPPDATA\ClaudeNotch"
$startup = [Environment]::GetFolderPath('Startup')

Stop-Process -Name ClaudeNotch, CopilotToClaude -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'bin\ClaudeNotch.exe') $dest -Force

$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut("$startup\ClaudeNotch.lnk")
$shortcut.TargetPath = "$dest\ClaudeNotch.exe"
$shortcut.WorkingDirectory = $dest
$shortcut.Save()

Remove-Item "$startup\CopilotToClaude.lnk" -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\CopilotToClaude" -Recurse -Force -ErrorAction SilentlyContinue

Start-Process "$dest\ClaudeNotch.exe"
Write-Host 'Claude Notch is installed and running.'
