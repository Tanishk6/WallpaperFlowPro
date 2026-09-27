<#
.SYNOPSIS
  Builds WallpaperFlow Pro: restore, test, publish (self-contained win-x64), optionally sign and package the installer.
.EXAMPLE
  .\build\build.ps1 -Version 1.2.0 -Installer
  .\build\build.ps1 -Version 1.2.0 -Installer -SignCertThumbprint ABCD... 
#>
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$Installer,
    [switch]$FrameworkDependent,
    [string]$SignCertThumbprint
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
function Check([string]$step) { if ($LASTEXITCODE -ne 0) { throw "$step failed (exit code $LASTEXITCODE). See the first error above." } }
Push-Location $root
try {
    if (-not (Test-Path "tools\mpv.exe") -or -not (Test-Path "tools\ffmpeg.exe")) {
        Write-Host "Fetching mpv and FFmpeg..." -ForegroundColor Cyan
        & "$PSScriptRoot\fetch-deps.ps1"
    }

    dotnet --version | Out-Null
    dotnet restore WallpaperFlow.sln; Check "Restore"
    dotnet build WallpaperFlow.sln -c $Configuration --no-restore -p:Version=$Version; Check "Build"

    if (-not $SkipTests) {
        dotnet test tests\WallpaperFlow.Core.Tests -c $Configuration --nologo; Check "Tests"
    }

    $publish = Join-Path $root "artifacts\publish"
    # A running copy locks its files (and its mpv/ffmpeg helpers), which would make the clean-up below fail.
    Get-Process WallpaperFlowPro, mpv, ffmpeg -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Write-Host "Closing running $($_.ProcessName) (pid $($_.Id))"; Stop-Process -Id $_.Id -Force }
    Start-Sleep -Milliseconds 500
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    dotnet publish src\WallpaperFlow.App\WallpaperFlow.App.csproj -c $Configuration -r win-x64 `
        --self-contained $selfContained -p:Version=$Version -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false `
        -o $publish
    Check "Publish"

    # One-file portable copy: unzip anywhere and run WallpaperFlowPro.exe.
    $portable = Join-Path $root "artifacts\WallpaperFlowPro-Portable-$Version.zip"
    if (Test-Path $portable) { Remove-Item $portable -Force }
    Write-Host "Creating portable zip..."
    Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $portable -CompressionLevel Optimal

    if ($SignCertThumbprint) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)" }
        Get-ChildItem $publish -Include WallpaperFlowPro.exe, WallpaperFlow*.dll -Recurse | ForEach-Object {
            & $signtool.FullName sign /sha1 $SignCertThumbprint /fd sha256 /tr http://timestamp.digicert.com /td sha256 $_.FullName
        }
    }

    if ($Installer) {
        $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $iscc) { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php" }
        # Project page from src\WallpaperFlow.Core\AppInfo.cs (GitHubRepo), shown in Windows' installed-apps list.
        $repo = [regex]::Match((Get-Content "src\WallpaperFlow.Core\AppInfo.cs" -Raw), 'GitHubRepo = "([^"]+)"').Groups[1].Value
        & $iscc "/DAppVersion=$Version" "/DAppURL=https://github.com/$repo" installer\WallpaperFlow.iss; Check "Installer"
        $setup = Join-Path $root "artifacts\installer\WallpaperFlowPro-Setup-$Version.exe"
        if ($SignCertThumbprint) { & $signtool.FullName sign /sha1 $SignCertThumbprint /fd sha256 /tr http://timestamp.digicert.com /td sha256 $setup }
        $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
        # Checksum for the release notes, so buyers can check their download is genuine.
        "$hash  WallpaperFlowPro-Setup-$Version.exe" | Set-Content (Join-Path $root "artifacts\installer\SHA256.txt") -Encoding ASCII
        Write-Host "Installer: $setup`nSHA-256:  $hash" -ForegroundColor Green
    }
    Write-Host "Published to $publish" -ForegroundColor Green
}
finally { Pop-Location }
