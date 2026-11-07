// Pure logic shared by the page and the Node tests: no DOM, no global state.

/* ---------- formatting ---------- */
const nf1 = new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
export const deg1 = v => v == null ? '—' : nf1.format(v) + ' °C';
export const degS = v => v == null ? '—' : nf1.format(v) + '°';
export const degR = v => v == null ? '—' : Math.round(v) + '°';
export const fmt1 = v => nf1.format(v);
const pad = n => String(n).padStart(2, '0');
export const clock = (t, withSeconds) => {
  const d = new Date(t);
  return pad(d.getHours()) + ':' + pad(d.getMinutes()) + (withSeconds ? ':' + pad(d.getSeconds()) : '');
};
const ESC = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
export const esc = s => String(s).replace(/[&<>"']/g, c => ESC[c]);

/* ---------- sorted time arrays ---------- */
/** First index whose value is >= x (a is sorted ascending). */
export function lowerBound(a, x) {
  let lo = 0, hi = a.length;
  while (lo < hi) { const m = (lo + hi) >> 1; if (a[m] < x) lo = m + 1; else hi = m; }
  return lo;
}
/** Index of the time closest to t, or -1 when there is none. */
export function nearestIndex(times, t) {
  const n = times.length;
  if (!n) return -1;
  const k = lowerBound(times, t);
  if (k <= 0) return 0;
  if (k >= n) return n - 1;
  return t - times[k - 1] <= times[k] - t ? k - 1 : k;
}
/** Last non-null index among the last `lookBack` entries before n, or -1. */
export function lastValid(a, n, lookBack = 6) {
  for (let k = n - 1; k >= Math.max(0, n - lookBack); k--) if (a[k] != null) return k;
  return -1;
}

/* ---------- devices ---------- */
export const KIND = {
  cpu:   { order: 0, label: 'Processeur',      thr: [70, 85, 95], icon: 'cpu' },
  gpu:   { order: 1, label: 'Carte graphique', thr: [70, 80, 90], icon: 'gpu' },
  mem:   { order: 2, label: 'Mémoire',         thr: [50, 65, 80], icon: 'mem' },
  board: { order: 3, label: 'Carte mère',      thr: [55, 70, 85], icon: 'board' },
  disk:  { order: 4, label: 'Stockage',        thr: [60, 70, 80], icon: 'disk' },
  other: { order: 5, label: 'Autre',           thr: [60, 75, 90], icon: 'temp' },
};
/** LibreHardwareMonitor hardware type -> KIND key. */
export const kindOf = t =>
  t === 'Cpu' ? 'cpu'
  : /^Gpu/.test(t) ? 'gpu'
  : t === 'Memory' ? 'mem'
  : /Motherboard|SuperIO|EmbeddedController/.test(t) ? 'board'
  : t === 'Storage' ? 'disk'
  : 'other';

export const STATUS = [
  { cls: 'good', label: 'Normal', glyph: '✓' },
  { cls: 'warning', label: 'Chaud', glyph: '!' },
  { cls: 'serious', label: 'Très chaud', glyph: '▲' },
  { cls: 'critical', label: 'Critique', glyph: '✕' },
];
/** 0..3 index into STATUS, or null without a value. */
export function statusOf(v, kind) {
  if (v == null) return null;
  const t = KIND[kind].thr;
  return v >= t[2] ? 3 : v >= t[1] ? 2 : v >= t[0] ? 1 : 0;
}

/* ---------- sensor names ---------- */
const PRIO = [/package/i, /core max/i, /^gpu core$/i, /hot ?spot/i, /core average/i, /memory junction/i, /composite/i, /^temperature$/i];
/** Lower = more representative of its device. */
export const prio = name => { const k = PRIO.findIndex(r => r.test(name)); return k < 0 ? 99 : k; };
export const CORE_RE = /\b([PE])?-?core #(\d+)/i;
/** "P-Core #3" -> "P3", "CPU Core #3" -> "C3". */
export const coreLabel = name => { const m = CORE_RE.exec(name); return m ? (m[1] ? m[1].toUpperCase() : 'C') + m[2] : name; };
/** Sort key: P-cores, then E-cores, then plain cores, each by number. */
export const coreKey = name => {
  const m = CORE_RE.exec(name);
  return m ? ({ P: 0, E: 1 }[(m[1] || '').toUpperCase()] ?? 2) * 1000 + +m[2] : 1e6;
};
export const shortName = name => name.replace(/^(CPU|GPU)\s+/i, '');
/** Short label of a device: its role, except drives (several can coexist) named by model. */
export const roleLabel = g => g.kind === 'disk' || g.kind === 'other' ? g.name : g.kind === 'gpu' ? 'GPU' : KIND[g.kind].label;

/**
 * Groups live sensors by device. Memory sticks share one group. Per group:
 * `all` (sensor indexes), `cores` (>= 3 per-core sensors, shown as a heatmap),
 * `lines` (the rest, most representative first), `slot` (color slot 0..7, -1 past 8),
 * `primary` (the sensor shown in the summary tile).
 */
export function buildGroups(sensors, alive) {
  const map = new Map();
  sensors.forEach((s, i) => {
    if (!alive[i]) return;
    const kind = kindOf(s.hwType);
    const key = kind === 'mem' ? 'mem' : s.hw;
    let g = map.get(key);
    if (!g) map.set(key, g = { id: key, name: s.hwName, names: new Set(), kind, all: [] });
    g.names.add(s.hwName);
    g.all.push(i);
  });
  const list = [...map.values()];
  for (const g of list) {
    if (g.kind === 'mem' && g.names.size > 1) g.name = `Mémoire vive · ${g.names.size} barrettes`;
    const cores = g.all.filter(i => CORE_RE.test(sensors[i].name));
    g.cores = cores.length >= 3 ? cores.sort((a, b) => coreKey(sensors[a].name) - coreKey(sensors[b].name)) : [];
    g.lines = g.all.filter(i => !g.cores.includes(i))
      .sort((a, b) => prio(sensors[a].name) - prio(sensors[b].name) || sensors[a].index - sensors[b].index || a - b);
    g.slot = new Map(g.lines.map((i, k) => [i, k < 8 ? k : -1]));
    g.primary = g.lines.length ? g.lines[0] : g.cores[0];
  }
  return list.sort((a, b) => KIND[a.kind].order - KIND[b.kind].order || a.name.localeCompare(b.name));
}

/* ---------- statistics ---------- */
/** now / min / max / avg of a[i0..n). `now` is the latest value among the last 4 entries. */
export function statsOf(a, i0, n) {
  let mn = Infinity, mx = -Infinity, sum = 0, cnt = 0, now = null;
  for (let k = i0; k < n; k++) { const v = a[k]; if (v == null) continue; if (v < mn) mn = v; if (v > mx) mx = v; sum += v; cnt++; }
  for (let k = n - 1; k >= Math.max(0, n - 4); k--) if (a[k] != null) { now = a[k]; break; }
  return cnt ? { now, min: mn, max: mx, avg: sum / cnt } : { now, min: null, max: null, avg: null };
}
/** Mean of the given sensors at row k (columns = values[sensor][row]), rounded to 0.1, or null. */
export function meanAt(values, idxs, k) {
  let sum = 0, cnt = 0;
  for (const i of idxs) { const v = values[i][k]; if (v != null) { sum += v; cnt++; } }
  return cnt ? Math.round(sum / cnt * 10) / 10 : null;
}

/* ---------- heat ramp (temperature only, always shown with its scale) ---------- */
export const HEAT_STOPS = [[25, [0x18, 0x4f, 0x95]], [40, [0x39, 0x87, 0xe5]], [55, [0x1b, 0xaf, 0x7a]], [70, [0xed, 0xa1, 0x00]], [85, [0xeb, 0x68, 0x34]], [100, [0xe3, 0x49, 0x48]]];
const heatCache = [];
/** CSS color for a temperature, clamped to the 25..100 °C ramp (0.5 °C steps). */
export function heat(v) {
  const k = Math.max(0, Math.min(250, Math.round(v * 2)));
  if (heatCache[k]) return heatCache[k];
  const t = k / 2, S = HEAT_STOPS;
  let rgb;
  if (t <= S[0][0]) rgb = S[0][1];
  else if (t >= S[S.length - 1][0]) rgb = S[S.length - 1][1];
  else {
    let i = 0;
    while (t > S[i + 1][0]) i++;
    const [a, ca] = S[i], [b, cb] = S[i + 1], f = (t - a) / (b - a);
    rgb = ca.map((x, j) => Math.round(x + (cb[j] - x) * f));
  }
  return heatCache[k] = `rgb(${rgb})`;
}

/* ---------- axes ---------- */
/** Y range in 5° steps, at least 20° tall, with a tick step of 5, 10 or 20. */
export function niceRange(lo, hi) {
  if (!isFinite(lo) || !isFinite(hi)) { lo = 35; hi = 55; }
  let a = Math.floor((lo - 2) / 5) * 5, b = Math.ceil((hi + 2) / 5) * 5;
  if (b - a < 20) {
    const mid = (lo + hi) / 2;
    a = Math.floor((mid - 10) / 5) * 5;
    b = a + 20;
    if (b < hi + 1) b += 5;
  }
  const step = b - a <= 30 ? 5 : b - a <= 60 ? 10 : 20;
  a = Math.floor(a / step) * step;
  b = Math.ceil(b / step) * step;
  return { lo: Math.max(0, a), hi: b, step };
}
const TIME_STEPS = [5e3, 10e3, 15e3, 30e3, 60e3, 120e3, 300e3, 600e3, 900e3, 1800e3, 3600e3];
/** Time tick step for a span drawn over `px` pixels (about one label per 105 px). */
export const timeStep = (spanMs, px) => TIME_STEPS.find(s => spanMs / s <= Math.max(2, Math.floor(px / 105))) || 3600e3;

/* ---------- geometry ---------- */
/**
 * Traces a series into a path (anything with moveTo / lineTo). Nulls and time gaps break the line.
 * Above ~1 point per pixel, each pixel column keeps its first, min, max and last value so peaks stay visible.
 * Returns { segs, firstX, lastX }.
 */
export function tracePath(path, times, a, i0, n, X, Y, pw, gapMs) {
  const dense = n - i0 > pw * 1.2;
  let pen = false, prevT = null, segs = 0, firstX = null, lastX = null;
  if (!dense) {
    for (let k = i0; k < n; k++) {
      const v = a[k], t = times[k];
      if (v == null || (prevT != null && t - prevT > gapMs)) pen = false;
      prevT = t;
      if (v == null) continue;
      const x = X(t), y = Y(v);
      if (pen) path.lineTo(x, y);
      else { path.moveTo(x, y); pen = true; segs++; if (firstX == null) firstX = x; }
      lastX = x;
    }
    return { segs, firstX, lastX };
  }
  let px = null, mn = 0, mx = 0, fv = 0, lv = 0;
  const flush = () => {
    if (px == null) return;
    if (!pen) { path.moveTo(px, Y(fv)); pen = true; segs++; if (firstX == null) firstX = px; }
    else path.lineTo(px, Y(fv));
    if (mn !== mx) { path.lineTo(px, Y(mn)); path.lineTo(px, Y(mx)); }
    path.lineTo(px, Y(lv));
    lastX = px;
  };
  for (let k = i0; k < n; k++) {
    const v = a[k], t = times[k];
    if (v == null || (prevT != null && t - prevT > gapMs)) { flush(); px = null; pen = false; }
    prevT = t;
    if (v == null) continue;
    const p = Math.round(X(t));
    if (p !== px) { flush(); px = p; mn = mx = fv = lv = v; }
    else { if (v < mn) mn = v; if (v > mx) mx = v; lv = v; }
  }
  flush();
  return { segs, firstX, lastX };
}

/**
 * Max value per time bucket of `bucketMs`, buckets aligned on absolute time so they do not
 * shimmer while the window scrolls. Returns [{ b, max }] where the bucket spans [b*bucketMs, (b+1)*bucketMs).
 */
export function bucketMax(times, a, i0, n, bucketMs) {
  const out = [];
  let cur = null, mx = -Infinity;
  const push = () => { if (cur != null && mx !== -Infinity) out.push({ b: cur, max: mx }); };
  for (let k = i0; k < n; k++) {
    const b = Math.floor(times[k] / bucketMs);
    if (b !== cur) { push(); cur = b; mx = -Infinity; }
    const v = a[k];
    if (v != null && v > mx) mx = v;
  }
  push();
  return out;
}

/** Spreads labels vertically so they stay `gap` px apart within [top, bottom]; keeps the order of `y`. */
export function spreadLabels(ys, gap, top, bottom) {
  const L = ys.map((y, i) => ({ i, y, ly: y })).sort((a, b) => a.y - b.y);
  for (let j = 0; j < L.length; j++) L[j].ly = Math.max(L[j].y, j ? L[j - 1].ly + gap : top);
  for (let j = L.length - 1; j >= 0; j--) {
    const lim = j === L.length - 1 ? bottom : L[j + 1].ly - gap;
    if (L[j].ly > lim) L[j].ly = lim;
  }
  const out = new Array(ys.length);
  for (const l of L) out[l.i] = l.ly;
  return out;
}
