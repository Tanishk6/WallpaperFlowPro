# Architecture

## Processes and threads

WallpaperFlow is one .NET 8 process, `WallpaperFlowPro.exe`, plus the renderers it starts.

| Process | Owns | Lifetime |
|---|---|---|
| WallpaperFlowPro.exe | Tray icon, engine thread, policy engine, database, optional WPF window | Always; starts with Windows (`--silent`) |
| mpv.exe (one per video slot) | Hardware video decode, D3D11 presentation, GLSL effects | While that slot plays, pauses or is soft-stopped |
| msedgewebview2.exe (shared browser, GPU and renderer processes) | HTML, URL, shader and clock wallpapers | While any web slot exists |
| ffmpeg.exe / ffprobe.exe | Probing, thumbnails, previews, video-cache transcodes | Short-lived jobs |

Inside the main process there are three kinds of thread:

- **The WPF UI thread.** It exists only while the window is open. `ShutdownMode=OnExplicitShutdown` keeps the app alive when the window closes. The window is destroyed, not hidden, and the working set is then trimmed.
- **The engine thread (`EngineThread`).** A dedicated STA thread with its own Win32 message loop and a `SynchronizationContext`. It owns every HWND the engine creates (the wallpaper host windows, the hidden `WallpaperFlow.Engine` message window and the tray icon), plus the WebView2 controllers, WinEvent hooks and low-level input hooks. Every renderer call is marshalled here through `InvokeAsync` or `Post`. This keeps COM apartment rules satisfied and means wallpapers never depend on the UI thread.
- **The thread pool.** Metrics sampling, process scanning, audio capture callbacks, imports, transcodes and HTTP run here.

Because the UI is a separate, disposable layer over `AppServices`, closing the window can never interrupt playback.
The view models are WPF-free and are compiled and checked separately from the XAML.

## Desktop embedding (`Desktop/DesktopHost.cs`)

A wallpaper has to sit behind the desktop icons but in front of Windows' own wallpaper.

1. Send `0x052C` to `Progman`. This asks Explorer to create the `WorkerW` layer used for wallpaper animations.
2. **Windows 11 24H2 and later (the "raised desktop"):** `SHELLDLL_DefView` is a child of `Progman`. The host windows are parented to `Progman` below `DefView` and above the `WorkerW`. They are layered windows with alpha 255 so DWM composes them correctly.
3. **Earlier builds:** the icons live in a top-level `WorkerW` that contains `DefView`. The host is parented to the *next* `WorkerW` sibling, which sits behind the icons.
4. `TaskbarCreated` means Explorer restarted. `RecreateAllAsync` rebuilds every host after Explorer settles.
5. On exit, or when no hosts remain, the static wallpaper is re-applied with `SystemParametersInfo(SPI_SETDESKWALLPAPER)`. Otherwise `WorkerW` can keep painting the last frame.

Positions use physical pixels under per-monitor-v2 DPI awareness. Monitor coordinates are converted into the
parent's client space, because `Progman`/`WorkerW` span the virtual screen, which can have a negative origin.

## Rendering

**Video, GIF, APNG and slideshow are handled by `MpvRenderer`.** It starts `mpv.exe --wid=<host HWND>` with:

- `--vo=gpu-next --gpu-api=d3d11 --d3d11-flip=yes` for rendering.
- `--hwdec=auto-safe` (or D3D11VA/NVDEC/off) for decoding. It switches to `auto-copy-safe` when the motion-blur filter needs frames on the CPU.
- `--loop-file=inf` and `--input-ipc-server=\\.\pipe\wallpaperflow-mpv-<id>` for looping and control.

Everything after launch goes through the JSON IPC pipe (`MpvIpcClient`): pause, volume, speed and
effects. Videos always play at their own frame rate.

Effects are libplacebo user shaders (`runtime/effects/mpv/*.glsl`) whose values are `//!PARAM`s. These are set
live with `glsl-shader-opts`, so moving a slider doesn't recompile or restart anything. The test
`Every_shader_parameter_exists_in_its_glsl_file` keeps the C# compiler and the shader files in sync. Motion blur
is an ffmpeg `tmix` filter labelled `@wfmblur`.

**HTML, URL, shader and clock wallpapers are handled by `WebRenderer`.** It uses one shared
`CoreWebView2Environment` and one controller per slot, created directly on the host HWND.

- **Local packages** are served from a virtual host, `w<id>.wallpaperflow.example`, with `DenyCors`.
- **Requests** to any other host get a 403 unless the user allowed network access for that wallpaper.
- **Blocked:** navigation away, pop-ups, downloads, permission prompts and external protocols.
- **Runtime script:** `runtime/web/wallpaperflow-runtime.js` is injected before page scripts. It gates `requestAnimationFrame` for the FPS cap and pausing, and reports FPS. It also provides the Wallpaper Engine APIs, synthesises forwarded input, draws overlay effects and adds a network kill-switch.
- **Deep pause** hides the controller, calls `TrySuspendAsync` (timers freeze) and lowers the memory target.

**Audio.** `AudioService` captures WASAPI loopback (or a microphone) only while an audio-reactive wallpaper plays.
It runs a 1024-point FFT and produces 64 log-spaced bands with AGC at 30 Hz. Web wallpapers receive the bands.
Video wallpapers get pulse (`video-zoom`) and glow (brightness/saturation).

**Input.** The desktop icons window receives all input, so `InputRouter` installs `WH_MOUSE_LL` (and optionally
`WH_KEYBOARD_LL`) only while an interactive wallpaper plays. The hook never blocks input, does almost nothing
inside the callback, and forwards keys only while the desktop itself has focus.

## The policy engine

`PlaybackGovernor.Decide(inputs, settings)` is a pure function, which makes it trivially testable. It returns a
`PolicyDecision` containing a per-monitor `SlotPolicy` (Play, Pause, soft or deep, or Stop), an FPS cap, a forced
quality level, whether heavy effects are allowed, and a render scale.

Rules are applied from most to least severe and combined with `SlotPolicy.Max`, so a weaker rule can never undo a
stronger one:

1. Sleep, display off, session locked.
2. User pause.
3. Process rules (OBS, Streamlabs, XSplit, Bandicam, Camtasia, …).
4. A fullscreen game, on all monitors because the GPU is shared. It escalates from Pause to Stop after N minutes, because a paused decoder still holds VRAM.
5. Presentations.
6. Fullscreen video, on that monitor only.
7. Other fullscreen apps, and monitors covered by maximised windows.
8. Battery: pause when unplugged, pause below N%, or cap FPS and drop heavy effects. Energy saver.
9. Adaptive rendering when *other* processes load the GPU or CPU past the thresholds.
10. Dynamic FPS while another app has focus.

`PolicyEngine` re-evaluates when any input changes (WinEvents, process scans, power events, metrics) and on a 2 s
tick. Restrictions apply immediately. Relaxations wait 600 ms, so alt-tabbing out of a game doesn't briefly
spin wallpapers back up.

`DetectionService` classifies the foreground fullscreen window:

- **Game:** `SHQueryUserNotificationState` reports D3D fullscreen, the window class belongs to a game engine, or the executable lives under a Steam, Epic, GOG, Xbox or Riot library.
- **Video:** a known player or browser.
- **Presentation:** PowerPoint, or the shell's presentation mode.
- **Other:** anything else.

Covered monitors are found by walking the z-order until it reaches the desktop.

## Wallpaper lifecycle (`WallpaperManager`)

`ReconcileAsync` is idempotent. It computes the slots it wants from the layout mode and the connected monitors,
then destroys, moves, replaces or creates slots to match. The layout modes are:

- **Same on every display:** one slot per monitor with the shared wallpaper.
- **Different per display:** one slot per monitor with that monitor's assignment.
- **Span:** one slot covering the virtual screen.

One slot is the audio owner (the primary monitor is preferred), so there is never more than one audible wallpaper.

When a renderer fails, it restarts with exponential backoff. After four failures in two minutes the slot is marked
Faulted and the user is notified. `CrashGuard` treats two consecutive unclean exits of the whole app as a crash
loop and starts in safe mode, which does not restore wallpapers.

## Data and services

- **SQLite** (WAL, FTS5) for the library; see [DATABASE.md](DATABASE.md). `settings.json` is written atomically after a 500 ms debounce.
- **Imports** are validated by content sniffing. Packages are extracted with zip-slip and zip-bomb guards, and native-code files are skipped.
- **Thumbnails** (480 px JPEG) and hover previews (4 s, 360 px H.264) are made by ffmpeg in the background after import.
- **Smart video cache:** a video much larger than the display, or faster than the FPS cap, is transcoded once in the background. NVENC is tried first, then QSV, AMF, x264 and Media Foundation. The result goes to an LRU-bounded cache, and the slot switches over when it's ready.
- **AI:** local colour, hue, brightness and sharpness statistics, a dHash, a keyword dictionary and usage-weighted recommendations. Optional Claude vision tagging sends only the thumbnail, with the user's own key stored under DPAPI.
- **Updates:** an HTTPS manifest, a SHA-256 check and an Authenticode publisher check, then the Inno installer runs silently. Installers are retained under `%LocalAppData%\WallpaperFlow\installers` for rollback.

## Folder layout at runtime

```
%LocalAppData%\WallpaperFlow\
  Data\library.db, settings.json, crash-count
  Library\<id>\...          copied media and packages
  Thumbnails\, Previews\, OptimizedCache\
  WebView2\                 shared WebView2 user data
  Themes\, Backups\, Updates\, installers\
  Logs\, Crash\, Temp\
```
