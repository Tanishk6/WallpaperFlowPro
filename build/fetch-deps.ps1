<#
.SYNOPSIS
  Downloads the external players/tools WallpaperFlow drives: mpv (video renderer) and FFmpeg/FFprobe
  (metadata, thumbnails, preview clips, smart video cache). Places mpv.exe, ffmpeg.exe, ffprobe.exe in .\tools.
.NOTES
  Licensing: the default FFmpeg build is GPL (includes libx264). Use -Lgpl for an LGPL build; WallpaperFlow then
  falls back to hardware encoders (NVENC/QSV/AMF) or Media Foundation for the video cache and to MPEG-4 for previews.
  mpv's official Windows builds are GPLv2+. See THIRD-PARTY-NOTICES.md before redistributing.
#>
param(
    [switch]$Lgpl,
    [string]$MpvUrl,
    [string]$MpvSha256,
    [string]$FfmpegUrl,
    [string]$FfmpegSha256
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root "tools"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("wf-deps-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $tools, $tmp | Out-Null

function Get-Verified([string]$url, [string]$file, [string]$sha) {
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $file -UseBasicParsing
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($sha -and $actual -ne $sha.ToUpperInvariant()) { throw "SHA-256 mismatch for $url`n expected $sha`n actual   $actual" }
    Write-Host "  sha256 $actual"
    return $actual
}

function Expand-Any([string]$archive, [string]$dest) {
    New-Item -ItemType Directory -Force $dest | Out-Null
    if ($archive.EndsWith(".zip")) { Expand-Archive $archive $dest -Force; return }
    $sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") + (Get-Command 7z, 7za -ErrorAction SilentlyContinue | ForEach-Object Source) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($sevenZip) { & $sevenZip x $archive "-o$dest" -y | Out-Null; return }
    tar -xf $archive -C $dest   # Windows' bsdtar reads 7z on recent builds
    if ($LASTEXITCODE -ne 0) { throw "Install 7-Zip to extract $archive" }
}

$versions = @()

# ── mpv (shinchiro builds, x86_64 baseline for widest CPU support) ──
if (-not $MpvUrl) {
    $headers = @{ "User-Agent" = "WallpaperFlow-build" }
    if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $env:GITHUB_TOKEN" }   # avoids API rate limits on CI
    $rel = Invoke-RestMethod "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest" -Headers $headers
    $asset = $rel.assets | Where-Object { $_.name -match '^mpv-x86_64-\d{8}-git-[0-9a-f]+\.7z$' } | Select-Object -First 1
    if (-not $asset) { throw "Could not find an mpv build in the latest shinchiro release" }
    $MpvUrl = $asset.browser_download_url
    if (-not $MpvSha256 -and $asset.digest -like "sha256:*") { $MpvSha256 = $asset.digest.Substring(7) }
}
$mpvArchive = Join-Path $tmp "mpv.7z"
$h = Get-Verified $MpvUrl $mpvArchive $MpvSha256
Expand-Any $mpvArchive (Join-Path $tmp "mpv")
Copy-Item (Get-ChildItem (Join-Path $tmp "mpv") -Recurse -Filter mpv.exe | Select-Object -First 1).FullName $tools -Force
$versions += "mpv     $MpvUrl  sha256=$h"

# ── FFmpeg (BtbN static builds) ──
if (-not $FfmpegUrl) {
    $flavor = if ($Lgpl) { "lgpl" } else { "gpl" }
    $FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-$flavor.zip"
}
$ffArchive = Join-Path $tmp "ffmpeg.zip"
$h = Get-Verified $FfmpegUrl $ffArchive $FfmpegSha256
Expand-Any $ffArchive (Join-Path $tmp "ffmpeg")
foreach ($exe in "ffmpeg.exe", "ffprobe.exe") {
    Copy-Item (Get-ChildItem (Join-Path $tmp "ffmpeg") -Recurse -Filter $exe | Select-Object -First 1).FullName $tools -Force
}
$versions += "ffmpeg  $FfmpegUrl  sha256=$h"

$versions | Set-Content (Join-Path $tools "VERSIONS.txt") -Encoding UTF8
Remove-Item $tmp -Recurse -Force
& (Join-Path $tools "mpv.exe") --version | Select-Object -First 1
& (Join-Path $tools "ffmpeg.exe") -hide_banner -version | Select-Object -First 1
Write-Host "Tools ready in $tools (pin these URLs and hashes for reproducible release builds: tools\VERSIONS.txt)" -ForegroundColor Green
