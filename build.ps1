# Builds bin\ClaudeNotch.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or Visual Studio needed.
$ErrorActionPreference = 'Stop'

$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
    "$wpf\PresentationFramework.dll",
    "$wpf\PresentationCore.dll",
    "$wpf\WindowsBase.dll",
    "$wpf\UIAutomationClient.dll",
    "$wpf\UIAutomationTypes.dll",
    "$fw\System.Xaml.dll",
    "$fw\System.Core.dll",
    "$fw\System.Drawing.dll",
    "$fw\System.Windows.Forms.dll",
    "$fw\System.Web.Extensions.dll",
    "$wpf\System.Speech.dll"
) | ForEach-Object { "/r:$_" }

$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object FullName

& "$fw\csc.exe" /nologo /target:winexe /optimize "/out:$out\ClaudeNotch.exe" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $out\ClaudeNotch.exe"
