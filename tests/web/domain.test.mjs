import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import {
  lowerBound, nearestIndex, lastValid, kindOf, statusOf, prio, coreLabel, coreKey, shortName, roleLabel,
  buildGroups, statsOf, meanAt, heat, niceRange, timeStep, tracePath, bucketMax, spreadLabels,
  deg1, degS, degR, clock, esc,
} from '../../src/Thermonitor/wwwroot/js/domain.js';

const jsDir = fileURLToPath(new URL('../../src/Thermonitor/wwwroot/js/', import.meta.url));

test('every page module parses', () => {
  for (const f of readdirSync(jsDir).filter(f => f.endsWith('.js'))) execFileSync(process.execPath, ['--check', jsDir + f]);
});

test('formatting uses French decimals and a dash when empty', () => {
  assert.equal(deg1(42.26), '42,3 °C');
  assert.equal(degS(40), '40,0°');
  assert.equal(degR(39.6), '40°');
  assert.equal(deg1(null), '—');
  assert.equal(esc('<a href="x">&\''), '&lt;a href=&quot;x&quot;&gt;&amp;&#39;');
  assert.match(clock(Date.UTC(2026, 0, 1, 12, 3, 4), true), /^\d\d:03:04$/);
  assert.match(clock(Date.UTC(2026, 0, 1, 12, 3, 4), false), /^\d\d:03$/);
});

test('lowerBound / nearestIndex / lastValid', () => {
  const t = [10, 20, 30];
  assert.equal(lowerBound(t, 5), 0);
  assert.equal(lowerBound(t, 20), 1);
  assert.equal(lowerBound(t, 21), 2);
  assert.equal(lowerBound(t, 99), 3);
  assert.equal(nearestIndex([], 5), -1);
  assert.equal(nearestIndex(t, 0), 0);
  assert.equal(nearestIndex(t, 24), 1);
  assert.equal(nearestIndex(t, 26), 2);
  assert.equal(nearestIndex(t, 99), 2);
  assert.equal(lastValid([1, null, null], 3), 0);
  assert.equal(lastValid([null, null], 2), -1);
  assert.equal(lastValid([1, ...Array(10).fill(null)], 11), -1, 'only looks a few samples back');
});

test('kindOf maps LibreHardwareMonitor types', () => {
  assert.equal(kindOf('Cpu'), 'cpu');
  assert.equal(kindOf('GpuNvidia'), 'gpu');
  assert.equal(kindOf('GpuAmd'), 'gpu');
  assert.equal(kindOf('Memory'), 'mem');
  assert.equal(kindOf('Motherboard'), 'board');
  assert.equal(kindOf('SuperIO'), 'board');
  assert.equal(kindOf('Storage'), 'disk');
  assert.equal(kindOf('Psu'), 'other');
});

test('statusOf uses per-device thresholds', () => {
  assert.equal(statusOf(null, 'cpu'), null);
  assert.equal(statusOf(69.9, 'cpu'), 0);
  assert.equal(statusOf(70, 'cpu'), 1);
  assert.equal(statusOf(85, 'cpu'), 2);
  assert.equal(statusOf(95, 'cpu'), 3);
  assert.equal(statusOf(56, 'disk'), 0, 'an NVMe controller at 56 °C is normal');
  assert.equal(statusOf(60, 'disk'), 1);
});

test('sensor names', () => {
  assert.ok(prio('CPU Package') < prio('Core Max'));
  assert.ok(prio('GPU Core') < prio('GPU Memory Junction'));
  assert.equal(prio('Temperature #4'), 99);
  assert.equal(coreLabel('P-Core #3'), 'P3');
  assert.equal(coreLabel('E-Core #12'), 'E12');
  assert.equal(coreLabel('CPU Core #2'), 'C2');
  assert.ok(coreKey('P-Core #8') < coreKey('E-Core #1'));
  assert.ok(coreKey('E-Core #2') < coreKey('E-Core #10'));
  assert.equal(shortName('CPU Package'), 'Package');
  assert.equal(shortName('GPU Hot Spot'), 'Hot Spot');
  assert.equal(roleLabel({ kind: 'gpu', name: 'RTX' }), 'GPU');
  assert.equal(roleLabel({ kind: 'disk', name: 'Samsung 990' }), 'Samsung 990');
});

const s = (hw, hwName, hwType, name, index = 0) => ({ id: `${hw}/${name}`, hw, hwName, hwType, name, index, sub: null });

test('buildGroups: one group per device, memory sticks merged, dead sensors left out', () => {
  const sensors = [
    s('/nvme/0', 'Disk B', 'Storage', 'Temperature'),
    s('/cpu', 'CPU', 'Cpu', 'Core Max'),
    s('/cpu', 'CPU', 'Cpu', 'CPU Package'),
    s('/dimm/1', 'Stick', 'Memory', 'DIMM #1'),
    s('/dimm/3', 'Stick #2', 'Memory', 'DIMM #3'),
    s('/gpu', 'GPU', 'GpuNvidia', 'GPU Core'),
    s('/nvme/1', 'Disk A', 'Storage', 'Temperature'),
    s('/mb', 'Board', 'Motherboard', 'Temperature #4'),
  ];
  const alive = [true, true, true, true, true, true, true, false];

  const groups = buildGroups(sensors, alive);

  assert.deepEqual(groups.map(g => g.kind), ['cpu', 'gpu', 'mem', 'disk', 'disk']);
  assert.deepEqual(groups.slice(3).map(g => g.name), ['Disk A', 'Disk B'], 'same kind: by name');
  const cpu = groups[0];
  assert.deepEqual(cpu.lines.map(i => sensors[i].name), ['CPU Package', 'Core Max'], 'most representative first');
  assert.equal(cpu.primary, 2);
  assert.equal(cpu.slot.get(2), 0);
  const mem = groups[2];
  assert.equal(mem.all.length, 2);
  assert.equal(mem.name, 'Mémoire vive · 2 barrettes');
});

test('buildGroups: 3+ per-core sensors go to the heatmap, P before E', () => {
  const sensors = [
    s('/cpu', 'CPU', 'Cpu', 'E-Core #1'), s('/cpu', 'CPU', 'Cpu', 'P-Core #2'),
    s('/cpu', 'CPU', 'Cpu', 'P-Core #1'), s('/cpu', 'CPU', 'Cpu', 'Core Average'),
  ];
  const [cpu] = buildGroups(sensors, sensors.map(() => true));
  assert.deepEqual(cpu.cores.map(i => sensors[i].name), ['P-Core #1', 'P-Core #2', 'E-Core #1']);
  assert.deepEqual(cpu.lines.map(i => sensors[i].name), ['Core Average']);
});

test('buildGroups: two cores stay plain lines; colors stop at 8', () => {
  const two = [s('/cpu', 'CPU', 'Cpu', 'Core #1'), s('/cpu', 'CPU', 'Cpu', 'Core #2')];
  assert.equal(buildGroups(two, [true, true])[0].cores.length, 0);

  const many = Array.from({ length: 10 }, (_, i) => s('/mb', 'Board', 'Motherboard', `Temperature #${i}`, i));
  const [board] = buildGroups(many, many.map(() => true));
  assert.deepEqual([...board.slot.values()], [0, 1, 2, 3, 4, 5, 6, 7, -1, -1]);
});

test('statsOf ignores nulls and looks at the window only', () => {
  const a = [99, 40, null, 44, 42];
  assert.deepEqual(statsOf(a, 1, 5), { now: 42, min: 40, max: 44, avg: 42 });
  assert.deepEqual(statsOf([null, null], 0, 2), { now: null, min: null, max: null, avg: null });
  assert.equal(statsOf([41, 43, null], 0, 3).now, 43, 'now falls back to the last value seen');
});

test('meanAt averages the present readings', () => {
  const v = [[40, null], [50, 60], [null, null]];
  assert.equal(meanAt(v, [0, 1, 2], 0), 45);
  assert.equal(meanAt(v, [0, 1], 1), 60);
  assert.equal(meanAt(v, [2], 0), null);
  assert.equal(meanAt([[1], [2], [2]], [0, 1, 2], 0), 1.7);
});

test('heat ramp: clamped ends and interpolated middle', () => {
  assert.equal(heat(-10), 'rgb(24,79,149)');
  assert.equal(heat(25), 'rgb(24,79,149)');
  assert.equal(heat(100), 'rgb(227,73,72)');
  assert.equal(heat(500), 'rgb(227,73,72)');
  assert.equal(heat(62.5), 'rgb(132,168,61)', 'halfway between aqua (55) and yellow (70)');
});

test('niceRange: at least 20° tall, 5° aligned, never negative', () => {
  assert.deepEqual(niceRange(44, 46), { lo: 35, hi: 55, step: 5 });
  assert.deepEqual(niceRange(30, 90), { lo: 20, hi: 100, step: 20 });
  assert.equal(niceRange(1, 3).lo, 0);
  assert.deepEqual(niceRange(Infinity, -Infinity), { lo: 30, hi: 60, step: 5 }, 'no data: a default range');
  for (const [lo, hi] of [[20, 21], [33, 71], [50, 99]]) {
    const r = niceRange(lo, hi);
    assert.ok(r.lo <= lo && r.hi >= hi && r.hi - r.lo >= 20, `${lo}..${hi} -> ${r.lo}..${r.hi}`);
    assert.equal(r.lo % r.step, 0);
  }
});

test('timeStep: about one label per 105 px', () => {
  assert.equal(timeStep(60e3, 600), 15e3);
  assert.equal(timeStep(300e3, 600), 60e3);
  assert.equal(timeStep(3600e3, 1200), 600e3);
});

function recorder() {
  const ops = [];
  return { ops, moveTo: (x, y) => ops.push(['M', x, y]), lineTo: (x, y) => ops.push(['L', x, y]) };
}

test('tracePath breaks the line on nulls and on time gaps', () => {
  const p = recorder();
  const times = [0, 1000, 2000, 3000, 20000, 21000];
  const vals = [1, 2, null, 4, 5, 6];
  const r = tracePath(p, times, vals, 0, 6, t => t / 1000, v => v, 1000, 5000);
  assert.equal(r.segs, 3);
  assert.deepEqual(p.ops.map(o => o[0]).join(''), 'MLMML');
  assert.equal(r.firstX, 0);
  assert.equal(r.lastX, 21);
});

test('tracePath keeps peaks when there are more points than pixels', () => {
  const p = recorder();
  const n = 100, times = Array.from({ length: n }, (_, k) => k), vals = times.map(k => k === 50 ? 99 : 10);
  tracePath(p, times, vals, 0, n, t => Math.floor(t / 10), v => v, 10, 1e9);
  assert.ok(p.ops.length < n, 'decimated');
  assert.ok(p.ops.some(o => o[2] === 99), 'the spike survives');
});

test('bucketMax groups by absolute time', () => {
  const times = [1000, 1500, 2000, 2500, 4000];
  const vals = [1, 5, 3, null, 2];
  assert.deepEqual(bucketMax(times, vals, 0, 5, 1000), [{ b: 1, max: 5 }, { b: 2, max: 3 }, { b: 4, max: 2 }]);
  assert.deepEqual(bucketMax(times, vals, 3, 4, 1000), [], 'a bucket of nulls is skipped');
});

test('spreadLabels keeps labels apart, in bounds, in input order', () => {
  const out = spreadLabels([50, 52, 10], 16, 0, 100);
  assert.equal(out[2], 10);
  assert.equal(out[0], 50);
  assert.equal(out[1], 66);
  const crowded = spreadLabels([95, 96, 97], 16, 0, 100);
  assert.deepEqual(crowded, [68, 84, 100]);
});
