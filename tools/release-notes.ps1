# Writes a release's notes: its section of CHANGELOG.md, how to install, and the exe's checksum.
# Fails if the tag doesn't match the version in the code or the changelog has no section for it.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$version = $Tag.TrimStart('v')

$assembly = Get-Content (Join-Path $root 'src\App\AssemblyInfo.cs') -Raw
if ($assembly -notmatch "AssemblyVersion\(`"$([regex]::Escape($version))\.0`"\)") {
    throw "Tag $Tag doesn't match the version in src/App/AssemblyInfo.cs"
}

$section = New-Object System.Collections.Generic.List[string]
$inside = $false
foreach ($line in Get-Content (Join-Path $root 'CHANGELOG.md') -Encoding UTF8) {
    if ($line -match '^## ') {
        if ($inside) { break }
        $inside = $line -match "^## $([regex]::Escape($version))( |$)"
        continue
    }
    if ($inside) { $section.Add($line) }
}
$notes = ($section -join "`n").Trim()
if ($notes -eq '') { throw "CHANGELOG.md has no section for $version" }

$hash = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLower()
$text = @"
$notes

## Install

Download ``ClaudeNotch.exe`` and run it. It installs itself. To update, run the new exe the same way.

The exe isn't code-signed yet, so if Windows SmartScreen warns you, choose **More info**, then **Run anyway**.

## SHA-256

``````text
$hash  ClaudeNotch.exe
``````
"@
[IO.File]::WriteAllText($Out, ($text -replace "`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "Wrote $Out"
