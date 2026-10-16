#Requires -Version 7.2
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/OfficeAddin' }
& dotnet build (Join-Path $PSScriptRoot 'RimePPT.OfficeAddin/RimePPT.OfficeAddin.csproj') -c Release -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Office add-in build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Register-OfficeAddin.ps1') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RimePPT.OfficeAddin/README.md') -Destination $OutputDirectory
Write-Output "Office add-in: $OutputDirectory"
