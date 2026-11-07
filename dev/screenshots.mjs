// README screenshots: drives headless Chrome over the DevTools protocol (no dependencies).
//   node dev/screenshots.mjs [url]       default url: http://localhost:8787/ (a running Thermonitor)
// Writes docs/screenshots/*.png. Works against the mock server too (node dev/mock-server.mjs).
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const url = process.argv[2] || 'http://localhost:8787/';
const outDir = fileURLToPath(new URL('../docs/screenshots/', import.meta.url));
const PORT = 9333;
const VIEWPORT = { width: 1440, height: 1000, deviceScaleFactor: 1.5, mobile: false };

// Each shot: page preferences, then the area covered by these selectors (union of their boxes).
const SHOTS = [
  { name: 'dashboard', prefs: { theme: 'dark', view: 'charts', win: 300000 }, area: ['.top', '.heroes', '.overview'] },
  { name: 'cpu-cores', prefs: { theme: 'dark', view: 'charts', win: 300000 }, area: ['.card.wide'] },
  { name: 'dashboard-light', prefs: { theme: 'light', view: 'charts', win: 300000 }, area: ['.top', '.heroes', '.overview'] },
  { name: 'table', prefs: { theme: 'dark', view: 'table', win: 300000 }, area: ['.top', '.heroes', '.table-card'], maxHeight: 1250 },
];

function findBrowser() {
  const local = process.env.LOCALAPPDATA || '';
  const candidates = [
    process.env.CHROME,
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    join(local, 'Google/Chrome/Application/chrome.exe'),
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
  ];
  const found = candidates.find(p => p && existsSync(p));
  if (!found) throw new Error('Chrome or Edge not found (set CHROME=<path to chrome.exe>)');
  return found;
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function waitForDebugger() {
  for (let i = 0; i < 50; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
      const page = targets.find(t => t.type === 'page');
      if (page) return page.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await sleep(200);
  }
  throw new Error('headless browser did not start');
}

async function connect(wsUrl) {
  const ws = new WebSocket(wsUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let nextId = 0;
  const pending = new Map();
  ws.onmessage = e => {
    const msg = JSON.parse(e.data);
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.error) p.reject(new Error(msg.error.message)); else p.resolve(msg.result);
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });
  return { send, close: () => ws.close() };
}

async function shoot(cdp, shot) {
  const prefs = Object.entries(shot.prefs)
    .map(([k, v]) => `localStorage.setItem('thermonitor.${k}', ${JSON.stringify(JSON.stringify(v))});`).join('');
  const { identifier } = await cdp.send('Page.addScriptToEvaluateOnNewDocument', { source: prefs });
  await cdp.send('Page.navigate', { url });
  await sleep(4000);   // snapshot, first ticks, chart animation
  await cdp.send('Page.removeScriptToEvaluateOnNewDocument', { identifier });

  const { result } = await cdp.send('Runtime.evaluate', {
    returnByValue: true,
    expression: `(() => {
      const boxes = ${JSON.stringify(shot.area)}.map(s => document.querySelector(s)?.getBoundingClientRect()).filter(Boolean);
      if (!boxes.length) return null;
      const top = Math.min(...boxes.map(b => b.top)) + scrollY, bottom = Math.max(...boxes.map(b => b.bottom)) + scrollY;
      return { x: 0, y: Math.max(0, top - 0), width: document.documentElement.clientWidth, height: bottom - top + 20 };
    })()`,
  });
  const clip = result.value;
  if (!clip) throw new Error(`${shot.name}: nothing matches ${shot.area.join(', ')}`);
  if (shot.maxHeight) clip.height = Math.min(clip.height, shot.maxHeight);
  // Header is sticky: scroll to the top so it sits where the clip expects it.
  await cdp.send('Runtime.evaluate', { expression: 'scrollTo(0, 0)' });
  const { data } = await cdp.send('Page.captureScreenshot', { format: 'png', clip: { ...clip, scale: 1 }, captureBeyondViewport: true });
  const file = join(outDir, shot.name + '.png');
  writeFileSync(file, Buffer.from(data, 'base64'));
  console.log(`${file}  ${Math.round(clip.width)}x${Math.round(clip.height)} css px`);
}

const profile = mkdtempSync(join(tmpdir(), 'thermonitor-shots-'));
const browser = spawn(findBrowser(), [
  '--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
  '--hide-scrollbars', '--no-first-run', '--no-default-browser-check', 'about:blank',
], { stdio: 'ignore' });
try {
  mkdirSync(outDir, { recursive: true });
  const cdp = await connect(await waitForDebugger());
  await cdp.send('Page.enable');
  await cdp.send('Emulation.setDeviceMetricsOverride', VIEWPORT);
  for (const shot of SHOTS) await shoot(cdp, shot);
  cdp.close();
} finally {
  browser.kill();
  await sleep(500);
  rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
