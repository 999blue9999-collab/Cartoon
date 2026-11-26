/**
 * Reference implementation of docs/COMBAT_SPEC.md in plain JavaScript.
 * The Unity C# BattleSimulation is authoritative; this mirror powers the browser
 * companion preview and tools/sim_harness.mjs so the same formulas and the same
 * JSON files can be exercised headlessly (determinism, morale, counters, ammo...).
 *
 * Integer-only gameplay math: basis points (10000 = 1.00x) and fixed-point grid
 * coordinates (Q = 1000 per cell). One squad = an aggregated stack of N troops.
 */
export const BP = 10000;
export const Q = 1000;

export const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
export const bpMul = (v, bp) => Math.trunc((v * bp) / BP);
export const bpDiv = (v, bp) => Math.trunc((v * BP) / Math.max(1, bp));
export const dist2 = (ax, ay, bx, by) => (ax - bx) ** 2 + (ay - by) ** 2;
export const within = (ax, ay, bx, by, r) => dist2(ax, ay, bx, by) <= r * r;
export const cellToWorld = (c) => c * Q + Q / 2;

export function moveToward(p, tx, ty, step) {
  const dx = tx - p.x, dy = ty - p.y;
  const d = Math.sqrt(dx * dx + dy * dy);
  if (d === 0 || step <= 0) return;
  if (d <= step) { p.x = tx; p.y = ty; return; }
  p.x += Math.trunc((dx * step) / d);
  p.y += Math.trunc((dy * step) / d);
}

/** xorshift32, identical stream to Nore.Core.DeterministicRng. */
export class Rng {
  constructor(seed = 1) { this.seed = seed >>> 0; this.state = this.seed || 0x9e3779b9; }
  nextUInt() {
    let x = this.state >>> 0;
    x ^= x << 13; x >>>= 0;
    x ^= x >>> 17;
    x ^= x << 5; x >>>= 0;
    this.state = x;
    return x >>> 0;
  }
  nextInt(upper) { return upper <= 1 ? 0 : this.nextUInt() % upper; }
  range(a, b) { return b <= a ? a : a + this.nextInt(b - a); }
  chanceBp(bp) { return this.nextUInt() % BP < bp; }
}

export function hash32(values) {
  let h = 2166136261 >>> 0;
  for (const v of values) {
    let x = BigInt(Math.trunc(v)) & 0xffffffffffffffffn;
    for (let b = 0; b < 4; b++) {
      h ^= Number((x >> BigInt(b * 8)) & 0xffn);
      h = Math.imul(h, 16777619) >>> 0;
    }
  }
  return h >>> 0;
}

/** Build fast lookup maps over the raw JSON files. */
export function makeRegistry(raw) {
  const map = (list) => Object.fromEntries((list ?? []).map((x) => [x.id, x]));
  const counters = new Map();
  for (const rc of raw.config?.roleCounters ?? []) counters.set(`${rc.attacker}>${rc.defender}`, rc.bp);
  const reg = {
    raw,
    config: raw.config,
    unit: (id) => map.units[id],
    race: (id) => map.races[id],
    hero: (id) => map.heroes[id],
    skill: (id) => map.skills[id],
    gear: (id) => map.gear[id],
    dragon: (id) => map.dragons[id],
    building: (id) => map.buildings[id],
    kingdom: (id) => map.kingdoms[id],
    city: (id) => map.cities[id],
    node: (id) => map.nodes[id],
    counterBp: (a, d) => counters.get(`${a}>${d}`) ?? BP,
    unitsForRace: (race) => raw.units.items.filter((u) => u.race === race),
  };
  const maps = {
    units: map(raw.units.items), races: map(raw.races.items), heroes: map(raw.heroes.items),
    skills: map(raw.skills.items), gear: map(raw.gear.items), dragons: map(raw.dragons.items),
    buildings: map(raw.buildings.items), kingdoms: map(raw.world.kingdoms),
    cities: map(raw.world.cities), nodes: map(raw.world.nodes),
  };
  Object.assign(map, maps);
  return reg;
}

/** Build a battle setup from JSON data. Options can override squads, heroes, seed, siege, command. */
export function makeSetup(reg, opts = {}) {
  const cfg = reg.config;
  const attackerRace = opts.attackerRace ?? "human";
  const defenderRace = opts.defenderRace ?? "dwarf";
  const command = opts.command ?? 20;
  const squads = [];
  const attackerUnits = opts.attackerUnits ?? reg.unitsForRace(attackerRace).slice(0, 6)
    .map((u) => [u.id, Math.max(8, u.stackSize)]);
  const defenderUnits = opts.defenderUnits ?? reg.unitsForRace(defenderRace).slice(0, 6)
    .map((u) => [u.id, Math.max(8, u.stackSize)]);
  const attackerCells = opts.attackerCells ?? null;
  const defenderCells = opts.defenderCells ?? null;
  attackerUnits.forEach(([unitId, count], i) => {
    const cell = attackerCells ? attackerCells[i % attackerCells.length] : [1 + (i % 3), (i * 2 + 1) % cfg.grid.rows];
    squads.push({ unitId, count, cellX: cell[0], cellY: cell[1], team: 0 });
  });
  defenderUnits.forEach(([unitId, count], i) => {
    const cell = defenderCells ? defenderCells[i % defenderCells.length] : [cfg.grid.columns - 2 - (i % 3), (i * 2) % cfg.grid.rows];
    squads.push({ unitId, count, cellX: cell[0], cellY: cell[1], team: 1 });
  });

  const mkHero = (id, team, cellX, cellY, skillIds) => {
    const def = reg.hero(id);
    return { heroId: id, team, cellX, cellY, might: def.stats.might, magic: def.stats.magic,
      stamina: def.stats.stamina, command: team === 0 ? command : def.stats.command,
      hp: 800 + def.stats.stamina * 22, skillIds: skillIds ?? def.skillIds };
  };
  const heroes = opts.heroes ?? [
    mkHero("rufio", 0, 0, 1, ["heroic_leap", "arrow_volley"]),
    mkHero("avalon", 0, 0, 4, ["war_horn", "arrow_volley"]),
    mkHero(opts.defenderHero ?? "jacques", 1, cfg.grid.columns - 1, 3, ["firestorm", "rocket_launcher"]),
  ];
  return {
    seed: opts.seed ?? 4242,
    label: opts.label ?? `${attackerRace} vs ${defenderRace}`,
    siege: !!opts.siege,
    defenderOnWalls: !!opts.siege,
    wallHp: opts.siege ? (opts.wallHp ?? 2000) : 0,
    maxTicks: opts.maxTicks ?? cfg.maxBattleTicks,
    disableAmmo: !!opts.disableAmmo,
    emptyAmmo: !!opts.emptyAmmo,
    overrides: opts.overrides ?? null,
    squads, heroes,
  };
}

export class Battle {
  constructor(setup, reg) {
    this.setup = setup;
    this.reg = reg;
    this.cfg = reg.config;
    this.rng = new Rng(setup.seed);
    this.tick = 0;
    this.finished = false;
    this.outcome = "Ongoing";
    this.log = [];
    this.totalKilled = 0;
    this.routedSquads = 0;
    this.rollingHash = 2166136261 >>> 0;
    this.teamRace = ["human", "human"];
    this.fury = [{ until: 0, stacks: 0 }, { until: 0, stacks: 0 }];
    this.heroDeathShock = [0, 0];
    this.squads = [];
    this.heroes = [];
    this.spawnSquads();
    this.spawnHeroes();
    this.note(`Battle: ${setup.label} (seed ${setup.seed})`);
  }

  note(line) { this.log.push(line); if (this.log.length > 240) this.log.shift(); }

  spawnSquads() {
    let seen = 0;
    for (const t of this.setup.squads) {
      const def = this.reg.unit(t.unitId);
      if (!def || t.count <= 0) continue;
      const race = this.reg.race(def.race);
      const mods = { ...race.modifiers };
      if (this.setup.overrides?.[t.unitId]) Object.assign(mods, this.setup.overrides[t.unitId]);
      if (this.setup.disableAmmo && t.team === 0) { mods.ammoReserve = 0; }
      const wall = this.setup.siege && this.setup.defenderOnWalls && t.team === 1 ? this.setup.wallHp : 0;
      const hpPerUnit = Math.max(1, def.hp + wall);
      let ammo = def.ammo + mods.ammoReserve;
      let ammoActive = ammo > 0 ? 1 : 0;
      if (this.setup.disableAmmo && t.team === 0) { ammo = 0; ammoActive = 0; }
      if (this.setup.emptyAmmo && t.team === 0) { ammo = 0; ammoActive = 1; }
      const s = {
        unitId: def.id, def, mods, raceId: def.race, team: clamp(t.team, 0, 1),
        x: cellToWorld(clamp(t.cellX, 0, this.cfg.grid.columns - 1)),
        y: cellToWorld(clamp(t.cellY, 0, this.cfg.grid.rows - 1)),
        hpPerUnit, maxAlive: t.count, alive: t.count,
        hpPool: hpPerUnit * t.count, maxHpPool: hpPerUnit * t.count,
        morale: this.cfg.morale.baseValue, shock: 0, routed: 0, routedTicks: 0, routedLost: 0,
        escaped: 0, attackTimer: 0, ammo, ammoActive, stun: 0,
        isSummon: 0, expireTick: 0, attacks: 0, damageDealt: 0,
        buffs: { hasteUntil: 0, attackUntil: 0, attackBp: 0, intimUntil: 0, intimBp: 0,
          resistUntil: 0, resistBp: BP },
      };
      this.squads.push(s);
      if (!(seen & (1 << s.team))) { this.teamRace[s.team] = def.race; seen |= 1 << s.team; }
    }
  }

  spawnHeroes() {
    for (const h of this.setup.heroes) {
      this.heroes.push({
        heroId: h.heroId, team: clamp(h.team, 0, 1),
        x: cellToWorld(clamp(h.cellX, 0, this.cfg.grid.columns - 1)),
        y: cellToWorld(clamp(h.cellY, 0, this.cfg.grid.rows - 1)),
        hp: Math.max(1, h.hp), maxHp: Math.max(1, h.hp), mana: 100,
        might: h.might, magic: h.magic, stamina: h.stamina, command: h.command,
        alive: 1, magicBuff: 0, skillIds: h.skillIds ?? [], cooldowns: {},
      });
    }
  }

  // ---------- tick ----------
  step() {
    if (this.finished) return;
    this.tick++;
    this.updateTimers();
    this.raceMechanics();
    if (this.tick % Math.max(1, this.cfg.morale.updateIntervalTicks) === 0) this.updateMorale();
    for (const s of this.squads) {
      if (s.alive <= 0 || s.escaped || s.stun > 0) continue;
      if (s.routed) { this.flee(s); continue; }
      const target = this.acquireTarget(s);
      if (!target) continue;
      if (!within(s.x, s.y, target.x, target.y, s.def.range)) moveToward(s, target.x, target.y, this.moveStep(s));
      else if (s.attackTimer <= 0) this.attack(s, target);
    }
    this.cleanup();
    this.heroCollapse();
    this.checkEnd();
    const hp = this.squads.reduce((a, s) => a + s.hpPool, 0);
    const morale = this.squads.reduce((a, s) => a + s.morale, 0);
    this.rollingHash ^= hash32([this.tick, this.squads.length, hp, morale, this.totalKilled, this.routedSquads]);
    this.rollingHash >>>= 0;
  }

  updateTimers() {
    for (const s of this.squads) {
      if (s.alive <= 0) continue;
      if (s.attackTimer > 0) s.attackTimer--;
      if (s.stun > 0) s.stun--;
      s.shock = Math.max(0, s.shock - this.cfg.morale.shockDecayPerTick);
    }
    for (const h of this.heroes) {
      if (!h.alive) continue;
      h.mana = Math.min(100, h.mana + 2);
      for (const id of Object.keys(h.cooldowns)) h.cooldowns[id] = Math.max(0, h.cooldowns[id] - 1);
    }
  }

  raceMechanics() {
    // Lich free summons every summonIntervalTicks while a support squad lives.
    if (this.tick % 20 === 0) {
      for (let team = 0; team < 2; team++) {
        let anchor = null;
        for (const s of this.squads) {
          if (s.team !== team || s.alive <= 0 || s.routed || s.escaped || s.mods.freeSummonCount <= 0) continue;
          if (!anchor || s.def.role === "clergy") anchor = s;
          if (s.def.role === "clergy") break;
        }
        if (!anchor || this.tick % Math.max(20, anchor.mods.summonIntervalTicks) !== 0) continue;
        this.spawnSummon(anchor.mods.summonUnitId, anchor.mods.freeSummonCount, team, anchor.x, anchor.y, 0);
        this.note(`T${this.tick} Lich free summon: +${anchor.mods.freeSummonCount} ${anchor.mods.summonUnitId}`);
      }
    }
    // Clergy / Serpent healing: up to 7 squads every 50 ticks.
    if (this.tick % 50 === 0) {
      for (const healer of this.squads) {
        if (healer.alive <= 0 || healer.routed || healer.escaped || healer.def.role !== "clergy") continue;
        const healBp = healer.mods.clergyHealBp + healer.mods.verdantHealBp;
        if (healBp <= 0) continue;
        let healed = 0;
        for (const s of this.squads) {
          if (healed >= 7 || s.team !== healer.team || s.alive <= 0 || s.hpPool >= s.maxHpPool) continue;
          if (!within(s.x, s.y, healer.x, healer.y, 4000)) continue;
          s.hpPool = Math.min(s.maxHpPool, s.hpPool + bpMul(s.maxHpPool, healBp));
          s.alive = Math.ceil(s.hpPool / s.hpPerUnit);
          healed++;
        }
      }
    }
  }

  updateMorale() {
    const m = this.cfg.morale;
    for (const s of this.squads) {
      if (s.alive <= 0 || s.escaped || this.moraleImmune(s)) continue;
      const lossBp = s.maxHpPool <= 0 ? 0 : Math.trunc(((s.maxHpPool - s.hpPool) * BP) / s.maxHpPool);
      const casualty = Math.trunc(lossBp / 100) * m.casualtyShockPerPercent;
      const aura = this.commandAura(s);
      s.morale = clamp(m.baseValue + aura - bpMul(casualty, s.mods.moraleShockBp) - s.shock - this.heroDeathShock[s.team], 0, 1000);
      if (!s.routed && s.morale < m.routThreshold) {
        s.routed = 1; s.routedTicks = 0; this.routedSquads++;
        this.note(`T${this.tick} ${s.def.name} (team ${s.team}) ROUTED at morale ${s.morale}`);
        for (const other of this.squads)
          if (other !== s && other.team === s.team && other.alive > 0 && !this.moraleImmune(other))
            other.shock += m.allyRoutShock;
      } else if (s.routed) {
        s.routedTicks += Math.max(1, m.updateIntervalTicks);
        if (s.morale >= m.rallyThreshold && s.routedTicks >= m.rallyTicks) {
          s.routed = 0; s.routedTicks = 0; s.morale = Math.max(s.morale, m.rallyThreshold);
          this.note(`T${this.tick} ${s.def.name} rallied`);
        }
      }
    }
  }

  commandAura(s) {
    let best = 0;
    for (const h of this.heroes) {
      if (!h.alive || h.team !== s.team) continue;
      const raw = h.command * this.cfg.morale.commandPerPoint;
      const aura = within(s.x, s.y, h.x, h.y, this.cfg.combat.commandAuraRange)
        ? raw : bpMul(raw, this.cfg.combat.commandAuraFalloffBp);
      best = Math.max(best, aura);
    }
    return best + (this.setup.siege && s.team === 1 ? 40 : 0);
  }

  moraleImmune(s) {
    return s.def.tags.includes("siege") || s.def.tags.includes("mechanical") || s.isSummon;
  }

  acquireTarget(s) {
    let best = null, bestD = Infinity;
    for (const t of this.squads) {
      if (t.team === s.team || t.alive <= 0 || t.escaped || t.routed) continue;
      let d = dist2(s.x, s.y, t.x, t.y);
      const counter = this.reg.counterBp(s.def.role, t.def.role);
      if (counter > BP) d = bpDiv(d, counter);
      if (d < bestD) { bestD = d; best = t; }
    }
    return best;
  }

  moveStep(s) {
    let step = bpMul(s.def.moveSpeed, s.mods.speedBp);
    if (this.tick < this.fury[s.team].until) step = bpMul(step, this.furyBp(s));
    if (this.tick < s.buffs.hasteUntil) step = bpMul(step, 11000);
    return Math.max(1, step);
  }

  furyBp(s) {
    const f = this.fury[s.team];
    if (this.teamRace[s.team] !== "rakan" || this.tick >= f.until) return BP;
    return BP + (s.mods.activeBuffBp - BP) * f.stacks;
  }

  attack(s, t) {
    const a = s.def, d = t.def;
    const burst = s.ammoActive && s.ammo > 0;
    const dry = s.ammoActive && s.ammo <= 0;
    let haste = bpMul(s.mods.hasteBp, this.furyBp(s));
    if (this.tick < s.buffs.hasteUntil) haste = bpMul(haste, 17000);
    if (burst) haste = bpMul(haste, 20000);
    else if (dry) haste = bpMul(haste, 6200);
    s.attackTimer = Math.max(1, Math.trunc((a.attackIntervalTicks * BP) / Math.max(2000, haste)));
    if (s.ammoActive && s.ammo > 0) s.ammo--;
    s.attacks++;

    let damage = a.attack * s.alive;
    damage = bpMul(damage, s.mods.damageDealtBp);
    damage = bpMul(damage, this.reg.counterBp(a.role, d.role));
    damage = bpMul(damage, this.cfg.combat.moraleDamageFloorBp + s.morale * 5);
    damage = bpMul(damage, BP + (this.tick < s.buffs.attackUntil ? s.buffs.attackBp : 0));
    damage = bpMul(damage, BP - (this.tick < s.buffs.intimUntil ? s.buffs.intimBp : 0));
    damage = bpMul(damage, burst ? s.mods.ammoBurstBp : dry ? s.mods.ammoDryBp : BP);
    damage = bpMul(damage, this.rampartBp(s, t));
    if (this.rng.chanceBp(this.cfg.combat.criticalChanceBp)) damage = bpMul(damage, this.cfg.combat.criticalBonusBp);
    this.applyDamage(t, damage, a.damageType === "magic", s);
    this.spillToHeroes(s, Math.trunc(damage / 20));
  }

  rampartBp(s, t) {
    if (!this.setup.siege || !this.setup.defenderOnWalls) return BP;
    if (s.team === 0 && t.def.role === "line") return s.mods.rampartBonusBp;
    return BP;
  }

  applyDamage(t, damage, magic, source) {
    if (damage <= 0 || t.alive <= 0) return 0;
    let mitigation = t.def.defense * 100;
    if (this.setup.siege && this.setup.defenderOnWalls && t.team === 1) mitigation += 1500;
    mitigation = Math.min(mitigation, this.cfg.combat.defenseSoftCapBp);
    damage = Math.trunc((damage * BP) / (BP + mitigation));
    damage = bpMul(damage, this.tick < t.buffs.resistUntil ? Math.max(1000, t.buffs.resistBp) : BP);
    damage = bpMul(damage, t.mods.damageTakenBp);
    if (magic) {
      let resist = t.def.magicResistBp;
      if (t.def.tags.includes("magical") || t.def.tags.includes("undead")) resist = Math.max(resist, t.mods.magicResistBp);
      resist = Math.min(resist, this.cfg.combat.magicResistCapBp);
      damage = bpMul(damage, BP - resist);
    }
    if (damage <= 0) return 0;
    if (source) source.damageDealt += damage;
    const pool = t.hpPool - damage;
    if (pool <= 0) {
      t.hpPool = 0; t.alive = 0;
      this.totalKilled += t.maxAlive - t.routedLost;
      this.note(`T${this.tick} ${t.def.name} (team ${t.team}) destroyed`);
    } else {
      t.hpPool = pool;
      t.alive = Math.ceil(pool / t.hpPerUnit);
    }
    return damage;
  }

  spillToHeroes(source, damage) {
    if (damage <= 0) return;
    for (const h of this.heroes) {
      if (!h.alive || h.team === source.team) continue;
      if (!within(source.x, source.y, h.x, h.y, 1800)) continue;
      h.hp -= Math.min(h.hp, Math.trunc(damage / 8));
      if (h.hp <= 0) {
        h.hp = 0; h.alive = 0;
        this.heroDeathShock[h.team] += this.cfg.morale.heroDeathShock;
        this.note(`T${this.tick} Hero ${h.heroId} fell!`);
      }
      break;
    }
  }

  flee(s) {
    s.routedTicks++;
    const edge = s.team === 0 ? -Q : (this.cfg.grid.columns + 1) * Q;
    moveToward(s, edge, s.y, Math.max(20, s.def.moveSpeed));
    if (s.routedTicks >= this.cfg.combat.routedEscapeTicks) {
      s.routedLost = s.alive; s.escaped = 1; s.hpPool = 0; s.alive = 0;
    }
  }

  cleanup() {
    for (const s of this.squads) {
      if (s.expireTick > 0 && this.tick >= s.expireTick && s.alive > 0) { s.alive = 0; s.hpPool = 0; }
    }
  }

  heroCollapse() {
    for (const h of this.heroes) {
      if (!h.alive) continue;
      if (this.squads.some((s) => s.team === h.team && s.alive > 0 && !s.escaped)) continue;
      h.hp -= 60;
      if (h.hp <= 0) { h.hp = 0; h.alive = 0; this.heroDeathShock[h.team] += this.cfg.morale.heroDeathShock; }
    }
  }

  checkEnd() {
    let a = 0, d = 0, ap = 0, dp = 0;
    for (const s of this.squads) {
      if (s.alive > 0 && !s.escaped && !s.routed) (s.team === 0 ? a++ : d++);
      if (s.alive > 0 && !s.escaped) { if (s.team === 0) ap += s.hpPool; else dp += s.hpPool; }
    }
    if (!a && !d) this.finish("Draw");
    else if (!a) this.finish("DefenderWin");
    else if (!d) this.finish("AttackerWin");
    else if (this.tick >= this.setup.maxTicks)
      this.finish(ap === dp ? "Draw" : ap > dp ? "AttackerWin" : "DefenderWin");
  }

  finish(outcome) {
    this.outcome = outcome;
    this.finished = true;
    this.note(`Battle finished at T${this.tick}: ${outcome} (hash ${this.hashHex()})`);
  }

  hashHex() { return (this.rollingHash >>> 0).toString(16).padStart(8, "0").toUpperCase(); }

  // ---------- skills ----------
  canCast(heroIndex, skillId) {
    if (this.finished) return "Battle over";
    const h = this.heroes[heroIndex];
    if (!h || !h.alive) return "Hero is down";
    if (!h.skillIds.includes(skillId)) return "Hero does not know that skill";
    const skill = this.reg.skill(skillId);
    if (!skill) return "Unknown skill";
    if ((h.cooldowns[skillId] ?? 0) > 0) return `Cooldown ${h.cooldowns[skillId]}t`;
    if (h.mana < skill.manaCost) return `Needs ${skill.manaCost} Mana`;
    return "";
  }

  castSkill(heroIndex, skillId, worldX, worldY) {
    const reason = this.canCast(heroIndex, skillId);
    if (reason) return reason;
    const h = this.heroes[heroIndex], skill = this.reg.skill(skillId);
    h.mana -= skill.manaCost;
    h.cooldowns[skillId] = Math.max(1, skill.cooldownTicks);
    const radius = skill.target === "enemies" ? 20000 : Math.max(1, skill.radius);
    const inRadius = (s) => within(worldX, worldY, s.x, s.y, radius);

    if (skill.kind === "damage" || skill.kind === "stun") {
      let base = skill.power + bpMul(h.might, skill.mightScaleBp) + bpMul(h.magic, skill.magicScaleBp);
      base = bpMul(base, BP + h.magicBuff);
      let hits = 0;
      for (const s of this.squads) {
        if (s.team === h.team || s.alive <= 0 || s.escaped || !inRadius(s)) continue;
        this.applyDamage(s, base, skill.damageType === "magic", null);
        if (skill.kind === "stun") s.stun = Math.max(s.stun, skill.durationTicks);
        hits++;
      }
      this.note(`  hit ${hits} squads for ${base} ${skill.damageType} each`);
    } else if (skill.kind === "heal") {
      const amount = skill.power + bpMul(h.magic, skill.magicScaleBp);
      let healed = 0;
      for (const s of this.squads) {
        if (healed >= 7 || s.team !== h.team || s.alive <= 0 || s.escaped || !inRadius(s)) continue;
        s.hpPool = Math.min(s.maxHpPool, s.hpPool + amount * 10);
        s.alive = Math.ceil(s.hpPool / s.hpPerUnit);
        healed++;
      }
      this.note(`  healed ${healed} squads`);
    } else if (skill.kind === "summon") {
      this.spawnSummon(skill.summonUnitId, skill.summonCount, h.team, worldX, worldY, skill.durationTicks);
    } else if (skill.kind === "buff") {
      if (skill.buffStat === "magicBp") {
        h.magicBuff += skill.magnitudeBp;
        this.note(`  spell power +${skill.magnitudeBp}bp`);
      } else {
        let affected = 0;
        for (const s of this.squads) {
          if (s.alive <= 0 || s.escaped) continue;
          const ally = s.team === h.team;
          if (skill.target === "enemies" && ally) continue;
          if (skill.target !== "enemies" && !ally) continue;
          if (skill.target === "self" && !within(h.x, h.y, s.x, s.y, 3000)) continue;
          switch (skill.buffStat) {
            case "hasteBp": s.buffs.hasteUntil = this.tick + skill.durationTicks; break;
            case "attackBp": s.buffs.attackUntil = this.tick + skill.durationTicks; s.buffs.attackBp = skill.magnitudeBp; break;
            case "hpBp": {
              const extra = bpMul(s.maxHpPool, skill.magnitudeBp);
              s.maxHpPool += extra; s.hpPool += extra; break;
            }
            case "intimidateBp": s.buffs.intimUntil = this.tick + skill.durationTicks; s.buffs.intimBp = skill.magnitudeBp; break;
            case "resistBp": s.buffs.resistUntil = this.tick + skill.durationTicks; s.buffs.resistBp = BP - skill.magnitudeBp; break;
            case "ammo": if (s.ammoActive) s.ammo += skill.summonCount; break;
            case "rally": if (s.routed) { s.routed = 0; s.routedTicks = 0; s.morale = Math.max(s.morale, this.cfg.morale.rallyThreshold); s.shock = 0; } break;
          }
          affected++;
        }
        this.note(`  buffed ${affected} squads (${skill.buffStat})`);
      }
    }
    if (this.teamRace[h.team] === "rakan") {
      const f = this.fury[h.team];
      f.stacks = Math.min(3, f.stacks + 1);
      f.until = this.tick + 60;
      this.note(`Rakan Fury x${f.stacks} triggered by manual cast`);
    }
    const msg = `T${this.tick} ${h.heroId} cast ${skill.name}`;
    this.note(msg);
    return msg;
  }

  autoCast(team) {
    if (this.finished) return;
    const enemies = this.squads.filter((s) => s.team !== team && s.alive > 0);
    if (!enemies.length) return;
    const cx = Math.trunc(enemies.reduce((a, s) => a + s.x, 0) / enemies.length);
    const cy = Math.trunc(enemies.reduce((a, s) => a + s.y, 0) / enemies.length);
    for (let h = 0; h < this.heroes.length; h++) {
      const hero = this.heroes[h];
      if (hero.team !== team || !hero.alive) continue;
      for (const id of hero.skillIds) {
        if (id && !this.canCast(h, id)) { this.castSkill(h, id, cx, cy); return; }
      }
    }
  }

  spawnSummon(unitId, count, team, x, y, durationTicks) {
    if (count <= 0) return;
    const def = this.reg.unit(unitId);
    if (!def) return;
    const race = this.reg.race(def.race);
    const hpPerUnit = Math.max(1, def.hp);
    this.squads.push({
      unitId: def.id, def, mods: { ...race.modifiers }, raceId: def.race, team, x, y,
      hpPerUnit, maxAlive: count, alive: count, hpPool: hpPerUnit * count, maxHpPool: hpPerUnit * count,
      morale: this.cfg.morale.baseValue, shock: 0, routed: 0, routedTicks: 0, routedLost: 0,
      escaped: 0, attackTimer: 0, ammo: def.ammo + race.modifiers.ammoReserve,
      ammoActive: def.ammo + race.modifiers.ammoReserve > 0 ? 1 : 0, stun: 0,
      isSummon: 1, expireTick: durationTicks > 0 ? this.tick + durationTicks : 0,
      attacks: 0, damageDealt: 0,
      buffs: { hasteUntil: 0, attackUntil: 0, attackBp: 0, intimUntil: 0, intimBp: 0, resistUntil: 0, resistBp: BP },
    });
  }

  teamPower(team) {
    return this.squads.reduce((a, s) => a + (s.team === team && s.alive > 0 && !s.escaped ? s.hpPool : 0), 0);
  }

  buildReport() {
    const won = this.outcome === "AttackerWin";
    return {
      outcome: this.outcome, ticks: this.tick, stateHash: this.hashHex(),
      routedSquads: this.routedSquads, totalKilled: this.totalKilled,
      attackerRemaining: Math.trunc(this.teamPower(0) / 1000),
      defenderRemaining: Math.trunc(this.teamPower(1) / 1000),
      lootGold: won ? 1200 + this.tick * 2 : 0,
      log: [...this.log],
      outcomes: this.squads.filter((s) => !s.isSummon).map((s) => {
        const survivors = !s.routed && !s.escaped ? s.alive : 0;
        const routed = s.routedLost + (s.routed && !s.escaped ? s.alive : 0);
        return { team: s.team, unitId: s.unitId, deployed: s.maxAlive,
          survivors, routed, killed: Math.max(0, s.maxAlive - survivors - routed) };
      }),
      summary: `${this.setup.label}: ${this.outcome} in ${this.tick} ticks`,
    };
  }
}

/** Headless helper used by the harness. casts: [{tick, hero, skill, x, y}] */
export function runBattle(reg, setup, ticks, casts = [], opts = {}) {
  const b = new Battle(setup, reg);
  for (let t = 0; t < ticks; t++) {
    for (const c of casts) if (c.tick === t) b.castSkill(c.hero, c.skill, c.x, c.y);
    b.step();
    if (opts.autoCast && t % 25 === 24) b.autoCast(1);
    if (b.finished) break;
  }
  return b;
}
