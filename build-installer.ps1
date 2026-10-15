<#
.SYNOPSIS
    Builds dist\UnboundedPanorama360-Setup-<version>.exe.

.DESCRIPTION
    Publishes the app self-contained for 64-bit Windows (no .NET install needed on the target PC)
    and compiles installer\UnboundedPanorama360.iss with Inno Setup 6. The version comes from the
    app project. With -Run, the finished setup is started.

    Requires the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
#>
param([switch]$Run)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$project = Join-Path $repo 'src\Unbounded.Panorama.App\Unbounded.Panorama.App.csproj'
$publish = Join-Path $repo 'dist\app'

$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in the app project.' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Write-Host "Publishing $version (self-contained, win-x64)..."
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

Write-Host 'Compiling the installer...'
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$publish" (Join-Path $repo 'installer\UnboundedPanorama360.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)." }

$setup = Join-Path $repo "dist\UnboundedPanorama360-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "Expected $setup was not produced." }
Write-Host ("Built {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))

if ($Run) { Start-Process $setup }
