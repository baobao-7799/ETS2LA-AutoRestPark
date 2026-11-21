# Install AutoRestPark into the local ETS2LA (close ETS2LA first)
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'dist\AutoRestPark.dll'
if (-not (Test-Path $src)) { throw "Missing $src - run .\build.ps1 first" }

$dstDir = Join-Path $env:LOCALAPPDATA 'ETS2LA\current\Plugins'
New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
Copy-Item $src (Join-Path $dstDir 'AutoRestPark.dll') -Force
Write-Host "Installed to $dstDir\AutoRestPark.dll"
Write-Host 'Restart ETS2LA, then enable "baobao-7799 自动休息区停靠" under Settings -> Plugins.'
