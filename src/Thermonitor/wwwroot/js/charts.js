// Canvas charts: line chart, per-core heatmap, sparkline. All draw from S on each frame.

import { S, ui, C, FONT, charts, requestRender, withAlpha } from './store.js';
import { lowerBound, nearestIndex, lastValid, niceRange, timeStep, tracePath, bucketMax, spreadLabels,
         heat, clock, deg1, degS, degR, esc, shortName, coreLabel } from './domain.js';

const gapMs = () => Math.max(5000, S.interval * 3.5);

class CanvasView {
  constructor(host, height) {
    this.wrap = Object.assign(document.createElement('div'), { className: 'chart' });
    this.wrap.style.height = height + 'px';
    this.cv = document.createElement('canvas');
    this.tip = Object.assign(document.createElement('div'), { className: 'tip', hidden: true });
    this.wrap.append(this.cv, this.tip);
    host.append(this.wrap);
    this.ctx = this.cv.getContext('2d');
    this.w = 0; this.h = height; this.dpr = 1;
    this.observer = new ResizeObserver(() => this.resize());
    this.observer.observe(this.wrap);
    charts.add(this);
  }
  dispose() { this.observer.disconnect(); }
  resize() {
    const w = this.wrap.clientWidth, h = this.wrap.clientHeight, dpr = window.devicePixelRatio || 1;
    if (w === this.w && h === this.h && dpr === this.dpr && this.cv.width > 1) return;
    Object.assign(this, { w, h, dpr });
    this.cv.width = Math.max(1, Math.round(w * dpr));
    this.cv.height = Math.max(1, Math.round(h * dpr));
    requestRender();
  }
  begin() {
    const { ctx, dpr } = this;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, this.w, this.h);
    return ctx;
  }
  placeTip(x, y, html) {
    const tip = this.tip;
    if (tip._html !== html) { tip.innerHTML = html; tip._html = html; }
    tip.hidden = false;
    const tw = tip.offsetWidth, th = tip.offsetHeight;
    let left = x + 16;
    if (left + tw > this.w) left = x - tw - 16;
    left = Math.max(0, left);
    const top = Math.max(0, Math.min(y - th / 2, this.h - th));
    tip.style.transform = `translate(${Math.round(left)}px, ${Math.round(top)}px)`;
  }
  hideTip() { this.tip.hidden = true; }
}

function clearHover() {
  if (ui.hoverSrc) ui.hoverSrc.hideTip();
  ui.hoverT = null; ui.hoverSrc = null;
  requestRender();
}

// Pointer and keyboard both move one shared crosshair, drawn on every chart.
function bindHover(view, toTime) {
  const cv = view.cv;
  cv.tabIndex = 0;
  cv.addEventListener('pointermove', e => {
    const r = cv.getBoundingClientRect();
    const t = toTime(e.clientX - r.left, e.clientY - r.top);
    if (t == null) { if (ui.hoverSrc === view) clearHover(); return; }
    const k = nearestIndex(S.t, t);
    ui.hoverT = k < 0 ? null : S.t[k];
    ui.hoverSrc = view;
    view.pointerY = e.clientY - r.top;
    requestRender();
  });
  cv.addEventListener('pointerleave', () => { if (ui.hoverSrc === view) clearHover(); });
  cv.addEventListener('focus', () => {
    if (!S.t.length) return;
    ui.hoverT = S.t[S.t.length - 1]; ui.hoverSrc = view; view.pointerY = null;
    requestRender();
  });
  cv.addEventListener('blur', () => { if (ui.hoverSrc === view) clearHover(); });
  cv.addEventListener('keydown', e => {
    if (ui.hoverSrc !== view || ui.hoverT == null) return;
    const step = e.shiftKey ? 10 : 1;
    let k = nearestIndex(S.t, ui.hoverT);
    if (e.key === 'ArrowLeft') k = Math.max(0, k - step);
    else if (e.key === 'ArrowRight') k = Math.min(S.t.length - 1, k + step);
    else if (e.key === 'Escape') { clearHover(); return; }
    else return;
    e.preventDefault();
    ui.hoverT = S.t[k];
    requestRender();
  });
}

function dot(ctx, x, y, color, r) {
  ctx.beginPath(); ctx.arc(x, y, r + 2, 0, Math.PI * 2); ctx.fillStyle = C.surface; ctx.fill();
  ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.fillStyle = color; ctx.fill();
}

function fillUnder(ctx, a, i0, n, X, Y, pw, top, bottom, color, alpha) {
  ctx.beginPath();
  const r = tracePath(ctx, S.t, a, i0, n, X, Y, pw, gapMs());
  if (r.segs !== 1) return;   // a broken line would close into odd shapes
  ctx.lineTo(r.lastX, bottom); ctx.lineTo(r.firstX, bottom); ctx.closePath();
  const grad = ctx.createLinearGradient(0, top, 0, bottom);
  grad.addColorStop(0, withAlpha(color, alpha)); grad.addColorStop(1, withAlpha(color, 0));
  ctx.fillStyle = grad; ctx.fill();
}

/** Line chart. series: [{ id, name, slot, data }], data aligned with S.t. */
export class LineChart extends CanvasView {
  constructor(host, series, height, title) {
    super(host, height);
    this.series = series;
    this.cv.setAttribute('role', 'img');
    this.cv.setAttribute('aria-label', `Courbes de température : ${title}. Flèches gauche et droite pour parcourir.`);
    this.m = this.margins();
    bindHover(this, x => {
      const m = this.m, pw = this.w - m.l - m.r;
      if (x < m.l - 4 || x > m.l + pw + 4 || !this.view) return null;
      return this.view.x0 + (Math.min(Math.max(x, m.l), m.l + pw) - m.l) / pw * (this.view.x1 - this.view.x0);
    });
  }
  margins() {
    // Up to 4 series also get direct labels in a right gutter.
    const labels = this.series.length <= 4 && this.w >= 520;
    return { l: 40, r: labels ? 158 : 14, t: 10, b: 24, labels };
  }
  draw(view) {
    if (!this.w) return;
    this.view = view;
    const ctx = this.begin(), w = this.w;
    const m = this.m = this.margins();
    const pw = w - m.l - m.r, ph = this.h - m.t - m.b;
    const n = S.t.length, i0 = Math.max(0, lowerBound(S.t, view.x0) - 1);
    const vis = this.series.filter(s => !ui.hidden.has(s.id));

    let lo = Infinity, hi = -Infinity;
    for (const s of vis) for (let k = i0; k < n; k++) { const v = s.data[k]; if (v != null) { if (v < lo) lo = v; if (v > hi) hi = v; } }
    const yr = niceRange(lo, hi);
    const X = t => m.l + (t - view.x0) / (view.x1 - view.x0) * pw;
    const Y = v => m.t + (1 - (v - yr.lo) / (yr.hi - yr.lo)) * ph;

    // grid and y ticks
    ctx.font = `11px ${FONT}`; ctx.lineWidth = 1;
    ctx.textAlign = 'right'; ctx.textBaseline = 'middle';
    for (let v = yr.lo; v <= yr.hi + 1e-6; v += yr.step) {
      const y = Math.round(Y(v)) + .5;
      ctx.strokeStyle = C.grid; ctx.beginPath(); ctx.moveTo(m.l, y); ctx.lineTo(m.l + pw, y); ctx.stroke();
      ctx.fillStyle = C.muted; ctx.fillText(v + '°', m.l - 8, y);
    }
    // x axis
    const by = Math.round(m.t + ph) + .5;
    ctx.strokeStyle = C.axis; ctx.beginPath(); ctx.moveTo(m.l, by); ctx.lineTo(m.l + pw, by); ctx.stroke();
    const step = timeStep(view.x1 - view.x0, pw);
    ctx.textAlign = 'center'; ctx.textBaseline = 'top'; ctx.fillStyle = C.muted;
    for (let t = Math.ceil(view.x0 / step) * step; t <= view.x1; t += step) {
      const x = Math.round(X(t)) + .5;
      if (x < m.l + 18 || x > m.l + pw - 18) continue;
      ctx.beginPath(); ctx.moveTo(x, by); ctx.lineTo(x, by + 4); ctx.stroke();
      ctx.fillText(clock(t, step < 60e3), x, by + 7);
    }

    if (!vis.length) {
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.fillStyle = C.muted;
      ctx.fillText('Toutes les courbes sont masquées', m.l + pw / 2, m.t + ph / 2);
      this.hideTip();
      return;
    }

    // series (first one on top, with a soft fill)
    ctx.save();
    ctx.beginPath(); ctx.rect(m.l, m.t - 8, pw, ph + 8); ctx.clip();
    ctx.lineJoin = 'round'; ctx.lineCap = 'round';
    for (let j = vis.length - 1; j >= 0; j--) {
      const s = vis[j], color = C.series[s.slot];
      if (j === 0) fillUnder(ctx, s.data, i0, n, X, Y, pw, m.t, m.t + ph, color, .20);
      ctx.beginPath(); tracePath(ctx, S.t, s.data, i0, n, X, Y, pw, gapMs());
      ctx.strokeStyle = color; ctx.lineWidth = 2; ctx.stroke();
    }
    ctx.restore();

    // end markers and direct labels
    const ends = [];
    for (const s of vis) {
      const k = lastValid(s.data, n);
      if (k < 0) continue;
      const x = X(S.t[k]);
      if (x < m.l || x > m.l + pw + 1) continue;
      ends.push({ s, x: Math.min(x, m.l + pw), y: Y(s.data[k]), v: s.data[k] });
    }
    for (const e of ends) dot(ctx, e.x, e.y, C.series[e.s.slot], 3.5);
    if (m.labels && ends.length) {
      const ly = spreadLabels(ends.map(e => e.y), 17, m.t + 6, m.t + ph - 6);
      const lx = m.l + pw + 14;
      ctx.textBaseline = 'middle'; ctx.textAlign = 'left';
      ends.forEach((e, j) => {
        ctx.fillStyle = C.series[e.s.slot];
        ctx.beginPath(); ctx.roundRect(lx, ly[j] - 1.5, 9, 3, 1.5); ctx.fill();
        ctx.font = `600 12px ${FONT}`; ctx.fillStyle = C.ink;
        const val = degS(e.v);
        ctx.fillText(val, lx + 15, ly[j]);
        const nx = lx + 15 + ctx.measureText(val).width + 6;
        ctx.font = `11.5px ${FONT}`; ctx.fillStyle = C.ink2;
        let name = shortName(e.s.name);
        while (name.length > 3 && ctx.measureText(name).width > w - nx - 2) name = name.slice(0, -2) + '…';
        ctx.fillText(name, nx, ly[j]);
      });
    }

    // crosshair
    if (ui.hoverT == null || ui.hoverT < view.x0 || ui.hoverT > view.x1) { this.hideTip(); return; }
    const k = nearestIndex(S.t, ui.hoverT), x = Math.round(X(S.t[k])) + .5;
    ctx.strokeStyle = C.axis; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(x, m.t); ctx.lineTo(x, m.t + ph); ctx.stroke();
    const rows = [];
    for (const s of vis) {
      const v = s.data[k];
      if (v == null) continue;
      dot(ctx, x, Y(v), C.series[s.slot], 4);
      rows.push({ s, v });
    }
    if (ui.hoverSrc !== this) { this.hideTip(); return; }
    rows.sort((a, b) => b.v - a.v);
    this.placeTip(x, this.pointerY ?? m.t + ph / 2,
      `<div class="tip-time">${clock(S.t[k], true)}</div>` + rows.map(r =>
        `<div class="tip-row"><i style="background:${C.series[r.s.slot]}"></i><span>${esc(r.s.name)}</span><b>${deg1(r.v)}</b></div>`).join(''));
  }
}

/** One row per core, one cell per time bucket, colored by the max temperature in that bucket. */
export class CoreMap extends CanvasView {
  static ROW = 11;
  static GAP = 2;
  constructor(host, cores, alignWith) {
    super(host, cores.length * (CoreMap.ROW + CoreMap.GAP) - CoreMap.GAP + 4);
    this.cores = cores;
    this.alignWith = alignWith;   // shares the line chart's margins so time lines up
    this.cv.setAttribute('role', 'img');
    this.cv.setAttribute('aria-label', `Carte thermique des ${cores.length} cœurs. Valeurs détaillées dans la vue tableau.`);
    bindHover(this, (x, y) => {
      const m = this.margins(), pw = this.w - m.l - m.r;
      if (x < m.l || x > m.l + pw || !this.view) return null;
      this.hoverRow = Math.max(0, Math.min(cores.length - 1, Math.floor((y - 2) / (CoreMap.ROW + CoreMap.GAP))));
      return this.view.x0 + (x - m.l) / pw * (this.view.x1 - this.view.x0);
    });
  }
  margins() { const cm = this.alignWith && this.alignWith.m; return { l: cm ? cm.l : 40, r: cm ? cm.r : 60 }; }
  rowY(r) { return 2 + r * (CoreMap.ROW + CoreMap.GAP); }
  draw(view) {
    if (!this.w) return;
    this.view = view;
    const ctx = this.begin(), m = this.margins(), pw = this.w - m.l - m.r;
    const ROW = CoreMap.ROW, n = S.t.length;
    const i0 = Math.max(0, lowerBound(S.t, view.x0) - 1);
    const span = view.x1 - view.x0, iv = S.interval || 1000;
    const cols = Math.max(1, Math.min(Math.floor(pw / 4), Math.round(span / iv)));
    const bucketMs = Math.max(iv, Math.ceil(span / cols / iv) * iv);
    const X = t => m.l + (t - view.x0) / span * pw;

    ctx.save(); ctx.beginPath(); ctx.rect(m.l, 0, pw, this.h); ctx.clip();
    this.cores.forEach((i, r) => {
      const y = this.rowY(r);
      for (const { b, max } of bucketMax(S.t, S.v[i], i0, n, bucketMs)) {
        const xa = X(b * bucketMs), cw = X((b + 1) * bucketMs) - xa;
        ctx.fillStyle = heat(max);
        ctx.fillRect(xa, y, cw > 4 ? cw - 1 : cw + .5, ROW);
      }
    });
    ctx.restore();

    ctx.font = `10.5px ${FONT}`; ctx.textBaseline = 'middle';
    this.cores.forEach((i, r) => {
      const y = this.rowY(r) + ROW / 2;
      ctx.textAlign = 'right'; ctx.fillStyle = C.muted;
      ctx.fillText(coreLabel(S.sensors[i].name), m.l - 8, y);
      if (m.r < 40) return;
      const k = lastValid(S.v[i], n);
      ctx.textAlign = 'left'; ctx.fillStyle = C.ink2;
      ctx.fillText(k < 0 ? '—' : degR(S.v[i][k]), m.l + pw + 14, y);
    });

    if (ui.hoverT == null || ui.hoverT < view.x0 || ui.hoverT > view.x1) { this.hideTip(); return; }
    const k = nearestIndex(S.t, ui.hoverT), x = Math.round(X(S.t[k])) + .5;
    ctx.strokeStyle = C.ink; ctx.globalAlpha = .55; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, this.h); ctx.stroke();
    ctx.globalAlpha = 1;
    if (ui.hoverSrc !== this || this.hoverRow == null) { this.hideTip(); return; }
    const i = this.cores[this.hoverRow], v = S.v[i][k], y = this.rowY(this.hoverRow);
    ctx.strokeStyle = C.ink; ctx.lineWidth = 1.5;
    ctx.strokeRect(m.l - .5, y - 1, pw + 1, ROW + 2);
    this.placeTip(x, y + ROW / 2, `<div class="tip-time">${clock(S.t[k], true)}</div>
      <div class="tip-row"><i style="background:${v == null ? C.muted : heat(v)}"></i><span>${esc(S.sensors[i].name)}</span><b>${deg1(v)}</b></div>`);
  }
}

/** Small single-series line for the summary tiles. */
export class Spark extends CanvasView {
  constructor(host, sensorIndex) {
    super(host, 46);
    this.i = sensorIndex;
    this.cv.setAttribute('aria-hidden', 'true');
  }
  draw(view) {
    if (!this.w) return;
    const ctx = this.begin(), a = S.v[this.i], n = S.t.length;
    const i0 = Math.max(0, lowerBound(S.t, view.x0) - 1);
    let lo = Infinity, hi = -Infinity;
    for (let k = i0; k < n; k++) { const v = a[k]; if (v != null) { if (v < lo) lo = v; if (v > hi) hi = v; } }
    if (!isFinite(lo)) return;
    if (hi - lo < 8) { const mid = (hi + lo) / 2; lo = mid - 4; hi = mid + 4; }
    const pad = 6, pw = this.w - 2 * pad, ph = this.h - 10;
    const X = t => pad + (t - view.x0) / (view.x1 - view.x0) * pw;
    const Y = v => 4 + (1 - (v - lo) / (hi - lo)) * ph;
    const color = C.series[0];
    ctx.save(); ctx.beginPath(); ctx.rect(pad, 0, pw, this.h); ctx.clip();
    ctx.lineJoin = 'round'; ctx.lineCap = 'round';
    fillUnder(ctx, a, i0, n, X, Y, pw, 0, this.h, color, .22);
    ctx.beginPath(); tracePath(ctx, S.t, a, i0, n, X, Y, pw, gapMs());
    ctx.strokeStyle = color; ctx.lineWidth = 2; ctx.stroke();
    ctx.restore();
    const k = lastValid(a, n);
    if (k >= 0) dot(ctx, Math.min(X(S.t[k]), pad + pw), Y(a[k]), color, 3);
  }
}
