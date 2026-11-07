import { test } from 'node:test';
import assert from 'node:assert/strict';
import { applySnapshot, applyTick, buildDerived, isStale, HISTORY_MS } from '../../src/Thermonitor/wwwroot/js/feed.js';
import { buildGroups } from '../../src/Thermonitor/wwwroot/js/domain.js';

const sensors = [
  { id: '/cpu/a', name: 'CPU Package', hw: '/cpu', hwName: 'CPU', hwType: 'Cpu', sub: null, index: 0 },
  { id: '/cpu/b', name: 'Core Max', hw: '/cpu', hwName: 'CPU', hwType: 'Cpu', sub: null, index: 1 },
  { id: '/gpu/a', name: 'GPU Core', hw: '/gpu', hwName: 'GPU', hwType: 'GpuNvidia', sub: null, index: 0 },
];
const snapshot = (t, v) => ({ state: 'ready', admin: true, pawnio: true, interval: 1000, u: 5, lib: 'x', sensors, t, v });

function setup(t = [1000, 2000], v = [[40, 42], [44, 46], [null, null]]) {
  const S = {};
  applySnapshot(S, snapshot(t, v));
  const groups = buildGroups(S.sensors, S.alive);
  return { S, groups, derived: buildDerived(S, groups) };
}

test('snapshot marks sensors that never answered as dead', () => {
  const { S, groups, derived } = setup();
  assert.deepEqual(S.alive, [true, true, false]);
  assert.equal(groups.length, 1, 'the GPU has no reading yet: no group');
  assert.deepEqual(derived, [[42, 44]], 'mean of the CPU sensors per sample');
});

test('a tick appends to every series, the overview included', () => {
  const { S, groups, derived } = setup();
  assert.equal(applyTick(S, { t: 3000, u: 7, v: [41, 45, null] }, groups, derived), 'ok');
  assert.deepEqual(S.t, [1000, 2000, 3000]);
  assert.deepEqual(S.v[0], [40, 42, 41]);
  assert.deepEqual(derived[0], [42, 44, 43]);
  assert.equal(S.u, 7);
});

test('a first reading from a dead sensor asks for a rebuild', () => {
  const { S, groups, derived } = setup();
  assert.equal(applyTick(S, { t: 3000, u: 7, v: [41, 45, 50] }, groups, derived), 'structural');
  assert.equal(S.alive[2], true);
  const rebuilt = buildDerived(S, buildGroups(S.sensors, S.alive));
  assert.equal(rebuilt.length, 2);
  assert.ok(rebuilt.every(a => a.length === S.t.length), 'rebuilt overview is aligned with the samples');
});

test('a tick for another sensor list asks for a resync and changes nothing', () => {
  const { S, groups, derived } = setup();
  assert.equal(applyTick(S, { t: 3000, u: 7, v: [41, 45] }, groups, derived), 'resync');
  assert.equal(applyTick(S, { t: 3000, u: 7 }, groups, derived), 'resync');
  assert.deepEqual(S.t, [1000, 2000]);
});

test('an out-of-order tick is ignored', () => {
  const { S, groups, derived } = setup();
  assert.equal(applyTick(S, { t: 2000, u: 7, v: [1, 1, 1] }, groups, derived), 'stale');
  assert.deepEqual(S.t, [1000, 2000]);
});

test('samples older than an hour are trimmed from every series at once', () => {
  const n = 100, t = Array.from({ length: n }, (_, k) => k * 1000);
  const { S, groups, derived } = setup(t, [t.map(() => 40), t.map(() => 50), t.map(() => null)]);
  const now = HISTORY_MS + 80 * 1000;   // the first 80 samples are now too old
  applyTick(S, { t: now, u: 1, v: [40, 50, null] }, groups, derived);
  assert.equal(S.t[0], 80000);
  assert.equal(S.t.length, 21);
  assert.ok(S.v.every(a => a.length === S.t.length));
  assert.ok(derived.every(a => a.length === S.t.length));
});

test('isStale: only when ready and nothing came for over 3 intervals', () => {
  const { S } = setup();
  assert.equal(isStale(S, 2000 + 4000), false);
  assert.equal(isStale(S, 2000 + 5001), true);
  S.state = 'init';
  assert.equal(isStale(S, 99999), false);
});
