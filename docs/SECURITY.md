# Security

WallpaperFlow runs content from strangers: downloaded videos, web wallpapers, Wallpaper Engine projects and
catalog items. It does this inside a process that starts with Windows. The design goal is that a malicious
wallpaper can at worst look bad or use some GPU. It must not run code on the machine, read local files or reach
the network without consent.

## Imports

- **Content, not extensions.** Every media file is identified by magic bytes. An `.mp4` that is really a PE executable, or anything unrecognised, is rejected.
- **No native code in packages.** `.exe`, `.dll`, scripts, shortcuts, installers, `.reg`, `.cpl` and similar files are skipped when a package is extracted or copied.
- **Safe extraction.** Every zip entry path is resolved and must stay inside the destination (zip-slip). Totals, entry counts and compression ratios are capped (zip bombs), and each entry's written size is checked against its header.
- **No links.** Symlinks and junctions are not followed or imported.
- **Wallpaper Engine "application" and "scene" projects** are listed but never run.

## Web wallpapers (WebView2)

- **Isolation.** Each package is served from its own virtual origin with `DenyCors`. Pages can't read other wallpapers or local files outside their folder.
- **Offline by default.** Requests to any other host get a 403. Sockets, SSE, WebRTC and WebTransport are disabled in the page as well. Online access is granted per wallpaper, or globally, by the user.
- **Nothing escapes the page.** Navigation away, new windows, downloads, permission prompts (camera, microphone, location, notifications), external protocol launches, host objects, dev tools, context menus, autofill and password saving are all blocked or off.
- **Limited messaging.** Messages from the page are limited to 4 KB and three types (`fps`, `ready`, `error`), and are parsed defensively.
- **Process-level sandboxing.** WebView2 renderer processes run in Chromium's sandbox.

## Video and tools

mpv and ffmpeg run as separate child processes started with explicit argument lists; there is no shell and no
string concatenation into command lines. mpv runs with `--no-config` and
`--load-scripts=no`, and never loads Lua scripts or a user config. Its JSON IPC pipe name is random per instance.

## Input

The low-level mouse hook exists only while an interactive wallpaper plays. It never blocks or modifies input.
The keyboard hook is off by default, and when on it forwards keys only while the desktop itself has focus, never
while you type in other applications.

## Updates and downloads

Only HTTPS is accepted. Every automatic update download is checked against the SHA-256 in its manifest. Update
installers must also carry a valid Authenticode signature from the expected publisher, both before an update and
before a rollback. Downloads are size-capped.

## Secrets and privacy

The optional Anthropic API key is encrypted with DPAPI for the current Windows user. Cloud tagging is off by
default; when on, it sends only the 480 px thumbnail and the title. Nothing else leaves the machine. There is no
telemetry. Crash reports are written locally to `Crash\` and are never uploaded.

## Resilience

- A wallpaper that crashes four times in two minutes is stopped and reported.
- Two consecutive unclean exits start the app in safe mode, without restoring wallpapers.
- An exception on the UI thread closes the window but leaves the engine running.
- Restores are staged and validated before they replace the database.

## Reporting

Please report vulnerabilities privately through the repository's Security tab (Report a vulnerability). Please don't open a public issue for them.
