#!/usr/bin/env node
/**
 * Headless balance/determinism harness for the shared combat spec.
 * Reads the exact same JSON as Unity, runs the JS reference sim in preview/sim.mjs,
 * and prints a machine-readable report to tools/out/harness-report.json.
 *
 * Run: node tools/sim_harness.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { BP, cellToWorld, makeRegistry, makeSetup, runBattle, Rng } from '../preview/sim.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const dataDir = path.join(root, 'Assets', 'Resources', 'Data');
const read = (name) => JSON.parse(fs.readFileSync(path.join(dataDir, `${name}.json`), 'utf8'));
const raw = Object.fromEntries(
  ['config', 'buildings', 'races', 'units', 'heroes', 'skills', 'gear', 'dragons', 'world'].map((n) => [n, read(n)]));

// Inert test dummies with huge HP so damage measurements are not cut short by deaths.
const dummy = (id, role) => ({
  id, race: 'human', name: id, role, tier: 1, hp: 1000000, attack: 1, defense: 0, magicResistBp: 0,
  damageType: 'physical', range: 100, attackIntervalTicks: 1000, moveSpeed: 0, supply: 1,
  stackSize: 1, cost: { gold: 0, wood: 0, mana: 0 }, ammo: 0, tags: ['mechanical'], special: 'test dummy',
});
raw.units.items.push(dummy('dummy', 'heavy'), dummy('dummy_line', 'line'));
const reg = makeRegistry(raw);

const results = [];
function check(name, fn) {
  try {
    const r = fn();
    results.push({ name, passed: !!r.passed, detail: r.detail, numbers: r.numbers ?? {} });
  } catch (e) {
    results.push({ name, passed: false, detail: `EXCEPTION ${e.name}: ${e.message}`, numbers: {} });
  }
}

// 1 — Determinism: identical seed + identical cast schedule => identical hash.
check('Deterministic replay (150 ticks + manual cast)', () => {
  const setup = makeSetup(reg, { seed: 4242, command: 10, maxTicks: 600 });
  const casts = [{ tick: 20, hero: 0, skill: 'heroic_leap', x: cellToWorld(10), y: cellToWorld(3) }];
  const a = runBattle(reg, structuredClone(setup), 150, casts);
  const b = runBattle(reg, structuredClone(setup), 150, casts);
  const same = a.hashHex() === b.hashHex() && a.buildReport().stateHash === b.buildReport().stateHash &&
    a.tick === b.tick && a.totalKilled === b.totalKilled;
  return { passed: same, detail: same ? `hash ${a.hashHex()}, ${a.tick} ticks, ${a.totalKilled} kills` : `${a.hashHex()} != ${b.hashHex()}`,
    numbers: { hashA: a.hashHex(), hashB: b.hashHex(), ticks: a.tick, kills: a.totalKilled } };
});

// 2 — Dwarf ammunition: opening burst must out-damage the same army with empty magazines.
check('Dwarf ammo burst > depleted magazines', () => {
  const opts = { seed: 99, attackerUnits: [['dwarf_marksman', 20]], defenderUnits: [['dummy', 1]],
    attackerCells: [[3, 3]], defenderCells: [[5, 3]], heroes: [], maxTicks: 200 };
  const burst = runBattle(reg, makeSetup(reg, opts), 30);
  const dry = runBattle(reg, makeSetup(reg, { ...opts, emptyAmmo: true }), 30);
  const dmgBurst = burst.squads.find((s) => s.unitId === 'dwarf_marksman')?.damageDealt ?? 0;
  const dmgDry = dry.squads.find((s) => s.unitId === 'dwarf_marksman')?.damageDealt ?? 0;
  return { passed: dmgBurst > dmgDry * 1.5, detail: `burst ${dmgBurst} vs dry ${dmgDry} damage in 30 ticks`,
    numbers: { burst: dmgBurst, dry: dmgDry, ratio: dmgDry ? +(dmgBurst / dmgDry).toFixed(2) : null } };
});

// 3 — Lich 50% magic resistance.
check('Lich magic resistance halves spell damage', () => {
  const mk = (defenderUnit) => {
    const setup = makeSetup(reg, {
      seed: 7, attackerUnits: [['dummy', 1]], defenderUnits: [[defenderUnit, 1]],
      attackerCells: [[1, 3]], defenderCells: [[5, 3]], maxTicks: 200,
      heroes: [{ heroId: 'avril', team: 0, cellX: 2, cellY: 3, might: 0, magic: 40,
        stamina: 16, command: 20, hp: 1000, skillIds: ['glacial_shard'] }],
    });
    const casts = [{ tick: 1, hero: 0, skill: 'glacial_shard', x: cellToWorld(5), y: cellToWorld(3) }];
    const b = runBattle(reg, setup, 3, casts);
    const target = b.squads.find((s) => s.team === 1);
    return target.maxHpPool - target.hpPool;
  };
  const human = mk('human_spearman');
  const lich = mk('lich_marauder');
  return { passed: lich < human * 0.75, detail: `human took ${human}, lich took ${lich} from Glacial Shard`,
    numbers: { human, lich } };
});

// 4 — Command keeps morale up and reduces routs.
check('High Command improves morale / reduces routs', () => {
  const mk = (command) => runBattle(reg, makeSetup(reg, { seed: 3131, command, maxTicks: 400 }), 140);
  const low = mk(0), high = mk(70);
  const avg = (b) => b.squads.length ? b.squads.reduce((a, s) => a + s.morale, 0) / b.squads.length : 0;
  const moraleLow = avg(low), moraleHigh = avg(high);
  return { passed: moraleHigh >= moraleLow && high.routedSquads <= low.routedSquads,
    detail: `Command 70: morale ${moraleHigh.toFixed(1)}, ${high.routedSquads} routs | Command 0: morale ${moraleLow.toFixed(1)}, ${low.routedSquads} routs`,
    numbers: { moraleLow: +moraleLow.toFixed(1), moraleHigh: +moraleHigh.toFixed(1), routsLow: low.routedSquads, routsHigh: high.routedSquads } };
});

// 5 — Sylvani rampart bonus against line troops on walls.
check('Sylvani rampart bonus vs wall garrison', () => {
  const mk = (attackerUnit) => {
    const b = runBattle(reg, makeSetup(reg, {
      seed: 515, siege: true, wallHp: 0,
      attackerUnits: [[attackerUnit, 20]], defenderUnits: [['dummy_line', 1]],
      attackerCells: [[3, 3]], defenderCells: [[5, 3]], heroes: [], maxTicks: 200,
    }), 40);
    return b.squads.find((s) => s.unitId === attackerUnit)?.damageDealt ?? 0;
  };
  const sylvani = mk('sylvani_watcher');
  const human = mk('human_archer');
  return { passed: sylvani > human * 1.3, detail: `Sylvani watcher ${sylvani} vs Human archer ${human} damage in 40 ticks`,
    numbers: { sylvani, human, ratio: human ? +(sylvani / human).toFixed(2) : null } };
});

// 6 — Rakan active-play Fury from a manual cast.
check('Rakan Fury increases attack rate after manual cast', () => {
  const opts = { seed: 88, attackerUnits: [['rakan_crossbowman', 20]], defenderUnits: [['dummy', 1]],
    attackerCells: [[3, 3]], defenderCells: [[5, 3]], maxTicks: 200,
    heroes: [{ heroId: 'avalon', team: 0, cellX: 1, cellY: 3, might: 16, magic: 18, stamina: 20,
      command: 34, hp: 1000, skillIds: ['arrow_volley'] }] };
  const casts = [{ tick: 1, hero: 0, skill: 'arrow_volley', x: cellToWorld(5), y: cellToWorld(3) }];
  const active = runBattle(reg, makeSetup(reg, opts), 60, casts);
  const passive = runBattle(reg, makeSetup(reg, opts), 60);
  const attacks = (b) => b.squads.filter((s) => s.team === 0).reduce((a, s) => a + s.attacks, 0);
  const a = attacks(active), p = attacks(passive);
  return { passed: a > p, detail: `active ${a} volleys vs passive ${p} volleys in 60 ticks`,
    numbers: { active: a, passive: p } };
});

// 7 — Aggregated squads handle very large troop counts.
check('100,000 troops in one aggregated squad', () => {
  const b = runBattle(reg, makeSetup(reg, {
    seed: 6060, attackerUnits: [['human_spearman', 100000]], defenderUnits: [['dwarf_ironsmith', 100000]],
    attackerCells: [[3, 3]], defenderCells: [[6, 3]], heroes: [], maxTicks: 200,
  }), 30);
  const finite = b.squads.every((s) => Number.isFinite(s.hpPool) && Number.isFinite(s.damageDealt));
  return { passed: b.squads.length === 2 && finite,
    detail: `${b.squads.length} squad slots simulate 200,000 soldiers; hp pools ${b.squads.map((s) => s.hpPool).join(' / ')}`,
    numbers: { squads: b.squads.length, ticks: b.tick } };
});

// 8 — Offline production arithmetic mirrors StrongholdState.
check('Offline production is linear in elapsed minutes', () => {
  const miner = raw.buildings.items.find((b) => b.id === 'miner_hut');
  const perMinute = miner.effects[0].baseValue + miner.effects[0].perLevel;
  const oneHour = perMinute * 60;
  const twoHours = perMinute * 120;
  return { passed: twoHours === oneHour * 2 && oneHour === 7200,
    detail: `level-1 Miner Hut: ${perMinute}/min → 7,200 gold per offline hour`,
    numbers: { perMinute, oneHour, twoHours } };
});

// 9 — Conversion chart completeness.
check('Conversion chart covers 5 races × 7 roles', () => {
  const groups = raw.races.conversionChart;
  const all = groups.every((g) => g.entries.length === raw.races.items.length);
  return { passed: all && groups.length === 7,
    detail: `${groups.length} groups × ${raw.races.items.length} races = ${groups.length * raw.races.items.length} conversion mappings` };
});

// Balance snapshot: round-robin skirmishes (150 ticks each).
const races = raw.races.items.map((r) => r.id);
const matrix = {};
for (const a of races) {
  matrix[a] = {};
  for (const d of races) {
    const b = runBattle(reg, makeSetup(reg, { attackerRace: a, defenderRace: d, seed: 1234, maxTicks: 600 }), 400);
    matrix[a][d] = { outcome: b.outcome, attackerHp: Math.trunc(b.teamPower(0)), defenderHp: Math.trunc(b.teamPower(1)), hash: b.hashHex() };
  }
}

let passed = 0;
console.log('\n=== Nore reference combat harness ===\n');
for (const r of results) {
  if (r.passed) passed++;
  console.log(`${r.passed ? 'PASS' : 'FAIL'}  ${r.name}\n      ${r.detail}`);
}
console.log(`\n${passed}/${results.length} checks passed.`);
console.log('\nRace matchup snapshot (400 ticks, seed 1234):');
console.log('        ' + races.map((r) => r.padEnd(10)).join(''));
for (const a of races) {
  console.log(a.padEnd(8) + races.map((d) => {
    const cell = matrix[a][d];
    return (cell.outcome === 'AttackerWin' ? 'W' : cell.outcome === 'DefenderWin' ? 'L' : cell.outcome === 'Draw' ? 'D' : '-').padEnd(10);
  }).join(''));
}
console.log('\nW = attacker ahead at 400 ticks, L = defender ahead, D = draw, - = still ongoing.');

fs.mkdirSync(path.join(root, 'tools', 'out'), { recursive: true });
fs.writeFileSync(path.join(root, 'tools', 'out', 'harness-report.json'),
  JSON.stringify({ generatedAt: new Date().toISOString(), passed, total: results.length, results, matrix }, null, 2));
console.log('\nWrote tools/out/harness-report.json');
process.exit(passed === results.length ? 0 : 1);
