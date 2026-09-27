# Wallpaper API

## Packages

A wallpaper package is a folder, or a `.zip` of one, containing `wallpaper.json`:

```json
{
  "title": "Spectrum rings",
  "type": "html",
  "entry": "index.html",
  "author": "You",
  "description": "Reacts to whatever is playing.",
  "category": "Abstract",
  "tags": ["music", "visualizer"],
  "audioReactive": true,
  "interactive": false,
  "allowNetwork": false,
  "properties": {
    "color": { "type": "color", "value": "0.478 0.549 1", "text": "Colour" },
    "speed": { "type": "slider", "value": 1.0, "min": 0.1, "max": 3, "text": "Speed" }
  }
}
```

`type` is `html`, `video`, `gif`, `apng`, `shader`, `clock` or `slideshow`. A folder with just an `index.html` also
imports, using defaults. Executables and other native code in packages (`.exe`, `.dll`, scripts such as `.bat` or
`.ps1`, `.lnk` and so on) are skipped during import. `allowNetwork` is only a request: the page stays offline until
the user allows it in the wallpaper's details.

## JavaScript runtime

Every web wallpaper gets `runtime/web/wallpaperflow-runtime.js` before its own scripts run.

**Wallpaper Engine compatibility.** Existing web wallpapers generally run unchanged:

- `window.wallpaperRegisterAudioListener(cb)`: `cb` receives 128 floats, 64 left-channel bands and then 64 right-channel bands, each 0..1, at 30 Hz.
- `window.wallpaperPropertyListener = { applyUserProperties(props), applyGeneralProperties(p), setPaused(bool) }`: properties arrive in the same `{ key: { value } }` shape.
- The media-integration registration functions exist as no-ops.

**The WallpaperFlow API:**

```js
wallpaperflow.config        // { fps, renderScale, effects, audioStyle, audioReactive, interactive, network, properties, wallpaper }
wallpaperflow.properties    // shortcut to config.properties
wallpaperflow.paused        // boolean
wallpaperflow.audio         // { level, left: number[], right: number[] }
wallpaperflow.on('audio',   ({ level, left, right }) => {})
wallpaperflow.on('pointer', ({ kind, x, y, button, delta }) => {})   // kind: move | down | up | wheel | leave, CSS pixels
wallpaperflow.on('key',     ({ kind, code, key }) => {})
wallpaperflow.on('config' | 'pause' | 'resume', handler)
wallpaperflow.post({ type: 'error', message })                        // ≤ 4 KB, shows up in logs
```

**Input.** Because the wallpaper sits behind the icons, the host forwards the pointer. The runtime dispatches real
`pointermove`/`mousemove`, `pointerdown`/`mousedown`, `pointerup`/`mouseup`/`click`, `contextmenu` and `wheel` events on
the element under the pointer. Ordinary DOM code works as-is.

**Frame rate and pausing.** `requestAnimationFrame` is gated by the FPS cap and stops entirely while paused. While
paused, videos and CSS animations are paused too. Use `requestAnimationFrame` for animation. `setInterval`
animations are only stopped by the deep-pause suspend.

**The network.** Unless allowed, requests to other origins get a 403. `WebSocket`, `EventSource`,
`RTCPeerConnection` and `WebTransport` throw, and `sendBeacon` returns false.

**Effects.** User-chosen effects are drawn on an overlay canvas (snow, rain, particles, fire, fog, lightning) and
through CSS filters on `<body>` (blur, colour grade, bloom and glow approximations). Pages don't need to do anything.

## Shaders

Shader wallpapers use Shadertoy conventions. Define `void mainImage(out vec4 fragColor, in vec2 fragCoord)`. The
following uniforms are available:

- `iResolution` (vec3), `iTime`, `iTimeDelta`, `iFrame` (int).
- `iMouse` (vec4: xy is the pointer, zw is the click position, negative when released).
- `iDate`.
- `iChannel0`: a 64×2 audio texture. Row 0 (y ≈ 0.25) holds spectrum bands low to high; row 1 (y ≈ 0.75) holds the overall level.

Render resolution follows the performance render scale.

## Video effect shaders

`runtime/effects/mpv/*.glsl` are libplacebo user shaders (they need mpv 0.37+ with `--vo=gpu-next`). Each
declares its parameters as `//!PARAM` with `//!TYPE DYNAMIC float`, so values change without recompiling. Time
is derived from the built-in `frame` counter.

To add an effect:

1. Add the `.glsl` file.
2. Add the kind to `EffectKind`.
3. Map it in `EffectsCompiler.Order` and `Params`.

The consistency test fails if a parameter name drifts.
