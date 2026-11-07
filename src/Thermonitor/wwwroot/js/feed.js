// How the page's data follows the server feed. Pure: works on plain objects, tested under Node.

import { lowerBound, meanAt } from './domain.js';

export const HISTORY_MS = 3600000;
const TRIM_BATCH = 60;   // trim old samples in batches, not one by one

/** Replaces the data with a snapshot. */
export function applySnapshot(S, d) {
  Object.assign(S, {
    sensors: d.sensors, t: d.t, v: d.v, admin: d.admin, pawnio: d.pawnio, interval: d.interval,
    state: d.state, error: d.error ?? null, u: d.u, lib: d.lib,
  });
  S.alive = S.v.map(a => a.some(x => x != null));
}

/** Overview series: the mean of each group's sensors at every sample. */
export const buildDerived = (S, groups) => groups.map(g => S.t.map((_, k) => meanAt(S.v, g.all, k)));

/**
 * Appends one tick in place (S, and the overview series in `derived`).
 * Returns 'resync' when the tick does not match the sensor list (ask for a fresh snapshot),
 * 'structural' when a sensor got its first reading (groups must be rebuilt, derived is left stale),
 * 'stale' for an out-of-order tick (ignored), else 'ok'.
 */
export function applyTick(S, d, groups, derived) {
  if (!Array.isArray(d.v) || d.v.length !== S.sensors.length) return 'resync';
  if (S.t.length && d.t <= S.t[S.t.length - 1]) return 'stale';
  S.t.push(d.t);
  S.u = d.u;
  let structural = false;
  for (let i = 0; i < d.v.length; i++) {
    const x = d.v[i];
    S.v[i].push(x);
    if (x != null && !S.alive[i]) { S.alive[i] = true; structural = true; }
  }
  if (!structural) groups.forEach((g, k) => derived[k].push(meanAt(S.v, g.all, S.t.length - 1)));
  const cut = lowerBound(S.t, d.t - HISTORY_MS);
  if (cut >= TRIM_BATCH) {
    S.t.splice(0, cut);
    for (const a of S.v) a.splice(0, cut);
    if (!structural) for (const a of derived) a.splice(0, cut);
  }
  return structural ? 'structural' : 'ok';
}

/** True when no reading arrived for a while (server alive but the sensors stopped answering). */
export const isStale = (S, now) => S.state === 'ready' && S.t.length > 0 && now - S.t[S.t.length - 1] > S.interval * 3 + 2000;
