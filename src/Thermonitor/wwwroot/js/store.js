// Shared page state, theme colors and the render loop.

export const storage = {
  get(key, fallback) {
    try { const v = localStorage.getItem('thermonitor.' + key); return v == null ? fallback : JSON.parse(v); }
    catch { return fallback; }
  },
  set(key, value) {
    try { localStorage.setItem('thermonitor.' + key, JSON.stringify(value)); }
    catch { /* private mode: preferences are not kept */ }
  },
};

/** Data received from the server. v[sensor][row] is aligned with t[row]. */
export const S = { sensors: [], t: [], v: [], alive: [], admin: false, pawnio: null, interval: 1000, state: 'connecting', error: null, u: 0, lib: '' };

/** Viewer preferences and hover state. */
export const ui = {
  win: storage.get('win', 300000),
  view: storage.get('view', 'charts'),
  hidden: new Set(storage.get('hidden', [])),
  theme: storage.get('theme', null),
  hoverT: null,
  hoverSrc: null,
};

/** Colors read from the CSS tokens (canvas cannot use var()). */
export const C = { series: [] };
export const FONT = 'system-ui, -apple-system, "Segoe UI Variable Text", "Segoe UI", sans-serif';
export function readColors() {
  const cs = getComputedStyle(document.documentElement);
  const g = n => cs.getPropertyValue(n).trim();
  Object.assign(C, {
    surface: g('--surface'), surface2: g('--surface-2'), ink: g('--ink'), ink2: g('--ink-2'),
    muted: g('--muted'), grid: g('--grid'), axis: g('--axis'),
    series: [1, 2, 3, 4, 5, 6, 7, 8].map(i => g('--s' + i)),
  });
}
export function withAlpha(hex, a) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex);
  if (!m) return hex;
  const n = parseInt(m[1], 16);
  return `rgba(${n >> 16},${(n >> 8) & 255},${n & 255},${a})`;
}

/* ---------- render loop ---------- */
/** Every canvas view on the page; each has draw(view) and dispose(). */
export const charts = new Set();
/** Called before the page is rebuilt: stops observing the old canvases. */
export function clearCharts() {
  for (const c of charts) c.dispose();
  charts.clear();
}

// The right edge of every chart slides smoothly to the newest sample.
const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)');
const anim = { from: 0, to: 0, start: 0, dur: 450 };
function currentX1(now) {
  if (!anim.start) return anim.to;
  const p = Math.min(1, (now - anim.start) / anim.dur);
  return anim.from + (anim.to - anim.from) * (1 - Math.pow(1 - p, 3));
}
export function jumpTo(t) { anim.from = anim.to = t; anim.start = 0; }
export function slideTo(t) {
  const now = performance.now();
  const from = currentX1(now);
  if (reduceMotion.matches || t - from > S.interval * 3) { jumpTo(t); return; }
  Object.assign(anim, { from, to: t, start: now, dur: Math.min(450, S.interval * 0.6) });
}

// Moving the window to a screen with another scaling changes devicePixelRatio without resizing
// anything: watch it so canvases are re-rasterised instead of staying blurry.
function watchPixelRatio() {
  matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`).addEventListener('change', () => {
    for (const c of charts) c.resize();
    watchPixelRatio();
  }, { once: true });
}
watchPixelRatio();

let raf = 0;
export function requestRender() { if (!raf) raf = requestAnimationFrame(frame); }
function frame(now) {
  raf = 0;
  const x1 = currentX1(now);
  const view = { x0: x1 - ui.win, x1 };
  for (const c of charts) c.draw(view);
  if (anim.start && now - anim.start < anim.dur) requestRender();
}
