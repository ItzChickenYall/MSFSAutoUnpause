# Builds a clean release zip for Flightsim.to / GitHub Releases.
#
#   .\publish.ps1          -> single exe + SimConnect DLLs (users need NOTHING installed)  [default]
#   .\publish.ps1 -Folder  -> old layout: exe + ~500 runtime DLLs in one folder (fallback if
#                             the single exe ever misbehaves; this is what v1.1.0-folder used)
#
# Output: release\MSFSAutoUnpause-v<version>.zip   (version comes from <Version> in the csproj)

param([switch]$Folder)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$proj    = Join-Path $root 'MSFSAutoUnpause\MSFSAutoUnpause.csproj'
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $proj" }

$releaseDir = Join-Path $root 'release'
$stageDir   = Join-Path $releaseDir "MSFSAutoUnpause"
$suffix     = if ($Folder) { '-folder' } else { '' }
$zipPath    = Join-Path $releaseDir "MSFSAutoUnpause-v$version$suffix.zip"

Write-Host "Publishing MSFS Auto Unpause v$version ($(if ($Folder) {'folder layout'} else {'single exe'})) ..." -ForegroundColor Cyan
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
if (Test-Path $zipPath)  { Remove-Item $zipPath -Force }

$args = @($proj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $stageDir,
          '-p:DebugType=None', '-p:DebugSymbols=false')
if (-not $Folder) {
    $args += '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
             '-p:EnableCompressionInSingleFile=true'
}
dotnet publish @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# User-facing docs go next to the exe
Copy-Item (Join-Path $root 'README.txt') $stageDir
Copy-Item (Join-Path $root 'LICENSE')    (Join-Path $stageDir 'LICENSE.txt')

# Sanity check: these must ship or the app will not start / cannot reach MSFS
$required = 'MSFSAutoUnpause.exe','SimConnect.dll','Microsoft.FlightSimulator.SimConnect.dll','icon.ico'
if ($Folder) { $required += 'Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll' }
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $stageDir $f))) { throw "Missing from release: $f" }
}

# Zip the folder itself so users extract a single "MSFSAutoUnpause" folder
Compress-Archive -Path $stageDir -DestinationPath $zipPath
$size = '{0:N1} MB' -f ((Get-Item $zipPath).Length / 1MB)

Write-Host ""
Write-Host "Done: $zipPath  ($size)" -ForegroundColor Green
Write-Host "Files in zip: $((Get-ChildItem $stageDir -Recurse -File).Count)"
Write-Host "Next: upload to virustotal.com, then Flightsim.to (and optionally a GitHub release)."
