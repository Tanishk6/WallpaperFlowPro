/* WallpaperFlow web runtime — injected into every HTML/URL/shader/clock wallpaper before page scripts run.
 * Provides: FPS cap + pause (rAF gate, media, CSS animations), fps reporting, Wallpaper Engine shims
 * (wallpaperRegisterAudioListener, wallpaperPropertyListener), synthetic pointer/keyboard input,
 * overlay effects (snow, rain, particles, fire, fog, lightning), CSS effects and audio-reactive styles,
 * and a network kill-switch when the wallpaper isn't allowed online. See docs/WALLPAPER_API.md. */
(() => {
  'use strict';
  if (window.wallpaperflow) return;
  const boot = window.__wallpaperflowBoot || {};
  const host = window.chrome && window.chrome.webview;
  const post = (msg) => { try { host && host.postMessage(msg); } catch (_) {} };
  const listeners = {};
  const emit = (name, data) => (listeners[name] || []).forEach((cb) => { try { cb(data); } catch (e) { report(e); } });
  const report = (e) => post({ type: 'error', message: String((e && (e.stack || e.message)) || e).slice(0, 500) });

  const state = { config: boot, paused: false, fps: boot.fps || 0, frames: 0, lastFrame: 0, audio: { level: 0, left: [], right: [] } };

  // ── Network kill-switch (the host also blocks requests; this stops sockets that bypass the resource filter) ──
  if (!boot.network) {
    const deny = function () { throw new Error('Network access is disabled for this wallpaper'); };
    for (const name of ['WebSocket', 'EventSource', 'RTCPeerConnection', 'webkitRTCPeerConnection', 'WebTransport']) {
      try { Object.defineProperty(window, name, { value: deny, configurable: false, writable: false }); } catch (_) {}
    }
    try { navigator.sendBeacon = () => false; } catch (_) {}
  }

  // ── Frame gate: every requestAnimationFrame goes through here so we can cap and pause. ──
  const nativeRaf = window.requestAnimationFrame.bind(window);
  let queue = new Map(), nextId = 1, scheduled = false;
  function pump(ts) {
    scheduled = false;
    if (state.paused) return;
    const cap = state.fps;
    if (cap > 0 && state.lastFrame && ts - state.lastFrame < 1000 / cap - 2) { schedule(); return; }
    state.lastFrame = ts;
    state.frames++;
    const run = queue; queue = new Map();
    for (const cb of run.values()) { try { cb(ts); } catch (e) { report(e); } }
  }
  function schedule() { if (!scheduled && !state.paused) { scheduled = true; nativeRaf(pump); } }
  window.requestAnimationFrame = (cb) => { const id = nextId++; queue.set(id, cb); schedule(); return id; };
  window.cancelAnimationFrame = (id) => { queue.delete(id); };
  setInterval(() => { post({ type: 'fps', value: state.frames / 2 }); state.frames = 0; }, 2000);

  // ── Pause / resume ──
  const pausedMedia = new Set();
  let pausedAnimations = [];
  const styleEl = document.createElement('style');
  styleEl.textContent = 'html.wf-paused *,html.wf-paused *::before,html.wf-paused *::after{animation-play-state:paused!important;transition:none!important}';
  function pause() {
    if (state.paused) return;
    state.paused = true;
    document.documentElement.classList.add('wf-paused');
    document.querySelectorAll('video,audio').forEach((m) => { if (!m.paused) { pausedMedia.add(m); m.pause(); } });
    try { pausedAnimations = document.getAnimations().filter((a) => a.playState === 'running'); pausedAnimations.forEach((a) => a.pause()); } catch (_) {}
    callWe('setPaused', true);
    emit('pause');
  }
  function resume() {
    if (!state.paused) return;
    state.paused = false;
    state.lastFrame = 0;
    document.documentElement.classList.remove('wf-paused');
    pausedMedia.forEach((m) => m.play().catch(() => {})); pausedMedia.clear();
    pausedAnimations.forEach((a) => { try { a.play(); } catch (_) {} }); pausedAnimations = [];
    callWe('setPaused', false);
    schedule();
    emit('resume');
  }

  // ── Wallpaper Engine compatibility ──
  const audioListeners = [];
  window.wallpaperRegisterAudioListener = (cb) => { if (typeof cb === 'function') audioListeners.push(cb); };
  for (const n of ['wallpaperRegisterMediaStatusListener', 'wallpaperRegisterMediaPropertiesListener', 'wallpaperRegisterMediaThumbnailListener',
                   'wallpaperRegisterMediaPlaybackListener', 'wallpaperRegisterMediaTimelineListener', 'wallpaperRequestRandomFileForProperty']) {
    if (!window[n]) window[n] = () => {};
  }
  function callWe(fn, arg) {
    const l = window.wallpaperPropertyListener;
    if (l && typeof l[fn] === 'function') { try { l[fn](arg); } catch (e) { report(e); } }
  }
  function applyProperties() {
    const props = state.config.properties;
    if (props) callWe('applyUserProperties', props);
    callWe('applyGeneralProperties', { fps: state.fps || 60 });
  }
  function resample(arr, n) {
    if (!arr.length) return new Array(n).fill(0);
    const out = new Array(n);
    for (let i = 0; i < n; i++) out[i] = arr[Math.min(arr.length - 1, Math.floor(i * arr.length / n))];
    return out;
  }

  // ── Synthetic input (the wallpaper sits behind desktop icons, so the host forwards input) ──
  let lastTarget = null;
  function pointer(m) {
    const dpr = window.devicePixelRatio || 1;
    const x = m.x / dpr, y = m.y / dpr;
    if (m.kind === 'leave') {
      if (lastTarget) { lastTarget.dispatchEvent(new PointerEvent('pointerleave', { bubbles: false })); lastTarget.dispatchEvent(new MouseEvent('mouseout', { bubbles: true })); }
      lastTarget = null;
      emit('pointer', { kind: 'leave', x, y });
      return;
    }
    const target = document.elementFromPoint(x, y) || document.body || document.documentElement;
    const init = { bubbles: true, cancelable: true, clientX: x, clientY: y, screenX: x, screenY: y, button: m.button === 2 ? 2 : 0,
                   buttons: m.kind === 'down' ? (m.button === 2 ? 2 : 1) : 0, pointerId: 1, pointerType: 'mouse', isPrimary: true, view: window };
    if (target !== lastTarget) {
      if (lastTarget) lastTarget.dispatchEvent(new MouseEvent('mouseout', init));
      target.dispatchEvent(new MouseEvent('mouseover', init));
      lastTarget = target;
    }
    switch (m.kind) {
      case 'move': target.dispatchEvent(new PointerEvent('pointermove', init)); target.dispatchEvent(new MouseEvent('mousemove', init)); break;
      case 'down': target.dispatchEvent(new PointerEvent('pointerdown', init)); target.dispatchEvent(new MouseEvent('mousedown', init)); break;
      case 'up':
        target.dispatchEvent(new PointerEvent('pointerup', init)); target.dispatchEvent(new MouseEvent('mouseup', init));
        target.dispatchEvent(new MouseEvent(m.button === 2 ? 'contextmenu' : 'click', init)); break;
      case 'wheel': target.dispatchEvent(new WheelEvent('wheel', { ...init, deltaY: -m.delta, deltaMode: 0 })); break;
    }
    emit('pointer', { kind: m.kind, x, y, button: m.button, delta: m.delta });
  }
  function key(m) {
    const target = document.activeElement || document.body;
    const ev = new KeyboardEvent(m.kind === 'down' ? 'keydown' : 'keyup', { key: m.key, keyCode: m.code, which: m.code, bubbles: true, cancelable: true });
    target && target.dispatchEvent(ev);
    emit('key', m);
  }

  // ── Effects: CSS filters on <body>, particle overlay canvas on <html> (outside body so filters don't hit it) ──
  let overlay = null, ctx = null, particles = [], flash = 0, bolt = null, fogT = 0, lastOverlay = 0, audioPulse = 0;
  function applyCss() {
    const fx = state.config.effects || {};
    const parts = [];
    if (fx.blur) parts.push(`blur(${(0.5 + fx.blur.intensity * 7.5).toFixed(1)}px)`);
    if (fx.colorGrade) {
      const g = fx.colorGrade, i = g.intensity;
      parts.push(`brightness(${(1 + (g.brightness || 0) * i).toFixed(3)})`, `contrast(${(1 + ((g.contrast ?? 1.05) - 1) * i).toFixed(3)})`,
                 `saturate(${(1 + ((g.saturation ?? 1.15) - 1) * i).toFixed(3)})`);
      if (g.temperature) parts.push(`sepia(${Math.max(0, g.temperature * i * 0.4).toFixed(3)})`, `hue-rotate(${(g.temperature < 0 ? -12 * i : 0).toFixed(1)}deg)`);
    }
    if (fx.bloom) parts.push(`brightness(${(1 + fx.bloom.intensity * 0.08).toFixed(3)})`, `contrast(${(1 + fx.bloom.intensity * 0.06).toFixed(3)})`);
    if (fx.glow) parts.push(`saturate(${(1 + fx.glow.intensity * 0.25).toFixed(3)})`);
    state.cssFilter = parts.join(' ');
    if (document.body) document.body.style.filter = state.cssFilter;
    const needsOverlay = ['snow', 'rain', 'particles', 'fire', 'fog', 'lightning'].some((k) => fx[k]) || (state.config.audioStyle && (state.config.audioStyle.particleBurst || state.config.audioStyle.waves));
    if (needsOverlay && !overlay) createOverlay();
    if (!needsOverlay && overlay) { overlay.remove(); overlay = null; ctx = null; particles = []; }
  }
  function createOverlay() {
    overlay = document.createElement('canvas');
    overlay.style.cssText = 'position:fixed;inset:0;width:100vw;height:100vh;pointer-events:none;z-index:2147483647';
    document.documentElement.appendChild(overlay);
    ctx = overlay.getContext('2d');
    const resize = () => { if (!overlay) return; const s = (window.devicePixelRatio || 1) * (state.config.renderScale || 1); overlay.width = innerWidth * s; overlay.height = innerHeight * s; };
    resize(); addEventListener('resize', resize);
    requestAnimationFrame(drawOverlay);
  }
  const rnd = (a, b) => a + Math.random() * (b - a);
  function spawn(kind, w, h, burst) {
    switch (kind) {
      case 'snow': return { kind, x: rnd(0, w), y: burst ? rnd(-h, 0) : -10, vx: rnd(-0.3, 0.3), vy: rnd(0.4, 1.4), r: rnd(1, 3.5), a: rnd(0.5, 0.95) };
      case 'rain': return { kind, x: rnd(-w * 0.1, w), y: burst ? rnd(-h, 0) : -30, vx: 1.5, vy: rnd(12, 20), len: rnd(10, 24), a: rnd(0.25, 0.5) };
      case 'fire': return { kind, x: rnd(0, w), y: h + 5, vx: rnd(-0.4, 0.4), vy: -rnd(0.8, 2.4), r: rnd(1, 3), life: 1, a: 1 };
      default: return { kind: 'particles', x: rnd(0, w), y: rnd(0, h), vx: rnd(-0.3, 0.3), vy: rnd(-0.3, 0.3), r: rnd(1, 2.5), a: rnd(0.2, 0.7), life: 1, decay: burst ? 0.02 : 0 };
    }
  }
  function drawOverlay(ts) {
    if (!overlay || !ctx) return;
    requestAnimationFrame(drawOverlay);
    const dt = lastOverlay ? Math.min(3, (ts - lastOverlay) / 16.67) : 1; lastOverlay = ts;
    const w = overlay.width, h = overlay.height, s = w / Math.max(1, innerWidth);
    const fx = state.config.effects || {};
    ctx.clearRect(0, 0, w, h);
    const want = { snow: fx.snow ? 400 * fx.snow.intensity : 0, rain: fx.rain ? 600 * fx.rain.intensity : 0, fire: fx.fire ? 300 * fx.fire.intensity : 0,
                   particles: fx.particles ? 180 * fx.particles.intensity : 0 };
    const count = { snow: 0, rain: 0, fire: 0, particles: 0 };
    for (const p of particles) count[p.kind]++;
    for (const k in want) for (let i = count[k]; i < want[k] && i < count[k] + 8; i++) particles.push(spawn(k, w, h, count[k] === 0));
    if (fx.fog) {
      fogT += 0.002 * dt * (fx.fog.speed || 1);
      for (let i = 0; i < 3; i++) {
        const gx = (Math.sin(fogT + i * 2.1) * 0.5 + 0.5) * w, gy = h * (0.55 + i * 0.15);
        const g = ctx.createRadialGradient(gx, gy, 0, gx, gy, w * 0.6);
        g.addColorStop(0, `rgba(220,225,235,${0.16 * fx.fog.intensity})`); g.addColorStop(1, 'rgba(220,225,235,0)');
        ctx.fillStyle = g; ctx.fillRect(0, 0, w, h);
      }
    }
    const speedOf = (k) => (fx[k] && fx[k].speed) || 1;
    particles = particles.filter((p) => {
      const sp = speedOf(p.kind) * dt;
      p.x += p.vx * sp * s; p.y += p.vy * sp * s;
      if (p.kind === 'snow') { p.vx += Math.sin((ts / 1000) + p.y / 60) * 0.004; ctx.globalAlpha = p.a; ctx.fillStyle = '#fff'; ctx.beginPath(); ctx.arc(p.x, p.y, p.r * s, 0, 6.283); ctx.fill(); return p.y < h + 10; }
      if (p.kind === 'rain') { ctx.globalAlpha = p.a; ctx.strokeStyle = '#b9c7dd'; ctx.lineWidth = s; ctx.beginPath(); ctx.moveTo(p.x, p.y); ctx.lineTo(p.x - p.vx * 2, p.y - p.len * s); ctx.stroke(); return p.y < h + 30; }
      if (p.kind === 'fire') {
        p.life -= 0.006 * dt / ((fx.fire && fx.fire.height) || 0.3) * 0.3; if (p.life <= 0) return false;
        ctx.globalAlpha = p.life; ctx.fillStyle = `hsl(${20 + p.life * 30},100%,${45 + p.life * 20}%)`; ctx.beginPath(); ctx.arc(p.x, p.y, p.r * s * (0.5 + p.life), 0, 6.283); ctx.fill(); return true;
      }
      if (p.decay) { p.life -= p.decay * dt; if (p.life <= 0) return false; }
      if (p.x < 0) p.x += w; if (p.x > w) p.x -= w; if (p.y < 0) p.y += h; if (p.y > h) p.y -= h;
      ctx.globalAlpha = p.a * (p.life ?? 1); ctx.fillStyle = '#dfe6ff'; ctx.beginPath(); ctx.arc(p.x, p.y, p.r * s * (1 + audioPulse), 0, 6.283); ctx.fill(); return true;
    });
    ctx.globalAlpha = 1;
    if (fx.lightning) {
      if (!bolt && Math.random() < 0.0025 * dt * (fx.lightning.rate || 1) * (0.5 + fx.lightning.intensity)) {
        const pts = [[rnd(w * 0.2, w * 0.8), 0]]; while (pts[pts.length - 1][1] < h * rnd(0.4, 0.8)) { const [x, y] = pts[pts.length - 1]; pts.push([x + rnd(-40, 40) * s, y + rnd(20, 50) * s]); }
        bolt = { pts, life: 1 }; flash = 0.35 * fx.lightning.intensity;
      }
      if (flash > 0) { ctx.fillStyle = `rgba(230,236,255,${flash})`; ctx.fillRect(0, 0, w, h); flash -= 0.03 * dt; }
      if (bolt) {
        ctx.strokeStyle = `rgba(240,245,255,${bolt.life})`; ctx.lineWidth = 2.5 * s; ctx.shadowColor = '#9fb4ff'; ctx.shadowBlur = 18 * s;
        ctx.beginPath(); bolt.pts.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y))); ctx.stroke(); ctx.shadowBlur = 0;
        bolt.life -= 0.06 * dt; if (bolt.life <= 0) bolt = null;
      }
    }
    const style = state.config.audioStyle || {};
    if (style.waves && state.audio.left.length) {
      const bands = state.audio.left, n = bands.length, base = h * 0.92, amp = h * 0.18 * (style.sensitivity || 1);
      ctx.strokeStyle = 'rgba(160,178,255,0.75)'; ctx.lineWidth = 2 * s; ctx.beginPath();
      for (let i = 0; i < n; i++) { const x = (i / (n - 1)) * w, y = base - bands[i] * amp; i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); }
      ctx.stroke();
    }
  }

  let lastBass = 0;
  function onAudio(m) {
    state.audio = { level: m.level, left: m.l || [], right: m.r || [] };
    const we = resample(state.audio.left, 64).concat(resample(state.audio.right, 64));
    audioListeners.forEach((cb) => { try { cb(we); } catch (e) { report(e); } });
    emit('audio', state.audio);
    const style = state.config.audioStyle || {};
    const sens = style.sensitivity || 1;
    const bass = (state.audio.left.slice(0, 6).reduce((a, b) => a + b, 0) / 6) || 0;
    audioPulse = audioPulse * 0.7 + bass * 0.3 * sens;
    if (document.body) {
      if (style.pulse) { document.body.style.transformOrigin = '50% 50%'; document.body.style.transform = `scale(${(1 + audioPulse * 0.035).toFixed(4)})`; }
      if (style.glow) document.body.style.filter = `${state.cssFilter || ''} brightness(${(1 + audioPulse * 0.25).toFixed(3)}) saturate(${(1 + audioPulse * 0.3).toFixed(3)})`;
    }
    if (style.particleBurst && overlay && bass - lastBass > 0.18 / sens) {
      for (let i = 0; i < 24; i++) { const p = spawn('particles', overlay.width, overlay.height, true); p.vx = rnd(-3, 3); p.vy = rnd(-3, 3); p.a = 0.9; particles.push(p); }
    }
    lastBass = bass;
  }

  // ── Host messages ──
  host && host.addEventListener('message', (e) => {
    const m = e.data;
    if (!m || typeof m !== 'object') return;
    switch (m.type) {
      case 'config': state.config = m; state.fps = m.fps || 0; applyCss(); applyProperties(); emit('config', m); break;
      case 'pause': pause(); break;
      case 'resume': resume(); break;
      case 'audio': onAudio(m); break;
      case 'pointer': pointer(m); break;
      case 'key': key(m); break;
    }
  });

  window.addEventListener('error', (e) => report(e.error || e.message));
  window.addEventListener('unhandledrejection', (e) => report(e.reason));
  const ready = () => {
    document.head && document.head.appendChild(styleEl);
    applyCss();
    // Wallpaper Engine pages register their listener in inline scripts; give them a moment.
    setTimeout(applyProperties, 50);
    post({ type: 'ready' });
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', ready, { once: true }); else ready();

  window.wallpaperflow = Object.freeze({
    get config() { return state.config; },
    get properties() { return state.config.properties || {}; },
    get paused() { return state.paused; },
    get audio() { return state.audio; },
    on(name, cb) { (listeners[name] = listeners[name] || []).push(cb); },
    off(name, cb) { listeners[name] = (listeners[name] || []).filter((x) => x !== cb); },
    post(msg) { const s = JSON.stringify(msg); if (s.length <= 4096) post(msg); },
    version: '1.0',
  });
})();
