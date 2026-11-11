<#
.SYNOPSIS
    Builds the release EXE files into .\artifacts.

.DESCRIPTION
    Produces two variants:
      - spotify-taskbar-flyout.exe          (self-contained, runs without an installed .NET runtime)
      - spotify-taskbar-flyout-small.exe    (requires the .NET 10 Desktop Runtime)

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Version 1.2.0
#>
param(
    [string]$Version,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\SpotifyTaskbarFlyout\SpotifyTaskbarFlyout.csproj'
$out = Join-Path $root 'artifacts'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
$userSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk\dotnet.exe'
if (Test-Path $userSdk) { $dotnet = $userSdk }
if (-not $dotnet) { throw '.NET SDK not found. Download: https://dotnet.microsoft.com/download' }

$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

New-Item -ItemType Directory -Path $out -Force | Out-Null
Remove-Item "$out\self-contained", "$out\framework-dependent", "$out\spotify-taskbar-flyout.exe", "$out\spotify-taskbar-flyout-small.exe" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "==> Self-contained ($Runtime)" -ForegroundColor Cyan
& $dotnet publish $project -c Release -r $Runtime --self-contained true -o "$out\self-contained" @versionArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish (self-contained) failed.' }

Write-Host "==> Framework-dependent ($Runtime)" -ForegroundColor Cyan
& $dotnet publish $project -c Release -r $Runtime --self-contained false -o "$out\framework-dependent" @versionArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish (framework-dependent) failed.' }

Copy-Item "$out\self-contained\spotify-taskbar-flyout.exe" "$out\spotify-taskbar-flyout.exe"
Copy-Item "$out\framework-dependent\spotify-taskbar-flyout.exe" "$out\spotify-taskbar-flyout-small.exe"

Write-Host ''
Get-ChildItem $out -Filter *.exe | ForEach-Object {
    '{0,-36} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
