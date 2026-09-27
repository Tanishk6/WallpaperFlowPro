# Third-party notices

WallpaperFlow Pro uses the following components. Full license texts ship in the `licenses` folder of release builds.

| Component | Use | License |
|---|---|---|
| .NET 8 / WPF | Runtime and UI framework | MIT |
| Microsoft.Data.Sqlite / SQLite | Library database | MIT / Public domain |
| Microsoft Edge WebView2 (SDK and Runtime) | HTML, URL, shader and clock wallpapers | BSD-style SDK license; runtime under Microsoft Edge terms |
| NAudio (NAudio.Wasapi) | System-audio and microphone capture | MIT |
| mpv (separate executable, `tools\mpv.exe`) | Video, GIF, APNG and slideshow playback, GLSL effects | GPLv2+ (official Windows builds); LGPLv2.1+ when built with `-Dgpl=false` |
| libplacebo (inside mpv) | GPU rendering and user shaders | LGPLv2.1+ |
| FFmpeg (separate executables, `tools\ffmpeg.exe`, `tools\ffprobe.exe`) | Metadata, thumbnails, previews, video cache | GPLv3+ for the default build (includes x264); LGPLv2.1+ for `-Lgpl` builds |
| Segoe Fluent Icons / Segoe UI Variable | UI fonts supplied by Windows | Windows font license (not redistributed) |

mpv and FFmpeg run as separate processes and are invoked through their command-line and IPC interfaces. If you
redistribute the GPL builds you must also provide (or offer) their corresponding source code; the exact download
URLs and SHA-256 hashes used for a release are recorded in `tools\VERSIONS.txt`. For a commercial release, use
LGPL builds of both (build\fetch-deps.ps1 -Lgpl for FFmpeg; an mpv build configured with `-Dgpl=false`) and have
your licensing reviewed.
