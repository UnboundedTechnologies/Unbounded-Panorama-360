<#
.SYNOPSIS
    Installs one dependency of Unbounded Panorama 360 through winget, only if it is missing.
    Called by setup.exe. Exit codes: 0 = present or installed, 1 = install failed, 2 = winget missing.
#>
param(
    [Parameter(Mandatory)][ValidateSet('ffmpeg', 'ytdlp', 'pawnio', 'openrgb')][string]$Component,
    [string]$Log
)

function Write-Log([string]$text) {
    if ($Log) { Add-Content -Path $Log -Value "$(Get-Date -Format 'HH:mm:ss') [$Component] $text" -Encoding UTF8 }
}

# Where winget puts portable tools (per user and machine wide), plus PATH.
function Find-Tool([string]$exe, [string[]]$prefixes) {
    $roots = @("$env:LOCALAPPDATA\Microsoft\WinGet\Packages", "$env:ProgramFiles\WinGet\Packages")
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($prefix in $prefixes) {
            $hit = Get-ChildItem $root -Directory -Filter "$prefix*" -ErrorAction SilentlyContinue |
                ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter $exe -File -ErrorAction SilentlyContinue } | Select-Object -First 1
            if ($hit) { return $hit.FullName }
        }
    }
    foreach ($links in "$env:LOCALAPPDATA\Microsoft\WinGet\Links", "$env:ProgramFiles\WinGet\Links") {
        if (Test-Path (Join-Path $links $exe)) { return (Join-Path $links $exe) }
    }
    $cmd = Get-Command $exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Install-WingetPackage([string]$id) {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) { Write-Log 'winget is not available'; exit 2 }
    Write-Log "installing $id"
    & $winget.Source install --id $id --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
    Write-Log "winget exit code $LASTEXITCODE"
    return $LASTEXITCODE
}

# sc.exe exits 0 when the driver is registered, 1060 when it does not exist.
function Test-PawnIO { sc.exe query PawnIO | Out-Null; return $LASTEXITCODE -eq 0 }

switch ($Component) {
    'ffmpeg' {
        if (Find-Tool 'ffmpeg.exe' @('Gyan.FFmpeg', 'yt-dlp.FFmpeg', 'BtbN.FFmpeg')) { Write-Log 'already present'; exit 0 }
        Install-WingetPackage 'Gyan.FFmpeg.Essentials' | Out-Null
        if (Find-Tool 'ffmpeg.exe' @('Gyan.FFmpeg', 'yt-dlp.FFmpeg', 'BtbN.FFmpeg')) { exit 0 } else { exit 1 }
    }
    'ytdlp' {
        if (-not (Find-Tool 'yt-dlp.exe' @('yt-dlp.yt-dlp'))) { Install-WingetPackage 'yt-dlp.yt-dlp' | Out-Null }
        # YouTube needs a JavaScript runtime for its player checks; yt-dlp's package usually brings Deno along.
        if (-not (Find-Tool 'deno.exe' @('DenoLand.Deno'))) { Install-WingetPackage 'DenoLand.Deno' | Out-Null }
        if ((Find-Tool 'yt-dlp.exe' @('yt-dlp.yt-dlp')) -and (Find-Tool 'deno.exe' @('DenoLand.Deno'))) { exit 0 } else { exit 1 }
    }
    'pawnio' {
        # Kernel driver LibreHardwareMonitor uses for CPU temperature, clocks and power.
        if (Test-PawnIO) { Write-Log 'already present'; exit 0 }
        Install-WingetPackage 'namazso.PawnIO' | Out-Null
        if (Test-PawnIO) { exit 0 } else { exit 1 }
    }
    'openrgb' {
        # OpenRGB installs a Windows service ("OpenRGB") that detects and drives RGB devices of every
        # brand it supports; the ARGB page talks to it on 127.0.0.1:6742.
        sc.exe query OpenRGB | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Log 'already present'; exit 0 }
        Install-WingetPackage 'OpenRGB.OpenRGB' | Out-Null
        sc.exe query OpenRGB | Out-Null
        if ($LASTEXITCODE -eq 0) { exit 0 } else { exit 1 }
    }
}
