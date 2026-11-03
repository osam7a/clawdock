# Stops Claude Notch and removes it from startup and %LOCALAPPDATA%.
Stop-Process -Name ClaudeNotch -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item "$([Environment]::GetFolderPath('Startup'))\ClaudeNotch.lnk" -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\ClaudeNotch" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Claude Notch is uninstalled.'
