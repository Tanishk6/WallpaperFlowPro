// Clock wallpaper: updates once per second (or once per minute without seconds) — near-zero CPU.
(() => {
  const opts = { style: 'minimal', accent: '#7A8CFF', seconds: true, h24: true, date: true };
  const $ = (id) => document.getElementById(id);
  const pad = (n) => String(n).padStart(2, '0');
  let timer = 0;

  function apply(props) {
    for (const k of Object.keys(opts)) if (props && props[k] && 'value' in props[k]) opts[k] = props[k].value;
    let accent = String(opts.accent);
    if (/^[\d.]+ [\d.]+ [\d.]+$/.test(accent)) accent = 'rgb(' + accent.split(' ').map((v) => Math.round(parseFloat(v) * 255)).join(',') + ')'; // WE colour format
    document.documentElement.style.setProperty('--accent', accent);
    document.body.dataset.style = opts.style;
    tick();
  }

  const WORDS = ['twelve', 'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine', 'ten', 'eleven'];
  function words(d) {
    let h = d.getHours(), m = Math.round(d.getMinutes() / 5) * 5;
    if (m === 60) { m = 0; h++; }
    const next = WORDS[(h + 1) % 12], cur = WORDS[h % 12];
    const phrases = { 0: `<b>${cur}</b> o'clock`, 5: `five past <b>${cur}</b>`, 10: `ten past <b>${cur}</b>`, 15: `quarter past <b>${cur}</b>`,
      20: `twenty past <b>${cur}</b>`, 25: `twenty-five past <b>${cur}</b>`, 30: `half past <b>${cur}</b>`, 35: `twenty-five to <b>${next}</b>`,
      40: `twenty to <b>${next}</b>`, 45: `quarter to <b>${next}</b>`, 50: `ten to <b>${next}</b>`, 55: `five to <b>${next}</b>` };
    return 'It is ' + phrases[m];
  }

  function drawAnalog(d) {
    const c = $('a-canvas'), g = c.getContext('2d'), r = c.width / 2;
    const accent = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#7A8CFF';
    g.clearRect(0, 0, c.width, c.height);
    g.save(); g.translate(r, r);
    for (let i = 0; i < 60; i++) {
      g.rotate(Math.PI / 30);
      g.fillStyle = i % 5 === 4 ? 'rgba(233,236,245,.85)' : 'rgba(233,236,245,.25)';
      g.fillRect(-2, -r * 0.94, 4, i % 5 === 4 ? r * 0.08 : r * 0.03);
    }
    const hand = (angle, len, width, color) => { g.save(); g.rotate(angle); g.strokeStyle = color; g.lineWidth = width; g.lineCap = 'round';
      g.beginPath(); g.moveTo(0, r * 0.08); g.lineTo(0, -len); g.stroke(); g.restore(); };
    const s = d.getSeconds(), m = d.getMinutes() + s / 60, h = (d.getHours() % 12) + m / 60;
    hand(h * Math.PI / 6, r * 0.5, 14, '#E9ECF5');
    hand(m * Math.PI / 30, r * 0.75, 8, '#E9ECF5');
    if (opts.seconds) hand(s * Math.PI / 30, r * 0.82, 3, accent);
    g.fillStyle = accent; g.beginPath(); g.arc(0, 0, 10, 0, Math.PI * 2); g.fill();
    g.restore();
  }

  function tick() {
    clearTimeout(timer);
    if (window.wallpaperflow && window.wallpaperflow.paused) { timer = setTimeout(tick, 1000); return; }
    const d = new Date();
    let h = d.getHours();
    const suffix = opts.h24 ? '' : (h < 12 ? ' AM' : ' PM');
    if (!opts.h24) h = h % 12 || 12;
    const hm = `${opts.h24 ? pad(h) : h}:${pad(d.getMinutes())}`;
    const dateText = opts.date ? d.toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long' }) : '';
    $('m-hm').textContent = hm + suffix; $('m-s').textContent = opts.seconds ? pad(d.getSeconds()) : '';
    $('d-time').textContent = hm + (opts.seconds ? ':' + pad(d.getSeconds()) : '') + suffix;
    $('w-text').innerHTML = words(d);
    for (const id of ['m-date', 'd-date', 'a-date', 'w-date']) $(id).textContent = dateText;
    if (opts.style === 'analog') drawAnalog(d);
    const interval = opts.seconds || opts.style === 'analog' ? 1000 - d.getMilliseconds() : 60000 - d.getSeconds() * 1000 - d.getMilliseconds();
    timer = setTimeout(tick, interval + 5);
  }

  window.wallpaperPropertyListener = { applyUserProperties: apply, setPaused: (p) => { if (!p) tick(); } };
  if (window.wallpaperflow) window.wallpaperflow.on('config', (c) => apply(c.properties));
  apply(window.wallpaperflow ? window.wallpaperflow.properties : null);
})();
