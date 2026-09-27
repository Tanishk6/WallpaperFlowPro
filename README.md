# WallpaperFlow Pro

<img src="src/WallpaperFlow.App/Assets/tnsk-logo.png" width="96" align="right" alt="Tnsk.exe logo" />

Made by **[Tnsk.exe](https://github.com/Tanishk6)**.

A live-wallpaper app for Windows 10 and 11 (x64): videos, GIF/APNG, web pages, websites, GLSL shaders,
slideshows and clocks on the desktop, behind your icons, that get out of the way when you need the machine.

WallpaperFlow pauses for games, fullscreen video, presentations and streaming or recording software; plays videos at their own
frame rate with hardware decoding; throttles web wallpapers while you work in other windows, on battery or when the GPU is busy; and drops its UI entirely when you close the window, leaving only a small tray process
and the renderers.

## What's in the box

- **Wallpaper types:** MP4, MKV, WEBM, MOV, AVI, WMV and M4V video (hardware decoded through D3D11 in mpv), GIF and APNG, image folders as slideshows, HTML packages, websites, Shadertoy-style shaders, and live clocks. Interactive and audio-reactive wallpapers are supported, and web pages written for Wallpaper Engine run unchanged.
- **Displays:** the same wallpaper on all monitors, a different one per monitor, or one spanned across all of them. Hot plug is handled, and monitors are identified by hardware ID.
- **Effects:** colour grading, blur, bloom, glow, fog, rain, snow, fire, particles, lightning and motion blur. On video these run as GPU shaders in mpv/libplacebo; on web wallpapers they are an overlay canvas plus CSS. Audio-reactive pulse, glow, particle burst and waves are included.
- **App design:** glass-style cards over an animated background of your choice: Aurora glow, Neon grid (gaming), Sakura night (anime) or your current wallpaper, blurred. It follows the mouse with a subtle parallax and pauses whenever the window isn't in use.
- **Library:** folders, collections, favourites, tags, instant full-text search, sorting, thumbnails and 4-second hover previews. The grid is virtualised, and each wallpaper has a detail panel whose edits apply live.
- **AI assistant:** local auto-tagging, categories, recommendations, duplicate detection (content hash plus perceptual hash) and quality flags. Richer tags are available optionally from Anthropic's API using your own key.
- **Performance engine:** a pure, unit-tested policy function that reacts to games, fullscreen apps, OBS and other recorders, covered monitors, lock, sleep, display off, battery, energy saver and system load. A live dashboard shows CPU, GPU, RAM, VRAM, temperature and FPS.
- **Importers:** files, folders, zip packages, URLs, Wallpaper Engine video and web projects, and GIF and image collections.
- **Themes:** five built-in themes, plus your own created from your current setup and exported or imported as `.wftheme` files, optionally including the wallpapers.
- **Sync and backup:** folder-based sync (OneDrive, Dropbox, a NAS), backups with staged restore.
- **Updates:** automatic or manual updates, verified by SHA-256 and Authenticode publisher, with rollback to the previous installer.
- **Safety:** content-sniffing import validation, zip-slip and zip-bomb protection, sandboxed web wallpapers that are offline by default, crash backoff per wallpaper and safe mode after repeated crashes.

## Download

**[Download WallpaperFlow Pro (free)](https://github.com/Tanishk6/WallpaperFlowPro/releases/latest)**: get `WallpaperFlowPro-Setup.exe` from the latest release. It's for Windows 10 and 11 (64-bit).

Each release lists the installer's SHA-256 checksum, so you can check your download is genuine: in PowerShell, run `Get-FileHash .\WallpaperFlowPro-Setup-1.0.0.exe`.

The installer isn't code-signed yet, so Windows SmartScreen may say "Windows protected your PC". Choose **More info → Run anyway**.

## Build it yourself

WallpaperFlow Pro is open source under GPL-3.0, so you're free to build it yourself. On a Windows PC, double-click `Build-Exe.cmd`. It installs what's missing (the .NET 8 SDK, 7-Zip and Inno Setup, via winget), downloads mpv and FFmpeg, and produces the installer in `artifacts\installer`. See [docs/BUILD.md](docs/BUILD.md) for details.

## Quick start (development)

```powershell
# Windows 10 1809+ / 11, .NET 8 SDK, PowerShell 5+
.\build\fetch-deps.ps1          # mpv + FFmpeg into .\tools (add -Lgpl for an LGPL FFmpeg)
dotnet build WallpaperFlow.sln
dotnet test tests\WallpaperFlow.Core.Tests
dotnet run --project src\WallpaperFlow.App
```

Release build and installer: `.\build\build.ps1 -Version 1.0.0 -Installer` (needs Inno Setup 6). See [docs/BUILD.md](docs/BUILD.md).

## Repository layout

```
src/WallpaperFlow.Core     engine, rendering, detection, data, media, AI, import, themes, cloud, updates (no WPF)
src/WallpaperFlow.App      WPF shell: tray-first lifetime, Mica UI, view models (WPF-free) and XAML views
runtime/                   web runtime script, clock + shader templates, mpv GLSL effects, sample wallpapers
tests/                     xunit tests for the pure logic (governor, scheduler, parsers, analysis, effects)
installer/                 Inno Setup script (per-user, WebView2 bootstrap, rollback copy)
build/                     build.ps1, fetch-deps.ps1
docs/                      architecture, build, database, wallpaper API, performance, security
```

## Documentation

- [Architecture](docs/ARCHITECTURE.md): processes, threads, desktop embedding, the renderers and the policy engine.
- [Performance](docs/PERFORMANCE.md): what costs what, the targets, and how to measure them.
- [Wallpaper API](docs/WALLPAPER_API.md): the `wallpaper.json` format, the JavaScript runtime and shaders.
- [Database](docs/DATABASE.md): the SQLite schema and search.
- [Security](docs/SECURITY.md): the threat model and mitigations.
- [Build and release](docs/BUILD.md): toolchain, signing, the installer and update manifests.

## Status

All of the source is written. The Core library and the WPF-free view models compile with no errors. The 44 unit
tests pass: the governor, scheduler, image analysis, validators, parsers, FFT, duplicates, the video-cache rules
and the effect/shader consistency checks.

The XAML UI, the desktop-embedding paths (including the Windows 11 24H2 "raised desktop"), mpv's libplacebo
shader parameters (mpv ≥ 0.37 with `--vo=gpu-next`) and WebView2 behaviour must be verified on Windows before
release. The performance numbers in the docs are design targets until they have been measured there.

## License

Copyright © 2026 Tnsk.exe. WallpaperFlow Pro is free software: you can redistribute it and/or modify it under the terms of the
[GNU General Public License v3.0](LICENSE). It comes with no warranty. Bundled tools (mpv, FFmpeg) keep their own
licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Bugs and ideas: [open an issue](../../issues). Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).
