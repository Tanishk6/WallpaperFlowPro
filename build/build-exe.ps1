<#
.SYNOPSIS
  One-step build for people who just want the .exe. Installs what's missing (via winget), fetches mpv/FFmpeg,
  publishes a self-contained build, a portable zip and, if Inno Setup is available, the installer.
#>
param([string]$Version = "1.0.0", [switch]$NoInstaller)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

function Refresh-Path {
    $env:PATH = [Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [Environment]::GetEnvironmentVariable("PATH", "User")
}
function Winget-Install([string]$id) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { return $false }
    Write-Host "Installing $id with winget..." -ForegroundColor Cyan
    winget install --id $id -e --silent --accept-source-agreements --accept-package-agreements | Out-Host
    Refresh-Path
    return $true
}
function Find-Iscc {
    @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}

$failure = $null
try {
    # 1. .NET 8 SDK
    $hasSdk = (Get-Command dotnet -ErrorAction SilentlyContinue) -and ((dotnet --list-sdks) -match '^8\.')
    if (-not $hasSdk) {
        if (-not (Winget-Install "Microsoft.DotNet.SDK.8")) { throw "Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 and run this again." }
        if (-not ((dotnet --list-sdks) -match '^8\.')) { throw ".NET 8 SDK was installed but isn't on PATH yet. Close this window and run Build-Exe.cmd again." }
    }

    # 2. 7-Zip (mpv ships as .7z)
    $has7z = (Test-Path "$env:ProgramFiles\7-Zip\7z.exe") -or (Get-Command 7z -ErrorAction SilentlyContinue)
    if (-not $has7z) { Winget-Install "7zip.7zip" | Out-Null }

    # 3. mpv + FFmpeg
    if (-not (Test-Path "$root\tools\mpv.exe") -or -not (Test-Path "$root\tools\ffmpeg.exe")) { & "$PSScriptRoot\fetch-deps.ps1" -Lgpl }

    # 4. Inno Setup for the installer
    $buildArgs = @{ Version = $Version; SkipTests = $true }
    if (-not $NoInstaller) {
        if (-not (Find-Iscc)) { Winget-Install "JRSoftware.InnoSetup" | Out-Null }
        if (Find-Iscc) { $buildArgs.Installer = $true } else { Write-Warning "Inno Setup not found; building the portable app only." }
    }

    # 5. Build
    & "$PSScriptRoot\build.ps1" @buildArgs
}
catch { $failure = $_.Exception.Message }

# Summary: say plainly what was produced.
$exe       = Join-Path $root "artifacts\publish\WallpaperFlowPro.exe"
$portable  = Join-Path $root "artifacts\WallpaperFlowPro-Portable-$Version.zip"
$installer = Join-Path $root "artifacts\installer\WallpaperFlowPro-Setup-$Version.exe"
Write-Host ""
Write-Host "==================== RESULT ====================" -ForegroundColor Cyan
foreach ($item in @(@("App (run in place)", $exe), @("Portable zip (share this)", $portable), @("Installer (share this)", $installer))) {
    if (Test-Path $item[1]) { Write-Host ("  OK       {0}: {1}" -f $item[0], $item[1]) -ForegroundColor Green }
    else { Write-Host ("  MISSING  {0}" -f $item[0]) -ForegroundColor Yellow }
}
if ($failure) {
    Write-Host ""
    Write-Host "  The build stopped with: $failure" -ForegroundColor Red
    Write-Host "  Copy the red error text above this box and send it." -ForegroundColor Red
}
Write-Host "================================================" -ForegroundColor Cyan
if ($failure) { exit 1 }
