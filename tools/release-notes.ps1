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

Download ``Clawdock.exe`` and run it. It installs itself, and later updates install by themselves.

The exe isn't code-signed yet, so if Windows SmartScreen warns you, choose **More info**, then **Run anyway**.

## Check the download

This exe was built by GitHub Actions from this tag. To confirm that, use the [GitHub CLI](https://cli.github.com):

``````powershell
gh attestation verify Clawdock.exe --repo osam7a/clawdock
``````

Its SHA-256:

``````text
$hash  Clawdock.exe
``````
"@
[IO.File]::WriteAllText($Out, ($text -replace "`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "Wrote $Out"
