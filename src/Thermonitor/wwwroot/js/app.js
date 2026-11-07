// Page: receives the feed, builds the DOM, keeps the text up to date.

import { S, ui, storage, clearCharts, readColors, requestRender, jumpTo, slideTo } from './store.js';
import { LineChart, CoreMap, Spark } from './charts.js';
import { KIND, STATUS, statusOf, buildGroups, statsOf, lowerBound, roleLabel,
         deg1, degS, degR, fmt1, esc } from './domain.js';
import { applySnapshot, applyTick, buildDerived, isStale } from './feed.js';

const $ = (s, el = document) => el.querySelector(s);
const h = (tag, cls, html) => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html != null) e.innerHTML = html;
  return e;
};
const setText = (el, txt) => { if (el.textContent !== txt) el.textContent = txt; };

const ICON = {
  cpu: '<rect x="5" y="5" width="14" height="14" rx="2.5"/><rect x="9" y="9" width="6" height="6" rx="1"/><path d="M9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3"/>',
  gpu: '<rect x="2" y="6" width="20" height="12" rx="2.5"/><circle cx="9" cy="12" r="3"/><path d="M15 10h3M15 14h3M5 18v3"/>',
  mem: '<rect x="2" y="7" width="20" height="10" rx="1.5"/><path d="M6 10.5v3M10 10.5v3M14 10.5v3M18 10.5v3M6 17v3M18 17v3"/>',
  board: '<rect x="3" y="3" width="18" height="18" rx="2.5"/><rect x="7" y="7" width="5" height="5" rx="1"/><path d="M15.5 7.5h2M15.5 10.5h2M7 16.5h10M12 12v4.5"/>',
  disk: '<rect x="3" y="5" width="18" height="14" rx="2.5"/><path d="M3 12h18M7 15.5h.01M10.5 15.5h6"/>',
  temp: '<path d="M14 14.76V4a2 2 0 0 0-4 0v10.76a4 4 0 1 0 4 0Z"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
  moon: '<path d="M20.5 14.5A8.5 8.5 0 1 1 9.5 3.5a7 7 0 0 0 11 11Z"/>',
};
const svg = name => `<svg class="i" viewBox="0 0 24 24" aria-hidden="true">${ICON[name]}</svg>`;

const GAUGE_MIN = 20, GAUGE_MAX = 100;
const RESTART_TIMEOUT_MS = 30000;

/* ================= state ================= */
let conn = 'connecting';        // connecting | ok | lost | restarting | dead
let groups = [];
let derived = [];               // overview: mean of each group, aligned with S.t
let stats = [];                 // per sensor, over the visible window
const win = { i0: 0, n: 0 };
const heroRefs = [], cardRefs = [];
let tableRefs = null;

/* ================= feed ================= */
function onSnapshot(d) {
  applySnapshot(S, d);
  jumpTo(S.t.length ? S.t[S.t.length - 1] : Date.now());
  conn = 'ok';
  rebuild();
}

function onTick(d) {
  const result = applyTick(S, d, groups, derived);
  if (result === 'resync') { reconnect(); return; }   // missed a snapshot: start over
  if (result === 'stale') return;
  slideTo(d.t);
  if (result === 'structural') rebuild();
  else { refreshText(); requestRender(); }
}

let es = null;
function connect() {
  es = new EventSource('/api/stream');
  es.addEventListener('snapshot', e => onSnapshot(JSON.parse(e.data)));
  es.addEventListener('tick', e => onTick(JSON.parse(e.data)));
  es.addEventListener('config', e => { S.interval = JSON.parse(e.data).interval; renderSegs(); renderLive(); });
  es.onerror = () => {
    if (conn === 'ok' || conn === 'connecting') conn = 'lost';
    renderLive();
    // EventSource retries by itself, except after an HTTP error: then it is closed for good.
    if (es.readyState === EventSource.CLOSED) setTimeout(reconnect, 2000);
  };
}
function reconnect() {
  es.close();
  connect();
}

async function postAction(path, body) {
  const r = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Thermonitor': '1' },
    body: body == null ? null : JSON.stringify(body),
  });
  return r.json();
}

/* ================= derived data ================= */
function computeStats() {
  win.n = S.t.length;
  win.i0 = lowerBound(S.t, win.n ? S.t[win.n - 1] - ui.win : 0);
  stats = S.v.map(a => statsOf(a, win.i0, win.n));
}
const statsFor = a => statsOf(a, win.i0, win.n);

/* ================= DOM ================= */
function rebuild() {
  readColors();
  groups = buildGroups(S.sensors, S.alive);
  derived = buildDerived(S, groups);
  computeStats();
  clearCharts();
  ui.hoverT = null;
  ui.hoverSrc = null;
  heroRefs.length = 0;
  cardRefs.length = 0;
  tableRefs = null;
  renderBanners(); renderLive(); renderSegs(); renderFooter();

  const content = $('#content');
  content.textContent = '';
  if (conn === 'connecting' || S.state === 'init' || (S.state === 'ready' && !S.t.length)) {
    content.append(h('div', 'empty', '<div class="spinner"></div>Lecture des capteurs…'));
    return;
  }
  if (S.state === 'error') {
    content.append(h('div', 'empty', `Impossible de lire les capteurs.<br><small>${esc(S.error || '')}</small>`));
    return;
  }
  if (!groups.length) {
    content.append(h('div', 'empty', 'Aucun capteur de température lisible pour l’instant.'));
    return;
  }

  const heroes = h('section', 'heroes');
  heroes.setAttribute('aria-label', 'Résumé');
  for (const g of groups) heroes.append(buildHero(g));
  content.append(heroes);

  if (ui.view === 'table') content.append(buildTable());
  else {
    if (groups.length > 1) content.append(buildOverview());
    const cards = h('section', 'cards');
    for (const g of groups) cards.append(buildCard(g));
    content.append(cards);
  }
  refreshText();
  requestRender();
}

function buildHero(g) {
  const el = h('article', 'tile');
  const ps = S.sensors[g.primary];
  el.innerHTML = `
    <div class="tile-head"><span>${svg(KIND[g.kind].icon)}</span><span class="name" title="${esc(g.name)}">${esc(roleLabel(g))}</span><span class="pill"></span></div>
    <div class="tile-value"><span class="int">—</span><span class="dec"></span><span class="unit">°C</span></div>
    <div class="tile-sub"><span class="sensor" title="${esc(ps.name)}">${esc(ps.name)}</span><span>min <b class="mn">—</b></span><span>max <b class="mx">—</b></span></div>
    <div class="gauge" aria-hidden="true"><i></i></div>
    <div class="spark-host"></div>`;
  new Spark($('.spark-host', el), g.primary);
  heroRefs.push({ g, int: $('.int', el), dec: $('.dec', el), mn: $('.mn', el), mx: $('.mx', el), pill: $('.pill', el), gauge: $('.gauge > i', el) });
  return el;
}

function buildOverview() {
  const sec = h('section', 'card overview');
  sec.innerHTML = `
    <header class="card-head">
      <span class="hw-icon">${svg('temp')}</span>
      <div class="card-title"><h2>Vue d’ensemble</h2><p>Température moyenne de chaque composant (moyenne de ses capteurs)</p></div>
    </header>`;
  const items = groups.map((g, k) => ({ id: 'avg:' + g.id, name: roleLabel(g), slot: k < 8 ? k : -1, data: derived[k] }));
  new LineChart(sec, items.filter(s => s.slot >= 0), 240, 'vue d’ensemble');
  const ref = { g: null, pill: null, rows: [] };
  sec.append(buildLegend(items, ref.rows, 'Composant'));
  cardRefs.push(ref);
  return sec;
}

function buildCard(g) {
  const sec = h('section', 'card' + (g.cores.length ? ' wide' : ''));
  const count = g.lines.length + g.cores.length;
  const subs = [...new Set(g.all.map(i => S.sensors[i].sub).filter(Boolean))];
  sec.innerHTML = `
    <header class="card-head">
      <span class="hw-icon">${svg(KIND[g.kind].icon)}</span>
      <div class="card-title"><h2 title="${esc(g.name)}">${esc(g.name)}</h2>
        <p>${KIND[g.kind].label} · ${count} capteur${count > 1 ? 's' : ''}${subs.length ? ' · ' + esc(subs.join(', ')) : ''}</p></div>
      <div class="card-now"><span class="pill"></span></div>
    </header>`;
  const ref = { g, pill: $('.pill', sec), rows: [] };
  const items = g.lines.map(i => ({ id: S.sensors[i].id, name: S.sensors[i].name, sub: S.sensors[i].sub, slot: g.slot.get(i), data: S.v[i] }));
  const plotted = items.filter(s => s.slot >= 0);
  const chart = plotted.length ? new LineChart(sec, plotted, g.cores.length ? 220 : 190, g.name) : null;
  if (g.cores.length) {
    const box = h('div', 'cores');
    box.innerHTML = `<div class="cores-head"><span>Cœurs <em>· max par intervalle</em></span>
      <div class="scale" role="img" aria-label="Échelle de couleur : bleu 25 °C, rouge 100 °C"><span>25°</span><i></i><span>100°</span></div></div>`;
    sec.append(box);
    new CoreMap(box, g.cores, chart);
  }
  if (items.length) sec.append(buildLegend(items, ref.rows, 'Capteur'));
  cardRefs.push(ref);
  return sec;
}

/** Legend rows double as a small table (now / min / max) and toggle their curve. */
function buildLegend(items, rowsOut, heading) {
  const lg = h('div', 'legend');
  lg.append(h('div', 'lg-head', `<span></span><span>${heading}</span><span>Actuel</span><span title="Sur la fenêtre affichée">Min</span><span title="Sur la fenêtre affichée">Max</span>`));
  let unplotted = 0;
  for (const s of items) {
    const b = h('button', 'lg-row');
    b.type = 'button';
    if (s.slot < 0) { b.disabled = true; unplotted++; }
    else {
      b.style.setProperty('--c', `var(--s${s.slot + 1})`);
      b.setAttribute('aria-pressed', String(!ui.hidden.has(s.id)));
      b.title = 'Afficher / masquer la courbe';
      b.addEventListener('click', () => {
        if (ui.hidden.has(s.id)) ui.hidden.delete(s.id); else ui.hidden.add(s.id);
        storage.set('hidden', [...ui.hidden]);
        b.setAttribute('aria-pressed', String(!ui.hidden.has(s.id)));
        requestRender();
      });
    }
    b.innerHTML = `<i class="sw"${s.slot < 0 ? ' style="visibility:hidden"' : ''}></i>`
      + `<span class="nm" title="${esc(s.name)}">${esc(s.name)}${s.sub ? ` <span class="sub">· ${esc(s.sub)}</span>` : ''}</span>`
      + '<span class="v now">—</span><span class="v mn">—</span><span class="v mx">—</span>';
    rowsOut.push({ data: s.data, now: $('.now', b), mn: $('.mn', b), mx: $('.mx', b) });
    lg.append(b);
  }
  if (unplotted) lg.append(h('div', 'lg-note', `${unplotted} courbe${unplotted > 1 ? 's' : ''} non tracée${unplotted > 1 ? 's' : ''} (8 max par graphique).`));
  return lg;
}

function buildTable() {
  const wrap = h('div', 'table-card');
  const table = h('table');
  table.innerHTML = '<caption class="sr-only">Toutes les températures, sur la fenêtre affichée</caption>'
    + '<thead><tr><th>Capteur</th><th>Actuel</th><th>Min</th><th>Moy.</th><th>Max</th><th>État</th></tr></thead>';
  tableRefs = [];
  for (const g of groups) {
    const tb = h('tbody');
    tb.append(h('tr', 'grp', `<th colspan="6">${esc(g.name)}<span>${KIND[g.kind].label}</span></th>`));
    for (const i of [...g.lines, ...g.cores]) {
      const s = S.sensors[i];
      const tr = h('tr', null, `<td>${esc(s.name)}${s.sub ? ` <span class="sub">· ${esc(s.sub)}</span>` : ''}</td>`
        + '<td class="now"></td><td class="mn"></td><td class="avg"></td><td class="mx"></td><td><span class="pill"></span></td>');
      tableRefs.push({ i, kind: g.kind, now: $('.now', tr), mn: $('.mn', tr), avg: $('.avg', tr), mx: $('.mx', tr), pill: $('.pill', tr) });
      tb.append(tr);
    }
    table.append(tb);
  }
  wrap.append(table);
  return wrap;
}

function setPill(el, st) {
  const cls = 'pill' + (st == null ? '' : ' ' + STATUS[st].cls);
  if (el.className !== cls) el.className = cls;
  const html = st == null ? '' : `<i aria-hidden="true">${STATUS[st].glyph}</i>${STATUS[st].label}`;
  if (el._html !== html) { el.innerHTML = html; el._html = html; }
}

function refreshText() {
  computeStats();
  for (const r of heroRefs) {
    const st = stats[r.g.primary] || {};
    const v = st.now;
    if (v == null) { setText(r.int, '—'); setText(r.dec, ''); }
    else { const s = fmt1(v), cut = s.lastIndexOf(','); setText(r.int, s.slice(0, cut)); setText(r.dec, s.slice(cut)); }
    setText(r.mn, degR(st.min));
    setText(r.mx, degR(st.max));
    setPill(r.pill, statusOf(v, r.g.kind));
    const pct = v == null ? 0 : Math.max(0, Math.min(1, (v - GAUGE_MIN) / (GAUGE_MAX - GAUGE_MIN)));
    r.gauge.style.width = (100 - pct * 100).toFixed(1) + '%';
  }
  for (const r of cardRefs) {
    if (r.g) {
      let hottest = null;
      for (const i of r.g.all) { const v = stats[i] && stats[i].now; if (v != null && (hottest == null || v > hottest)) hottest = v; }
      setPill(r.pill, statusOf(hottest, r.g.kind));
    }
    for (const row of r.rows) {
      const st = statsFor(row.data);
      setText(row.now, degS(st.now)); setText(row.mn, degS(st.min)); setText(row.mx, degS(st.max));
    }
  }
  if (tableRefs) for (const r of tableRefs) {
    const st = stats[r.i] || {};
    setText(r.now, deg1(st.now)); setText(r.mn, deg1(st.min)); setText(r.avg, deg1(st.avg)); setText(r.mx, deg1(st.max));
    setPill(r.pill, statusOf(st.now, r.kind));
  }
  renderLive();
  renderFooter();
}

/* ================= chrome ================= */
const WINDOWS = [[60e3, '1 min'], [300e3, '5 min'], [900e3, '15 min'], [3600e3, '1 h']];
const RATES = [[500, '0,5 s'], [1000, '1 s'], [2000, '2 s']];
const VIEWS = [['charts', 'Graphiques'], ['table', 'Tableau']];

function seg(el, items, current, onPick) {
  if (!el._built) {
    el.innerHTML = items.map(([v, l]) => `<button type="button" data-v="${v}">${l}</button>`).join('');
    el.addEventListener('click', e => { const b = e.target.closest('button'); if (b) onPick(b.dataset.v); });
    el._built = true;
  }
  for (const b of el.children) b.setAttribute('aria-pressed', String(b.dataset.v === String(current)));
}

function renderSegs() {
  seg($('#win'), WINDOWS, ui.win, v => {
    ui.win = +v; storage.set('win', ui.win);
    renderSegs(); refreshText();
    jumpTo(S.t.length ? S.t[S.t.length - 1] : Date.now());
    requestRender();
  });
  seg($('#rate'), RATES, S.interval, async v => {
    try { await postAction('/api/interval', { ms: +v }); }
    catch { toast('Impossible de changer la fréquence'); }
  });
  seg($('#view'), VIEWS, ui.view, v => { ui.view = v; storage.set('view', v); renderSegs(); rebuild(); });
}

function renderLive() {
  const el = $('#live');
  let cls = '', txt;
  if (conn === 'restarting') { cls = 'warn'; txt = 'Relance en administrateur…'; }
  else if (conn === 'dead') { cls = 'bad'; txt = 'Serveur arrêté · relance Thermonitor'; }
  else if (conn === 'lost') { cls = 'bad'; txt = 'Connexion perdue · reconnexion…'; }
  else if (conn === 'connecting') txt = 'Connexion…';
  else if (S.state === 'init') { cls = 'warn'; txt = 'Lecture des capteurs…'; }
  else if (S.state === 'error') { cls = 'bad'; txt = 'Erreur capteurs'; }
  else if (isStale(S, Date.now())) { cls = 'warn'; txt = 'Capteurs muets · données figées'; }
  else {
    cls = 'ok';
    const n = S.alive.filter(Boolean).length;
    txt = `En direct · ${n} capteur${n > 1 ? 's' : ''}${S.admin ? '' : ' · mode limité'}`;
  }
  if (el.className !== 'live ' + cls) el.className = 'live ' + cls;
  setText($('span', el), txt);
}

function renderFooter() {
  setText($('#foot-lib'), 'LibreHardwareMonitor ' + (S.lib || ''));
  setText($('#foot-u'), S.state === 'ready' ? `Lecture des capteurs : ${Math.round(S.u)} ms` : '');
}

function renderBanners() {
  const box = $('#banners');
  box.textContent = '';
  if (conn !== 'ok') return;
  if (!S.admin) {
    const b = h('div', 'banner', `<span class="badge">!</span>
      <div class="txt"><b>Mode limité.</b> Sans droits administrateur, Windows ne donne pas les températures du processeur, de la carte mère ni de la mémoire.</div>`);
    const btn = h('button', 'btn', 'Relancer en administrateur');
    btn.type = 'button';
    btn.addEventListener('click', async () => {
      btn.disabled = true; btn.textContent = 'Valide la demande Windows…';
      try {
        const j = await postAction('/api/elevate');
        if (j.ok) {
          conn = 'restarting'; renderLive(); btn.textContent = 'Relance…';
          // The elevated copy should answer within seconds; if it died, say so instead of waiting forever.
          setTimeout(() => { if (conn === 'restarting') { conn = 'dead'; renderLive(); toast('La version administrateur n’a pas démarré'); } }, RESTART_TIMEOUT_MS);
          return;
        }
        toast('Demande refusée : on reste en mode limité');
      } catch { toast('La relance a échoué'); }
      btn.disabled = false; btn.textContent = 'Relancer en administrateur';
    });
    b.append(btn);
    box.append(b);
  } else if (S.pawnio === false) {
    const cmd = 'winget install namazso.PawnIO';
    const b = h('div', 'banner', `<span class="badge">!</span>
      <div class="txt"><b>Pilote PawnIO absent.</b> LibreHardwareMonitor en a besoin pour lire le processeur et la carte mère. Installe-le puis relance Thermonitor : <code>${cmd}</code></div>`);
    const btn = h('button', 'btn ghost', 'Copier la commande');
    btn.type = 'button';
    btn.addEventListener('click', async () => {
      try { await navigator.clipboard.writeText(cmd); toast('Commande copiée'); } catch { toast(cmd); }
    });
    b.append(btn);
    box.append(b);
  }
}

let toastTimer = 0;
function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), 2600);
}

/* ================= theme ================= */
const darkQuery = matchMedia('(prefers-color-scheme: dark)');
const isDark = () => document.documentElement.dataset.theme ? document.documentElement.dataset.theme === 'dark' : darkQuery.matches;
function applyTheme() {
  if (ui.theme) document.documentElement.dataset.theme = ui.theme;
  else delete document.documentElement.dataset.theme;
  readColors();
  $('#theme').innerHTML = svg(isDark() ? 'sun' : 'moon');
  requestRender();
}
$('#theme').addEventListener('click', () => {
  ui.theme = isDark() ? 'light' : 'dark';
  storage.set('theme', ui.theme);
  applyTheme();
});
darkQuery.addEventListener('change', applyTheme);

/* ================= go ================= */
applyTheme();
renderSegs();
connect();
setInterval(renderLive, 1000);   // notices a feed that went quiet
