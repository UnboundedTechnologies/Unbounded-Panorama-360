<#
.SYNOPSIS
    Builds Unbounded Panorama 360 and installs it to %LocalAppData%\Programs\UnboundedPanorama360.

.DESCRIPTION
    Each install goes into its own folder (app-yyyyMMdd-HHmmss) because Windows cannot overwrite
    the files of a running app. A directory junction named "current" points at the newest one;
    the Start Menu shortcut and "Start with Windows" always use current\UnboundedPanorama360.exe.

    After installing, the new version is started with --replace: the running copy closes its USB
    session cleanly and the new one takes over (one UAC prompt, the app needs administrator rights
    for CPU sensors). Older version folders that are no longer in use are removed.

.PARAMETER NoLaunch
    Install only; do not start the new version.

.PARAMETER Desktop
    Also create a desktop shortcut.
#>
param(
    [switch]$NoLaunch,
    [switch]$Desktop
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$root = Join-Path $env:LOCALAPPDATA 'Programs\UnboundedPanorama360'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$target = Join-Path $root "app-$stamp"
$current = Join-Path $root 'current'
$exeName = 'UnboundedPanorama360.exe'

New-Item -ItemType Directory -Force $root | Out-Null

Write-Host "Building into $target ..."
& dotnet publish (Join-Path $repo 'src\Unbounded.Panorama.App\Unbounded.Panorama.App.csproj') -c Release -o $target --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $target $exeName))) { throw "Build output is missing $exeName." }

# Point "current" at the new version. rmdir on a junction removes only the link, never the target.
if (Test-Path $current) {
    $item = Get-Item $current -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "$current exists and is not a junction; refusing to touch it." }
    cmd /c rmdir "$current" | Out-Null
    if (Test-Path $current) { throw "Could not remove the old 'current' link." }
}
cmd /c mklink /J "$current" "$target" | Out-Null
if (-not (Test-Path (Join-Path $current $exeName))) { throw "The 'current' link does not lead to the new build." }
Write-Host "current -> app-$stamp"

# Shortcuts.
$shell = New-Object -ComObject WScript.Shell
$links = @(Join-Path ([Environment]::GetFolderPath('Programs')) 'Unbounded Panorama 360.lnk')
if ($Desktop) { $links += Join-Path ([Environment]::GetFolderPath('Desktop')) 'Unbounded Panorama 360.lnk' }
foreach ($link in $links) {
    $s = $shell.CreateShortcut($link)
    $s.TargetPath = Join-Path $current $exeName
    $s.WorkingDirectory = $current
    $s.Arguments = '--replace'
    $s.Description = 'Unbounded Panorama 360'
    $s.Save()
    Write-Host "Shortcut: $link"
}

if (-not $NoLaunch) {
    Write-Host 'Starting the new version (accept the administrator prompt)...'
    Start-Process (Join-Path $current $exeName) -ArgumentList '--replace'
    # Give the old copy time to close before cleaning up its folder.
    Start-Sleep -Seconds 20
}

# Remove older versions that are not in use (a running copy keeps its folder locked).
Get-ChildItem $root -Directory | Where-Object { $_.Name -match '^app-\d{8}-\d{6}$' -and $_.FullName -ne $target } | ForEach-Object {
    try {
        Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop
        Write-Host "Removed old version $($_.Name)"
    } catch {
        Write-Host "Kept $($_.Name) (still in use; removed on a later install)"
    }
}
Write-Host 'Done.'
