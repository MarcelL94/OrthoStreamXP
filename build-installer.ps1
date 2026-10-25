# Builds the OrthoStreamXP installer: publishes the program, then packs it with Velopack.
# The result is in artifacts\releases: OrthoStreamXP-Setup.msi (the installer people download: welcome, licence
# agreement to accept, just for me or all users, finish) and the files updates need later.
#
#   .\build-installer.ps1              the version in src\OrthoStreamXP\OrthoStreamXP.csproj
#   .\build-installer.ps1 -Version 0.1.0
param([string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$project = 'src\OrthoStreamXP\OrthoStreamXP.csproj'
if (-not $Version) { $Version = (dotnet msbuild $project -getProperty:Version).Trim() }
Write-Host "OrthoStreamXP $Version"

dotnet tool restore
if (Test-Path artifacts\publish) { Remove-Item artifacts\publish -Recurse -Force }
# A local build starts clean: Velopack refuses a version it already packed there.
if (Test-Path artifacts\releases) { Remove-Item artifacts\releases -Recurse -Force }
dotnet publish $project -c Release -o artifacts\publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# The licence page: the notice (beta, the imagery's terms), then the full GPL-3.0.
$license = (Get-Content installer\license-notice.md -Raw) + "`n`n---`n`n" + (Get-Content LICENSE -Raw)
Set-Content artifacts\license.md $license -Encoding utf8

# --framework: the installer installs the .NET desktop runtime first when the PC does not have it.
dotnet vpk pack `
    --packId OrthoStreamXP.App `
    --packVersion $Version `
    --packTitle OrthoStreamXP `
    --packAuthors OrthoStreamXP `
    --packDir artifacts\publish `
    --mainExe orthostreamxp.exe `
    --icon src\OrthoStreamXP\orthostreamxp.ico `
    --framework net10.0-x64-desktop `
    --runtime win-x64 `
    --msi `
    --instWelcome installer\welcome.txt `
    --instLicense artifacts\license.md `
    --instConclusion installer\conclusion.txt `
    --outputDir artifacts\releases
$packExit = $LASTEXITCODE
# The published files are inside the package now; nothing else needs them.
Remove-Item artifacts\publish -Recurse -Force
if ($packExit -ne 0) { throw 'pack failed' }

# vpk always builds a Setup.exe and a portable zip, plus assets.win.json (a manifest for vpk upload, which we don't
# use); the MSI is the installer we ship, named so users know which file to run.
Remove-Item artifacts\releases\OrthoStreamXP.App-win-Setup.exe, artifacts\releases\OrthoStreamXP.App-win-Portable.zip, artifacts\releases\assets.win.json -ErrorAction SilentlyContinue
Rename-Item artifacts\releases\OrthoStreamXP.App-win.msi OrthoStreamXP-Setup.msi

Get-ChildItem artifacts\releases | Format-Table Name, @{ Name = 'MB'; Expression = { [math]::Round($_.Length / 1MB, 1) } }
