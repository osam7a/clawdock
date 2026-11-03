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
    "$fw\System.Security.dll",
    "$fw\System.IO.Compression.dll",
    "$fw\System.IO.Compression.FileSystem.dll",
    "$wpf\System.Speech.dll",
    # Windows Runtime, for modern dictation
    "$fw\System.Runtime.dll",
    "$fw\System.Runtime.InteropServices.WindowsRuntime.dll",
    "$env:WINDIR\System32\WinMetadata\Windows.Foundation.winmd",
    "$env:WINDIR\System32\WinMetadata\Windows.Globalization.winmd",
    "$env:WINDIR\System32\WinMetadata\Windows.Media.winmd"
) | ForEach-Object { "/r:$_" }

$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object FullName

& "$fw\csc.exe" /nologo /codepage:65001 /target:winexe /optimize "/out:$out\ClaudeNotch.exe" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $out\ClaudeNotch.exe"
