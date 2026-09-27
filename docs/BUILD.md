# Build and release

## Requirements

- Windows 10 1809 or later, or Windows 11, x64.
- .NET 8 SDK (8.0.100 or later; `global.json` rolls forward within 8.0).
- Visual Studio 2022 17.8+ with the ".NET desktop development" workload, or just the SDK and VS Code / Rider.
- PowerShell 5.1+ and 7-Zip (to extract mpv's `.7z`; recent Windows `tar` may also work).
- Inno Setup 6.2+ for the installer.
- Optionally, a code-signing certificate (removes Windows SmartScreen warnings for your users).

## Steps

```powershell
git clone <repo> WallpaperFlowPro; cd WallpaperFlowPro
.\build\fetch-deps.ps1                 # tools\mpv.exe, tools\ffmpeg.exe, tools\ffprobe.exe (+ VERSIONS.txt)
dotnet build WallpaperFlow.sln -c Debug
dotnet test tests\WallpaperFlow.Core.Tests
dotnet run --project src\WallpaperFlow.App
```

The App project copies `runtime\**` and `tools\**` next to the executable, so the debug build can play videos
straight away. If the tools are missing, the app still runs: Settings → Tools shows what's absent, and video
wallpapers report a clear error.

## Release

```powershell
.\build\build.ps1 -Version 1.2.0 -Installer -SignCertThumbprint <thumbprint>
```

This does the following:

1. It restores, builds and runs the tests.
2. It publishes a self-contained win-x64 build with ReadyToRun to `artifacts\publish`. Add `-FrameworkDependent` for a much smaller build that needs the .NET 8 Desktop Runtime installed.
3. If a certificate is given, it signs the executable and the WallpaperFlow DLLs, then the installer.
4. It compiles `installer\WallpaperFlow.iss` into `artifacts\installer\WallpaperFlowPro-Setup-<version>.exe`, and writes `SHA256.txt` with the installer's checksum for your release notes.

The installer is per-user (no admin rights) and installs to `%LocalAppData%\Programs\WallpaperFlow Pro`. It:

- Registers autostart under `HKCU\...\Run`, if chosen.
- Installs the WebView2 Runtime if it's missing.
- Copies itself to `%LocalAppData%\WallpaperFlow\installers` so a later version can roll back to it.
- On uninstall, asks whether to delete the library and settings.

## Publishing a new version

1. Change the version when you build: `.\build\build.ps1 -Version 1.1.0 -Installer`.
2. Upload `artifacts\installer\WallpaperFlowPro-Setup-1.1.0.exe` wherever you distribute it (your store page, for example), and put the checksum from `artifacts\installer\SHA256.txt` in the release notes.
3. On GitHub, create a release with a matching tag (`v1.1.0`) so the source for that exact version is easy to find, as GPL-3.0 expects.
4. Edit `update.json` in the repository root: set `version`, `notes`, and `page` (the link where people get the new version). Commit it to `main`.

Installed copies check `update.json` once a day, using the repository set in `src/WallpaperFlow.Core/AppInfo.cs`. When a newer version appears, the app says so, and **Install** opens your `page` link.

If you later host the installer at a direct link, also fill in `url` and `sha256`. The app can then download and verify it by itself, and install it on exit. By default, unsigned installers are refused for automatic installs (`Updates.RequireSignature`).

## Continuous integration

A Windows runner (for example `windows-latest` on GitHub Actions) needs:
`actions/setup-dotnet@v4` (8.0.x), `.\build\fetch-deps.ps1 -Lgpl`, `dotnet test`, and then `.\build\build.ps1 -Installer`.
Cache `tools\` keyed on the URLs in `tools\VERSIONS.txt`, and pin those URLs and hashes for reproducible releases.
