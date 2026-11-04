# Builds bin\Clawdock.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or Visual Studio needed.
# -Test builds and runs the tests in tests\ instead of the app. -Only picks tests by name.
# -Screenshots regenerates docs\screenshots. It covers your screen for about a minute.
param([switch]$Test, [string[]]$Only = @(), [switch]$Screenshots)

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

$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs -Recurse | ForEach-Object FullName

if ($Test) {
    $tests = Get-ChildItem (Join-Path $PSScriptRoot 'tests') -Filter *.cs | ForEach-Object FullName
    & "$fw\csc.exe" /nologo /codepage:65001 /target:exe /main:Clawdock.Tests.TestRunner "/out:$out\Clawdock.Tests.exe" @refs @sources @tests
    if ($LASTEXITCODE -ne 0) { throw 'Building the tests failed' }
    & "$out\Clawdock.Tests.exe" @Only
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    return
}

if ($Screenshots) {
    & "$fw\csc.exe" /nologo /codepage:65001 /target:exe /main:Clawdock.Tools.Screenshots "/out:$out\Clawdock.Screenshots.exe" @refs @sources (Join-Path $PSScriptRoot 'tools\Screenshots.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Building the screenshot tool failed' }
    # A running notch would sit on top of the shots, so it's stopped for them and started again.
    $running = Get-Process Clawdock -ErrorAction SilentlyContinue | Select-Object -First 1
    $path = if ($running) { $running.Path } else { $null }
    if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 400 }
    try { & "$out\Clawdock.Screenshots.exe" (Join-Path $PSScriptRoot 'docs\screenshots') }
    finally { if ($path) { Start-Process $path -WorkingDirectory (Split-Path $path) } }
    return
}

# The app icon: the mascot (the same shape as TrayIcon.DrawMascot) in clay, pixel-sharp at each size.
Add-Type -AssemblyName System.Drawing
$body = '..##########..', '..##.####.##..', '##############', '##############', '..##########..', '..#.#....#.#..'
$pngs = foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $clay = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xD9, 0x77, 0x57))
    $px = [Math]::Floor($size / 14)
    $ox = [Math]::Floor(($size - 14 * $px) / 2); $oy = [Math]::Floor(($size - 12 * $px) / 2)
    for ($r = 0; $r -lt 6; $r++) { for ($c = 0; $c -lt 14; $c++) {
        if ($body[$r][$c] -eq '#') { $g.FillRectangle($clay, [int]($ox + $c * $px), [int]($oy + $r * 2 * $px), [int]$px, [int](2 * $px)) }
    } }
    $mem = New-Object IO.MemoryStream
    $bmp.Save($mem, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $clay.Dispose(); $bmp.Dispose()
    , @($size, $mem.ToArray())
}
$icon = Join-Path $out 'Clawdock.ico'
$w = New-Object IO.BinaryWriter ([IO.File]::Create($icon))
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $b = if ($p[0] -ge 256) { [byte]0 } else { [byte]$p[0] }
    $w.Write($b); $w.Write($b); $w.Write([byte]0); $w.Write([byte]0); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$p[1].Length); $w.Write([int]$offset)
    $offset += $p[1].Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
$w.Close()

& "$fw\csc.exe" /nologo /codepage:65001 /target:winexe /optimize "/win32icon:$icon" "/out:$out\Clawdock.exe" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $out\Clawdock.exe"
