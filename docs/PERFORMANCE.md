# Performance

## Targets and what they mean

The goals are under 2% CPU and under 150 MB of RAM at idle, a wallpaper that visibly steps aside for games, and
nothing that makes a laptop run warm on battery. What "idle" costs depends almost entirely on the wallpaper type,
so here is what each piece of the system costs.

These are design estimates for a typical modern PC: a recent 6–8 core CPU and a GPU with hardware
H.264/HEVC/AV1 decode. They have **not been measured yet**. Verify them on Windows with the Performance page
(which shows only WallpaperFlow and its renderers) and Task Manager before publishing numbers.

| State | Expected CPU | Expected memory | Notes |
|---|---|---|---|
| Tray only, window closed, nothing playing | ~0% | ~40–70 MB | Event-driven; timers every 2–5 s. Working set trimmed after the window closes. |
| Window open | 0–2% | +60–120 MB | WPF and the thumbnail cache. Released again when the window closes. |
| 1080p30 H.264 video, hardware decode | ~0.5–2% | mpv ~80–150 MB | Decode on the GPU's video engine; presentation via D3D11 flip model. |
| 4K60 HEVC video on a 1080p screen | 2–6% | mpv ~150–300 MB | Decoding 4K60 keeps the GPU's video-decode engine busy (Task Manager may show 20–40% for that engine); the optional optimiser brings it close to the row above. |
| Video, paused (soft) | ~0% | unchanged | Resumes instantly. |
| Video, stopped (gaming escalation) | 0% | freed | Restarts in about a second when the game closes. |
| Web / shader wallpaper | page dependent | ~150–350 MB total | Chromium runs several processes. This is the one category that cannot meet a 150 MB budget. |
| Web wallpaper, deep pause | ~0% | reduced | `TrySuspendAsync` plus a low memory target. |
| Clock wallpaper | < 0.5% | ~150 MB | Updates once a second. |

**What this means for the targets:** the under-2% CPU goal is met by video wallpapers with hardware decoding and
by everything paused. The under-150 MB goal is met by the app itself, and by the app plus most video wallpapers.
Web-based wallpapers carry Chromium's baseline cost, and the UI says so.

## Where the savings come from

- **Nothing polls when nothing changes.** Window changes come from WinEvent hooks, power and session changes from notifications, and audio capture and input hooks exist only while a wallpaper needs them.
- **Hidden means paused.** Monitors covered by maximised windows, the display being off, the session locked and fullscreen apps all pause the affected slots. Deep pause also hides the WebView2 controller and suspends its timers.
- **Games get the GPU.** A game pauses every wallpaper. After five minutes (configurable) the renderers stop entirely, which frees VRAM and the decoder.
- **Frame-rate discipline.** Videos always play at their own frame rate (no re-timing, which looks choppy). Web and shader wallpapers are capped at 60 fps by default, 30 while you work in other windows and 15 on battery, by gating `requestAnimationFrame`.
- **Adaptive rendering.** When *other* processes push the GPU past 85% (or the CPU past 90%), the app switches to Performance quality, drops heavy effects and renders web content at 75% resolution. Its own renderers are excluded from that measurement, so a wallpaper never throttles itself.
- **The optional video optimiser (off by default).** A video much larger than the screen (4K on 1080p) can be re-encoded once to screen size at the same frame rate and high quality, trading a little quality for less decode work.
- **A disposable UI.** The main window is destroyed on close, then the app runs a compacting GC and `EmptyWorkingSet`.
- **Efficiency mode.** Paused mpv renderers are switched to Windows 11 Efficiency mode (EcoQoS) and below-normal priority.

## Measuring

1. Open **Performance** and let it sample for a minute. The page switches the sampler to 1 s intervals only while it's open.
2. The CPU, GPU, RAM and VRAM figures cover this process plus the mpv and WebView2 processes. GPU numbers come from the same "GPU Engine" counters Task Manager uses.
3. For a clean idle number, close the window, wait 30 s, then check Task Manager's *Details* tab with the "Memory (private working set)" column.
4. To test policies, start a game, start OBS, maximise a window, unplug the charger, and watch the status line change.

Temperature comes from ACPI thermal zones. Many desktop boards don't expose them, and the page then says "Not
reported". GPU-vendor sensors would need vendor SDKs and are intentionally not bundled.
