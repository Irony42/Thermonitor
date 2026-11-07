// UI playground: serves wwwroot with simulated sensors (no admin, no .NET).
//   node dev/mock-server.mjs [port] [--limited]
//   --limited  behaves like a server without admin rights (CPU/board sensors missing, elevate banner)
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, normalize, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const port = +(args.find(a => /^\d+$/.test(a)) || 8788);
const limited = args.includes('--limited');
const root = fileURLToPath(new URL('../src/Thermonitor/wwwroot/', import.meta.url));
const TYPES = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8' };

const sensors = [];
const add = (hw, hwName, hwType, name, base, swing, sub = null) =>
  sensors.push({ id: `/${hw}/temperature/${sensors.length}`, name, hw: `/${hw}`, hwName, hwType, sub, index: sensors.length, base, swing });

add('intelcpu/0', 'Intel Core i7-13700KF', 'Cpu', 'CPU Package', 46, 30);
add('intelcpu/0', 'Intel Core i7-13700KF', 'Cpu', 'Core Max', 48, 32);
add('intelcpu/0', 'Intel Core i7-13700KF', 'Cpu', 'Core Average', 40, 24);
for (let i = 1; i <= 8; i++) add('intelcpu/0', 'Intel Core i7-13700KF', 'Cpu', `P-Core #${i}`, 42 + i % 3, 30);
for (let i = 1; i <= 8; i++) add('intelcpu/0', 'Intel Core i7-13700KF', 'Cpu', `E-Core #${i}`, 38 + i % 2, 22);
add('gpu-nvidia/0', 'NVIDIA GeForce RTX 4070 Ti', 'GpuNvidia', 'GPU Core', 41, 26);
add('gpu-nvidia/0', 'NVIDIA GeForce RTX 4070 Ti', 'GpuNvidia', 'GPU Hot Spot', 50, 30);
add('gpu-nvidia/0', 'NVIDIA GeForce RTX 4070 Ti', 'GpuNvidia', 'GPU Memory Junction', 52, 18);
add('memory/dimm/0', 'G.Skill F5-6000J3038F16G', 'Memory', 'DIMM #1', 38, 6);
add('memory/dimm/1', 'G.Skill F5-6000J3038F16G #2', 'Memory', 'DIMM #2', 39, 6);
add('motherboard', 'MSI MAG Z790 TOMAHAWK', 'Motherboard', 'CPU', 44, 18, 'Nuvoton NCT6687D');
add('motherboard', 'MSI MAG Z790 TOMAHAWK', 'Motherboard', 'System', 33, 4, 'Nuvoton NCT6687D');
add('motherboard', 'MSI MAG Z790 TOMAHAWK', 'Motherboard', 'VRM MOS', 41, 12, 'Nuvoton NCT6687D');
add('motherboard', 'MSI MAG Z790 TOMAHAWK', 'Motherboard', 'PCH', 47, 3, 'Nuvoton NCT6687D');
add('motherboard', 'MSI MAG Z790 TOMAHAWK', 'Motherboard', 'Temperature #5', null, 0, 'Nuvoton NCT6687D');
add('nvme/0', 'Samsung SSD 990 PRO 2TB', 'Storage', 'Temperature', 39, 10);
add('nvme/0', 'Samsung SSD 990 PRO 2TB', 'Storage', 'Temperature 2', 47, 14);
add('nvme/1', 'WD_BLACK SN850X 1000GB', 'Storage', 'Temperature', 36, 8);

let interval = 1000;
const state = sensors.map(() => 0);
// Bursts of "work" every ~90 s, plus a slow drift.
const load = t => { const p = (t / 1000) % 90; return p > 55 && p < 80 ? 1 : 0.12 + 0.08 * Math.sin(t / 23000); };
const needsAdmin = s => ['Cpu', 'Motherboard', 'Memory'].includes(s.hwType);
function sample(t) {
  const L = load(t);
  return sensors.map((s, i) => {
    if (s.base == null || (limited && needsAdmin(s))) return null;
    const target = s.base + s.swing * L;
    state[i] = state[i] ? state[i] + (target - state[i]) * 0.18 + (Math.random() - 0.5) * 1.6 : target;
    return Math.round(state[i] * 10) / 10;
  });
}

const history = { t: [], rows: [] };
const t0 = Date.now();
for (let k = 900; k > 0; k--) { const t = t0 - k * 1000; history.t.push(t); history.rows.push(sample(t)); }

const clients = new Set();
const frame = (evt, obj) => `event: ${evt}\ndata: ${JSON.stringify(obj)}\n\n`;
const snapshot = () => frame('snapshot', {
  state: 'ready', admin: !limited, pawnio: true, interval, u: 14.2, lib: '0.9.6 (simulé)',
  sensors: sensors.map(({ base, swing, ...s }) => s),
  t: history.t, v: sensors.map((_, i) => history.rows.map(r => r[i])),
});
let timer;
function schedule() {
  clearInterval(timer);
  timer = setInterval(() => {
    const t = Date.now(), row = sample(t);
    history.t.push(t); history.rows.push(row);
    if (history.t.length > 7200) { history.t.shift(); history.rows.shift(); }
    const msg = frame('tick', { t, u: 10 + Math.random() * 8, v: row });
    for (const res of clients) res.write(msg);
  }, interval);
}
schedule();

createServer(async (req, res) => {
  const path = new URL(req.url, 'http://x').pathname;
  if (path === '/api/stream') {
    res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store' });
    res.write('retry: 1200\n\n' + snapshot());
    clients.add(res);
    req.on('close', () => clients.delete(res));
  } else if (req.method === 'POST' && req.headers['x-thermonitor'] !== '1') {
    res.writeHead(403).end();   // same guard as the real server
  } else if (path === '/api/interval' && req.method === 'POST') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      interval = Math.min(5000, Math.max(250, JSON.parse(body).ms));
      schedule();
      for (const c of clients) c.write(frame('config', { interval }));
      res.writeHead(200, { 'Content-Type': 'application/json' }).end('{"ok":true}');
    });
  } else if (path === '/api/elevate' && req.method === 'POST') {
    res.writeHead(200, { 'Content-Type': 'application/json' }).end(limited ? '{"ok":false}' : '{"ok":true,"already":true}');
  } else {
    const file = normalize(root + (path === '/' ? 'index.html' : path.slice(1)));
    if (!file.startsWith(root.replace(/[\\/]$/, '') + sep) || !TYPES[extname(file)]) { res.writeHead(404).end(); return; }
    try {
      const body = await readFile(file);
      res.writeHead(200, { 'Content-Type': TYPES[extname(file)], 'Cache-Control': 'no-store' }).end(body);
    } catch { res.writeHead(404).end(); }
  }
}).listen(port, '127.0.0.1', () => console.log(`mock Thermonitor on http://localhost:${port}/`));
