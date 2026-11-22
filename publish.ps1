# Builds a clean release zip for Flightsim.to / GitHub Releases.
#
#   .\publish.ps1                 -> self-contained (users need NOTHING installed)  [default]
#   .\publish.ps1 -FrameworkDependent -> smaller zip, users must install the .NET Desktop Runtime
#
# Output: release\MSFSAutoUnpause-v<version>.zip   (version comes from <Version> in the csproj)

param([switch]$FrameworkDependent)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$proj    = Join-Path $root 'MSFSAutoUnpause\MSFSAutoUnpause.csproj'
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $proj" }

$releaseDir = Join-Path $root 'release'
$stageDir   = Join-Path $releaseDir "MSFSAutoUnpause"
$zipPath    = Join-Path $releaseDir "MSFSAutoUnpause-v$version.zip"

Write-Host "Publishing MSFS Auto Unpause v$version ..." -ForegroundColor Cyan
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
if (Test-Path $zipPath)  { Remove-Item $zipPath -Force }

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish $proj -c Release -r win-x64 --self-contained $selfContained -o $stageDir -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# User-facing docs go in the zip root next to the exe
Copy-Item (Join-Path $root 'README.txt') $stageDir
Copy-Item (Join-Path $root 'LICENSE')    (Join-Path $stageDir 'LICENSE.txt')

# Sanity check: these must ship or the app will not start / cannot OCR
foreach ($f in 'MSFSAutoUnpause.exe','SimConnect.dll','Microsoft.FlightSimulator.SimConnect.dll',
               'Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll','icon.ico') {
    if (-not (Test-Path (Join-Path $stageDir $f))) { throw "Missing from release: $f" }
}

# Zip the folder itself so users extract a single "MSFSAutoUnpause" folder
Compress-Archive -Path $stageDir -DestinationPath $zipPath
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$size = '{0:N1} MB' -f ((Get-Item $zipPath).Length / 1MB)

Write-Host ""
Write-Host "Done: $zipPath  ($size)" -ForegroundColor Green
Write-Host "SHA256: $hash"
Write-Host "Next: upload to virustotal.com, then attach to the GitHub release + Flightsim.to."
