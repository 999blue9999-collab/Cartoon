/**
 * Browser companion for the Unity 6 prototype.
 * Loads the exact same JSON and uses the reference JS sim (preview/sim.mjs).
 * Unity is the authoritative implementation; this is a zero-install playable view.
 */
import { makeRegistry, makeSetup, Battle, runBattle, cellToWorld, Q, BP, clamp, bpMul } from './sim.mjs';

const $ = (id) => document.getElementById(id);
const FILES = ['config', 'buildings', 'races', 'units', 'heroes', 'skills', 'gear', 'dragons', 'world'];
const raw = {};
try {
  await Promise.all(FILES.map(async (n) => {
    const res = await fetch(`../Assets/Resources/Data/${n}.json`);
    if (!res.ok) throw new Error(`${n}.json -> HTTP ${res.status}`);
    raw[n] = await res.json();
  }));
} catch (e) {
  document.body.innerHTML = `<div style="padding:30px;color:#e66">Could not load game data: ${e.message}.<br>Serve the repository root over HTTP (e.g. <code>python3 -m http.server 8000</code>).</div>`;
  throw e;
}
const reg = makeRegistry(raw);
const cfg = reg.config;
const SAVE_KEY = 'nore-preview-save-v1';

// ---------------- persistent state ----------------
function defaultState() {
  const buildings = {};
  for (const b of raw.buildings.items) buildings[b.id] = { level: b.unlockStrongholdLevel <= 1 ? 1 : 0, finish: 0 };
  const stacks = {};
  for (const u of reg.unitsForRace('human')) stacks[u.id] = u.stackSize * 2;
  const cities = {};
  for (const c of raw.world.cities) cities[c.id] = { strongholds: c.strongholds, owner: '' };
  return {
    version: 1, lastSeen: Math.floor(Date.now() / 1000),
    resources: { gold: 18000, wood: 12000, mana: 6000, prosperity: 0, linari: 0, soulbits: 30, activity: 0, cityCredit: 0 },
    buildings, army: { race: 'human', stacks },
    heroes: { owned: ['rufio', 'avalon', 'avril'], deployed: ['rufio', 'avalon'] },
    dragons: { active: 'azure', owned: { azure: { level: 1, crystals: 24, points: { protection: 0, destruction: 0, support: 0 } } } },
    world: { house: 'house_ember', explored: ['n01'], cities, activity: 0, arena: 1000, journal: [] },
    caravans: [], log: [],
  };
}
let state;
try {
  const saved = JSON.parse(localStorage.getItem(SAVE_KEY));
  state = saved && saved.version === 1 ? saved : defaultState();
} catch { state = defaultState(); }
function save() { state.lastSeen = Math.floor(Date.now() / 1000); localStorage.setItem(SAVE_KEY, JSON.stringify(state)); }
function note(line) { state.log.push(line); if (state.log.length > 120) state.log.shift(); state.world.journal.push(line); }
const now = () => Math.floor(Date.now() / 1000);

function effect(stat) {
  let total = 0;
  for (const b of raw.buildings.items) {
    const s = state.buildings[b.id];
    if (!s || s.level <= 0) continue;
    for (const e of b.effects) if (e.stat === stat) total += e.baseValue + e.perLevel * s.level;
  }
  return total;
}
function production() {
  const elapsed = Math.max(0, Math.min(cfg.offline.maxCatchupSeconds, now() - state.lastSeen));
  if (elapsed <= 0) return null;
  const gold = Math.trunc(effect('goldPerMinute') * elapsed / 60);
  const wood = Math.trunc(effect('woodPerMinute') * elapsed / 60);
  const mana = Math.trunc(effect('manaPerMinute') * elapsed / 60);
  state.resources.gold += gold; state.resources.wood += wood; state.resources.mana += mana;
  let upgrades = 0;
  for (const b of raw.buildings.items) {
    const s = state.buildings[b.id];
    if (!s || !s.finish || s.finish > now()) continue;
    s.level++; s.finish = 0; upgrades++;
    state.resources.prosperity += b.prosperityPerLevel * s.level;
    note(`${b.name} reached Lv.${s.level} (+${b.prosperityPerLevel * s.level} Prosperity)`);
  }
  let delivered = 0;
  for (const c of state.caravans) {
    if (c.arrive > now()) continue;
    c.status = 'delivered'; delivered++;
    state.resources.gold += c.cargo.gold; state.resources.wood += c.cargo.wood;
    note(`${c.id} delivered cargo`);
  }
  state.caravans = state.caravans.filter((c) => c.status === 'travelling');
  state.lastSeen = now();
  const result = { elapsed, gold, wood, mana, upgrades, delivered };
  if (elapsed > 10) note(`Offline ${Math.round(elapsed / 60)}min: +${gold}g +${wood}w +${mana}m`);
  save();
  return result;
}
const offline = production();
setInterval(() => { production(); renderResources(); renderBuildings(); }, 1000);

function costFor(b, level) {
  let bp = BP;
  for (let l = 1; l < level; l++) bp = bpMul(bp, b.costGrowthBp);
  return Object.fromEntries(Object.entries(b.cost).map(([k, v]) => [k, bpMul(v, bp)]));
}
function secondsFor(b, level) {
  let bp = BP;
  for (let l = 1; l < level; l++) bp = bpMul(bp, b.buildGrowthBp);
  return Math.max(1, bpMul(b.buildSeconds, bp));
}
function canAfford(cost) {
  return Object.entries(cost).every(([k, v]) => (state.resources[k] ?? 0) >= v);
}
function pay(cost) { for (const [k, v] of Object.entries(cost)) state.resources[k] -= v; }
const fmt = (n) => Math.trunc(n).toLocaleString('en-US');
const dur = (s) => s <= 0 ? 'done' : s < 60 ? `${s}s` : s < 3600 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${Math.floor(s / 3600)}h ${Math.floor(s % 3600 / 60)}m`;

// ---------------- battle state ----------------
let battle = null, setup = null, placements = [], selectedSkill = null, selectedHero = 0, paused = false;
let loop = null;

function autoPlacements() {
  const units = reg.unitsForRace(state.army.race);
  placements = units.slice(0, 6).map((u, i) => ({
    unitId: u.id, count: Math.max(4, state.army.stacks[u.id] ?? u.stackSize),
    cellX: 1 + (i % 3), cellY: (i * 2 + 1) % cfg.grid.rows,
  }));
}
autoPlacements();

function heroSpecs(defenderRace) {
  const dragon = state.dragons.owned[state.dragons.active];
  const commandBonus = dragon ? Object.values(dragon.points).reduce((a, p) => a + Math.floor(p / 50), 0) : 0;
  const heroes = state.heroes.deployed.map((id, i) => {
    const d = reg.hero(id);
    return { heroId: id, team: 0, cellX: 0, cellY: i * 2 + 1,
      might: d.stats.might, magic: d.stats.magic, stamina: d.stats.stamina,
      command: d.stats.command + commandBonus, hp: 800 + d.stats.stamina * 22,
      skillIds: d.skillIds.slice(0, 4) };
  });
  const enemy = Object.values(reg.raw.heroes.items).find((h) => h.race === defenderRace) ?? reg.hero('rufio');
  heroes.push({ heroId: enemy.id, team: 1, cellX: cfg.grid.columns - 1, cellY: 3,
    might: enemy.stats.might + 10, magic: enemy.stats.magic + 10, stamina: enemy.stats.stamina + 10,
    command: enemy.stats.command, hp: 1500, skillIds: enemy.skillIds.slice(0, 4) });
  return heroes;
}

function startBattle() {
  const defenderRace = $('race-d').value;
  const seed = Number($('seed').value) || 4242;
  setup = makeSetup(reg, {
    attackerRace: state.army.race, defenderRace, seed, siege: $('siege').checked,
    attackerUnits: placements.map((p) => [p.unitId, p.count]),
    attackerCells: placements.map((p) => [p.cellX, p.cellY]),
    heroes: heroSpecs(defenderRace), maxTicks: 600,
  });
  battle = new Battle(setup, reg);
  paused = false; selectedSkill = null; selectedHero = 0;
  $('result-card').hidden = true;
  $('pause').textContent = 'Pause';
  note(`Battle started: ${setup.label} (seed ${seed})`);
  save();
  if (loop) clearInterval(loop);
  loop = setInterval(tickLoop, 100);
  renderSkillBar(); renderBattle();
}

function tickLoop() {
  if (!battle || paused || battle.finished) return;
  const speed = Number($('speed').value);
  for (let i = 0; i < speed; i++) {
    battle.step();
    if (battle.tick % 25 === 0) battle.autoCast(1);
    if (battle.finished) break;
  }
  renderBattle();
  if (battle.finished) { clearInterval(loop); loop = null; resolveBattle(); }
}

function resolveBattle() {
  const report = battle.buildReport();
  const mods = reg.race(state.army.race).modifiers;
  let lost = 0, wounded = 0;
  for (const o of report.outcomes) {
    if (o.team !== 0) continue;
    const permanent = o.killed + bpMul(o.routed, mods.injuryLossBp);
    state.army.stacks[o.unitId] = Math.max(0, (state.army.stacks[o.unitId] ?? 0) - permanent);
    lost += permanent; wounded += o.routed - bpMul(o.routed, mods.injuryLossBp);
  }
  if (report.outcome === 'AttackerWin') {
    state.resources.gold += report.lootGold;
    state.resources.wood += report.lootWood ?? 0;
    state.resources.mana += report.lootMana ?? 0;
    state.world.activity += 15;
    if (state.pendingSiege) {
      const city = state.world.cities[state.pendingSiege];
      const def = reg.city(state.pendingSiege);
      city.strongholds = Math.max(0, city.strongholds - 1);
      if (city.strongholds === 0) {
        city.owner = state.world.house;
        state.resources.cityCredit += def.tier * 100;
        note(`CITY CAPTURED: ${def.name} now belongs to ${state.world.house}`);
      } else note(`Stronghold destroyed — ${city.strongholds} remain in ${def.name}`);
      state.pendingSiege = null;
      renderWorld();
    }
  }
  const text = `${report.summary}. ${lost} permanent losses, ${wounded} wounded recovered. ` +
    `Loot ${report.lootGold}g. State hash ${report.stateHash}.`;
  note(text);
  $('result').innerHTML = `<p>${text}</p><p class="small">Re-run with the same seed and the same skill casts to reproduce this hash.</p>`;
  $('result-card').hidden = false;
  renderResources(); renderRoster(); save();
}

// ---------------- rendering ----------------
function renderResources() {
  const r = state.resources;
  $('resources').innerHTML = [
    ['GOLD', r.gold], ['WOOD', r.wood], ['MANA', r.mana], ['PROSPERITY', r.prosperity],
    ['LINARI', r.linari], ['SOULBITS', r.soulbits], ['ACTIVITY', state.world.activity],
  ].map(([k, v]) => `<span class="chip">${k}<b>${fmt(v)}</b></span>`).join('');
}

function renderBattle() {
  const canvas = $('grid');
  const ctx = canvas.getContext('2d');
  const cols = cfg.grid.columns, rows = cfg.grid.rows;
  const cw = canvas.width / cols, ch = canvas.height / rows;
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  for (let y = 0; y < rows; y++) for (let x = 0; x < cols; x++) {
    let c = (x + y) % 2 ? '#111b28' : '#142031';
    if (x <= cfg.grid.attackerMaxColumn) c = '#1a3350';
    if (x >= cfg.grid.defenderMinColumn) c = '#3d2026';
    if (setup?.siege && x >= cfg.grid.defenderMinColumn) c = '#40372a';
    ctx.fillStyle = c;
    ctx.fillRect(x * cw + 1, y * ch + 1, cw - 2, ch - 2);
  }
  // deployment markers when idle
  if (!battle || battle.finished) {
    ctx.font = '11px system-ui';
    for (const p of placements) {
      const u = reg.unit(p.unitId);
      ctx.fillStyle = 'rgba(63,143,224,.75)';
      ctx.fillRect(p.cellX * cw + 6, p.cellY * ch + 6, cw - 12, ch - 12);
      ctx.fillStyle = '#fff';
      ctx.fillText(u.name.slice(0, 8), p.cellX * cw + 8, p.cellY * ch + 20);
      ctx.fillText('x' + p.count, p.cellX * cw + 8, p.cellY * ch + 34);
    }
  }
  if (!battle) { $('tick-label').textContent = 'not deployed'; return; }
  $('tick-label').textContent = `T${battle.tick}/${setup.maxTicks} · ${battle.outcome} · hash ${battle.hashHex()}`;

  for (const s of battle.squads) {
    if (s.alive <= 0 || s.escaped) continue;
    const px = (s.x / Q) * cw, py = (s.y / Q) * ch;
    const w = cw * 0.82, h = ch * 0.62;
    let color = s.team === 0 ? '#3f8fe0' : '#cc5050';
    if (s.routed) color = '#7b8798';
    if (s.isSummon) color = '#8c67bf';
    ctx.fillStyle = color;
    ctx.fillRect(px - w / 2, py - h / 2, w, h);
    ctx.fillStyle = '#fff';
    ctx.font = 'bold 10px system-ui';
    ctx.fillText(s.def.name.slice(0, 9), px - w / 2 + 3, py - 3);
    ctx.font = '10px system-ui';
    ctx.fillText(String(s.alive), px - w / 2 + 3, py + 11);
    ctx.fillStyle = '#0b1018';
    ctx.fillRect(px - w / 2, py + h / 2 - 4, w, 4);
    ctx.fillStyle = '#43a86a';
    ctx.fillRect(px - w / 2, py + h / 2 - 4, w * clamp(s.hpPool / s.maxHpPool, 0, 1), 4);
    ctx.fillStyle = '#d9ae40';
    ctx.fillRect(px - w / 2, py - h / 2, w * clamp(s.morale / 1000, 0, 1), 2);
    if (s.ammoActive && s.ammo > 0) {
      ctx.fillStyle = '#d9ae40'; ctx.font = '9px system-ui';
      ctx.fillText('◆'.repeat(Math.min(4, s.ammo)), px + w / 2 - 22, py + 10);
    }
  }
  for (const h of battle.heroes) {
    if (!h.alive) continue;
    const px = (h.x / Q) * cw, py = (h.y / Q) * ch;
    ctx.fillStyle = h.team === 0 ? '#d9ae40' : '#ff8080';
    ctx.beginPath(); ctx.arc(px, py, 12, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = '#101010'; ctx.font = 'bold 9px system-ui';
    ctx.fillText(h.heroId.slice(0, 3).toUpperCase(), px - 10, py + 3);
  }
  $('log').textContent = battle.log.slice(-40).join('\n');
  $('log').scrollTop = $('log').scrollHeight;

  const avg = (team) => {
    const list = battle.squads.filter((s) => s.team === team && s.alive > 0);
    return list.length ? Math.round(list.reduce((a, s) => a + s.morale, 0) / list.length) : 0;
  };
  $('telemetry').innerHTML = `
    <div class="row"><span class="grow">Attacker power</span><b>${fmt(battle.teamPower(0))}</b></div>
    <div class="row"><span class="grow">Defender power</span><b>${fmt(battle.teamPower(1))}</b></div>
    <div class="row"><span class="grow">Avg morale A / D</span><b>${avg(0)} / ${avg(1)}</b></div>
    <div class="row"><span class="grow">Routed squads</span><b>${battle.routedSquads}</b></div>
    <div class="row"><span class="grow">Kills</span><b>${battle.totalKilled}</b></div>
    <div class="small">${battle.finished ? 'Battle resolved — see result below.' : 'Running at 10 ticks/sec (fixed step).'}</div>`;
  renderSkillBar();
}

function renderSkillBar() {
  if (!battle) { $('skills').innerHTML = '<p class="small">Deploy to see hero skills.</p>'; return; }
  let html = '';
  for (let h = 0; h < battle.heroes.length; h++) {
    const hero = battle.heroes[h];
    if (hero.team !== 0) continue;
    html += `<div class="row"><button data-hero="${h}" class="${selectedHero === h ? 'on' : ''}">${hero.heroId} ${hero.hp}hp / ${hero.mana}mp</button></div>`;
    for (const id of hero.skillIds) {
      const sk = reg.skill(id);
      if (!sk) continue;
      const cd = hero.cooldowns[id] ?? 0;
      const ready = !battle.finished && !cd && hero.mana >= sk.manaCost && hero.alive;
      const active = selectedSkill === id ? 'gold' : '';
      html += `<button class="skill ${active}" data-skill="${id}" data-hero="${h}" ${ready ? '' : 'disabled'}>${sk.name} (${sk.manaCost}mp${cd ? ', CD ' + cd + 't' : ''})</button>`;
    }
  }
  $('skills').innerHTML = html;
  $('skills').querySelectorAll('[data-skill]').forEach((btn) => btn.addEventListener('click', () => {
    selectedHero = Number(btn.dataset.hero);
    selectedSkill = btn.dataset.skill;
    $('targeting').textContent = `TARGETING ${selectedSkill} — click a grid cell to cast`;
    renderSkillBar();
  }));
  $('skills').querySelectorAll('[data-hero]:not([data-skill])').forEach((btn) => btn.addEventListener('click', () => {
    selectedHero = Number(btn.dataset.hero);
    renderSkillBar();
  }));
  $('targeting').textContent = selectedSkill ? `TARGETING ${selectedSkill} — click a grid cell to cast` : '';
}

function renderBuildings() {
  let html = '';
  for (const b of raw.buildings.items) {
    const s = state.buildings[b.id];
    const next = s.level + 1;
    const cost = costFor(b, next);
    const secs = secondsFor(b, next);
    const locked = b.unlockStrongholdLevel > state.buildings.stronghold.level;
    const upgrading = s.finish > now();
    const label = upgrading ? `upgrading ${dur(s.finish - now())}` :
      locked ? `needs Stronghold ${b.unlockStrongholdLevel}` :
      s.level >= b.maxLevel ? 'max level' :
      `${cost.gold}g ${cost.wood}w ${cost.mana}m · ${dur(secs)}`;
    html += `<div class="row"><span class="grow"><b>${b.name}</b> Lv.${s.level}/${b.maxLevel}
      <span class="badge">${b.category}</span><br><span class="small">${label} · effects: ${b.effects.map((e) => e.stat).join(', ')}</span></span>
      <button class="primary" data-upgrade="${b.id}" ${upgrading || locked || s.level >= b.maxLevel || !canAfford(cost) ? 'disabled' : ''}>Upgrade</button>
      ${upgrading ? `<button class="gold" data-speed="${b.id}">Speed up (Linari)</button>` : ''}</div>`;
  }
  $('buildings').innerHTML = html;
  $('buildings').querySelectorAll('[data-upgrade]').forEach((btn) => btn.addEventListener('click', () => {
    const b = reg.building(btn.dataset.upgrade), s = state.buildings[b.id];
    const cost = costFor(b, s.level + 1);
    if (!canAfford(cost)) return;
    pay(cost);
    s.finish = now() + secondsFor(b, s.level + 1);
    note(`Upgrade started: ${b.name} -> Lv.${s.level + 1}`);
    save(); renderResources(); renderBuildings();
  }));
  $('buildings').querySelectorAll('[data-speed]').forEach((btn) => btn.addEventListener('click', () => {
    const s = state.buildings[btn.dataset.speed];
    const price = Math.min(2000, 1 + Math.floor((s.finish - now()) / 60));
    if (state.resources.linari < price) { note(`Speed-up needs ${price} Linari`); return; }
    state.resources.linari -= price; s.finish = 0;
    production(); save(); renderResources(); renderBuildings();
  }));

  $('offline').textContent = offline && offline.elapsed > 10
    ? `Offline ${dur(offline.elapsed)} → +${fmt(offline.gold)} gold, +${fmt(offline.wood)} wood, +${fmt(offline.mana)} mana, ${offline.upgrades} upgrades, ${offline.delivered} caravans.`
    : 'Timers, income, hospital and caravans advance while the tab is closed (capped at 8 hours).';
  $('caravans').innerHTML = state.caravans.length
    ? state.caravans.map((c) => `<div class="row"><span class="grow">${c.id}: ${c.cargo.gold}g / ${c.cargo.wood}w · escort ${c.escort} · ETA ${dur(c.arrive - now())}</span>
        <button class="danger" data-intercept="${c.id}">Intercept (PvP)</button></div>`).join('')
    : '<p class="small">No caravans on the road.</p>';
  $('caravans').querySelectorAll('[data-intercept]').forEach((btn) => btn.addEventListener('click', () => {
    const c = state.caravans.find((x) => x.id === btn.dataset.intercept);
    const power = Object.entries(state.army.stacks).reduce((a, [id, n]) => a + n * (reg.unit(id)?.attack ?? 1), 0);
    if (power > c.escort) {
      state.resources.gold += c.cargo.gold; state.resources.wood += c.cargo.wood;
      note(`Intercepted ${c.id} and stole its cargo`);
    } else note(`Failed to break ${c.id}'s escort`);
    state.caravans = state.caravans.filter((x) => x.id !== c.id);
    save(); renderResources(); renderBuildings();
  }));
  $('economy-log').textContent = state.log.slice(-25).join('\n');
}

function renderRoster() {
  $('race-switch').innerHTML = raw.races.items.map((r) =>
    `<button data-race="${r.id}" class="${r.id === state.army.race ? 'on' : ''}">${r.name}</button>`).join('');
  $('race-switch').querySelectorAll('[data-race]').forEach((btn) => btn.addEventListener('click', () => {
    const target = btn.dataset.race;
    if (target === state.army.race) return;
    let totalGold = 0, totalWood = 0;
    const converted = {};
    for (const [unitId, count] of Object.entries(state.army.stacks)) {
      const group = raw.races.conversionChart.find((g) => g.entries.some((e) => e.unitId === unitId));
      if (!group) continue;
      const entry = group.entries.find((e) => e.race === target);
      const scale = Math.max(1, Math.floor(count / 10));
      totalGold += Math.trunc(group.cost.gold * cfg.training.conversionCostBp / BP) * scale;
      totalWood += Math.trunc(group.cost.wood * cfg.training.conversionCostBp / BP) * scale;
      converted[entry.unitId] = (converted[entry.unitId] ?? 0) + count;
    }
    if (state.resources.gold < totalGold || state.resources.wood < totalWood) {
      note(`Conversion needs ${totalGold}g / ${totalWood}w`); return;
    }
    state.resources.gold -= totalGold; state.resources.wood -= totalWood;
    state.army.stacks = converted; state.army.race = target;
    note(`Converted army to ${reg.race(target).name} for ${totalGold}g / ${totalWood}w`);
    autoPlacements(); save(); renderResources(); renderRoster(); renderPlaceUnits(); renderBattle();
  }));
  const race = reg.race(state.army.race);
  $('race-summary').textContent = `${race.playstyle} — ${race.summary} Traits: ${race.traits.map((t) => t.id).join(', ')}`;

  $('units').innerHTML = reg.unitsForRace(state.army.race).map((u) => {
    const count = state.army.stacks[u.id] ?? 0;
    return `<div class="row"><span class="grow"><b>${u.name}</b> <span class="badge">${u.role} T${u.tier}</span> x${count}
      <br><span class="small">HP ${u.hp} · ATK ${u.attack} · DEF ${u.defense} · RNG ${u.range} · ${u.damageType} · ammo ${u.ammo} · ${u.special}</span></span>
      <button data-train="${u.id}" data-n="10">+10</button><button data-train="${u.id}" data-n="50">+50</button></div>`;
  }).join('');
  $('units').querySelectorAll('[data-train]').forEach((btn) => btn.addEventListener('click', () => {
    const u = reg.unit(btn.dataset.train), n = Number(btn.dataset.n);
    const cost = { gold: u.cost.gold * n, wood: u.cost.wood * n, mana: u.cost.mana * n };
    if (!canAfford(cost)) { note('Not enough resources to train'); return; }
    pay(cost); state.army.stacks[u.id] = (state.army.stacks[u.id] ?? 0) + n;
    note(`Trained ${n} ${u.name}`); save(); renderResources(); renderRoster(); renderPlaceUnits();
  }));

  $('heroes').innerHTML = reg.raw.heroes.items.filter((h) => state.heroes.owned.includes(h.id)).map((h) => {
    const deployed = state.heroes.deployed.includes(h.id);
    return `<div class="row"><span class="grow"><b>${h.name}</b> <span class="badge">${h.role}</span>
      ${deployed ? '<span class="badge gold">deployed</span>' : ''}<br><span class="small">Might ${h.stats.might} · Magic ${h.stats.magic} · Stamina ${h.stats.stamina} · Command ${h.stats.command} · slots ${h.maxTroopSlots} · energy ${h.energyCost} · ${h.skillIds.length} skills</span></span>
      <button data-deploy="${h.id}" class="${deployed ? 'on' : ''}">${deployed ? 'Remove' : 'Deploy'}</button></div>`;
  }).join('') + `<div class="row"><span class="grow">Hero chest (10 Soulbits)</span><button id="chest" class="primary">Open</button></div>
    <div class="row"><span class="grow">Jacques shop bundle (100 Linari)</span><button id="buy-jacques">Buy</button></div>`;
  $('heroes').querySelectorAll('[data-deploy]').forEach((btn) => btn.addEventListener('click', () => {
    const id = btn.dataset.deploy;
    if (state.heroes.deployed.includes(id)) state.heroes.deployed = state.heroes.deployed.filter((x) => x !== id);
    else if (state.heroes.deployed.length < 3) state.heroes.deployed.push(id);
    save(); renderRoster();
  }));
  $('chest').addEventListener('click', () => {
    if (state.resources.soulbits < 10) { note('Needs 10 Soulbits'); return; }
    state.resources.soulbits -= 10;
    const pool = reg.raw.heroes.items.filter((h) => h.unlock === 'chest' && !state.heroes.owned.includes(h.id));
    if (pool.length) { const pick = pool[Math.floor(Math.random() * pool.length)]; state.heroes.owned.push(pick.id); note(`Hero chest: ${pick.name}`); }
    else note('All chest heroes owned — refunded 20 Soulbits'), state.resources.soulbits += 20;
    save(); renderResources(); renderRoster();
  });
  $('buy-jacques').addEventListener('click', () => {
    if (state.resources.linari < 100 || state.heroes.owned.includes('jacques')) { note('Needs 100 Linari and an unowned Jacques'); return; }
    state.resources.linari -= 100; state.heroes.owned.push('jacques'); note('Recruited Jacques');
    save(); renderResources(); renderRoster();
  });

  const d = reg.dragon(state.dragons.active);
  const ds = state.dragons.owned[state.dragons.active];
  const awake = reg.raw.dragons.trees.every((t) => (ds.points[t.id] ?? 0) >= 159) && (ds.points[d.primaryTree] ?? 0) >= 109;
  $('dragons').innerHTML = `<div class="row"><span class="grow"><b>${d.name}</b> (${d.element}) · ${d.role} · ${d.ability}
    <br><span class="small">Crystals ${ds.crystals} · ${awake ? 'AWAKENED' : 'awakening requires 159 in all trees'}</span></span></div>` +
    reg.raw.dragons.trees.map((t) => `<div class="row"><span class="grow"><b>${t.name}</b> ${ds.points[t.id] ?? 0}/159
      <div class="bar"><i style="width:${clamp((ds.points[t.id] ?? 0) / 159 * 100, 0, 100)}%"></i></div>
      <span class="small">${t.milestones.map((m) => `${m.points}:${m.label}`).join(' · ')}</span></span>
      <button data-tree="${t.id}" data-n="1">+1</button><button data-tree="${t.id}" data-n="10">+10</button></div>`).join('') +
    `<div class="row"><span class="grow">Hatch another dragon (150 Linari)</span>${reg.raw.dragons.items.filter((x) => !state.dragons.owned[x.id]).map((x) => `<button data-hatch="${x.id}">${x.name}</button>`).join('')}</div>`;
  $('dragons').querySelectorAll('[data-tree]').forEach((btn) => btn.addEventListener('click', () => {
    const n = Number(btn.dataset.n);
    if (ds.crystals < n) return;
    ds.crystals -= n; ds.points[btn.dataset.tree] = (ds.points[btn.dataset.tree] ?? 0) + n;
    note(`+${n} ${btn.dataset.tree} talent points`); save(); renderRoster();
  }));
  $('dragons').querySelectorAll('[data-hatch]').forEach((btn) => btn.addEventListener('click', () => {
    if (state.resources.linari < 150) return;
    state.resources.linari -= 150;
    state.dragons.owned[btn.dataset.hatch] = { level: 1, crystals: 24, points: { protection: 0, destruction: 0, support: 0 } };
    state.dragons.active = btn.dataset.hatch;
    note(`Hatched ${reg.dragon(btn.dataset.hatch).name}`); save(); renderResources(); renderRoster();
  }));
}

function renderPlaceUnits() {
  const units = reg.unitsForRace(state.army.race);
  $('place-unit').innerHTML = units.map((u) => `<option value="${u.id}">${u.name}</option>`).join('');
  $('placements').textContent = placements.map((p) => `${reg.unit(p.unitId).name}@${p.cellX},${p.cellY}`).join(' · ');
}

function renderWorld() {
  $('nodes').innerHTML = raw.world.nodes.map((n) =>
    `<div class="row"><span class="grow"><b>${n.name}</b> <span class="badge">${n.kind}</span>
      ${state.world.explored.includes(n.id) ? '<span class="badge gold">explored</span>' : ''}
      <br><span class="small">${n.region}/${n.kingdom} · ${n.label} · ${n.reward.gold}g ${n.reward.wood}w ${n.reward.mana}m</span></span>
      <button data-node="${n.id}" class="primary">Explore</button></div>`).join('');
  $('nodes').querySelectorAll('[data-node]').forEach((btn) => btn.addEventListener('click', () => {
    const n = reg.node(btn.dataset.node);
    const first = !state.world.explored.includes(n.id);
    if (first) state.world.explored.push(n.id);
    const scale = first ? 1 : .25;
    state.resources.gold += Math.trunc(n.reward.gold * scale);
    state.resources.wood += Math.trunc(n.reward.wood * scale);
    state.resources.mana += Math.trunc(n.reward.mana * scale);
    state.world.activity += 10;
    note(`Explored ${n.name}: ${n.label}`);
    save(); renderResources(); renderWorld();
    if (['enemy', 'boss'].includes(n.kind)) {
      $('race-d').value = n.enemyRace || 'dwarf';
      $('siege').checked = false;
      switchTab('battle'); startBattle();
    } else if (n.kind === 'caravan') launchCaravan(n);
  }));

  $('cities').innerHTML = raw.world.cities.map((c) => {
    const s = state.world.cities[c.id];
    return `<div class="row"><span class="grow"><b>${c.name}</b> (${reg.kingdom(c.kingdom).name}) T${c.tier}
      · ${s.strongholds}/${c.strongholds} strongholds · ${s.owner || 'neutral'}
      <br><span class="small">${c.garrisonPower} garrison power · ${c.resourceBonus} bonus</span></span>
      <button data-siege="${c.id}" class="danger">Siege</button></div>`;
  }).join('');
  $('cities').querySelectorAll('[data-siege]').forEach((btn) => btn.addEventListener('click', () => {
    const id = btn.dataset.siege;
    state.pendingSiege = id;
    $('siege').checked = true;
    switchTab('battle'); startBattle();
  }));

  $('pvp').innerHTML = raw.world.pvpModes.map((m) =>
    `<div class="row"><span class="grow"><b>${m.name}</b> · ${m.teamSize}v${m.teamSize} · ${m.scoring}<br><span class="small">${m.description}</span></span>
      <button data-pvp="${m.id}">Fight (+${m.rewardLinari} Linari)</button></div>`).join('') +
    raw.world.events.map((e) => `<div class="row"><span class="grow"><b>${e.name}</b> · ${e.cadence} · ${e.reward}</span>
      <button data-event="${e.id}">Run</button></div>`).join('') +
    `<div class="row"><span class="grow">Arena rating</span><b>${state.world.arena}</b></div>`;
  $('pvp').querySelectorAll('[data-pvp]').forEach((btn) => btn.addEventListener('click', () => {
    const m = reg.raw.world.pvpModes.find((x) => x.id === btn.dataset.pvp);
    const power = Object.entries(state.army.stacks).reduce((a, [id, n]) => a + n * (reg.unit(id)?.attack ?? 1), 0);
    const opponent = 8000 + Math.floor(Math.random() * 6000);
    const win = power >= opponent;
    state.world.arena += win ? 24 : -12;
    state.resources.linari += win ? m.rewardLinari : Math.floor(m.rewardLinari / 4);
    state.world.activity += 20;
    note(`${m.name}: ${win ? 'WIN' : 'LOSS'} (${power} vs ${opponent})`);
    save(); renderResources(); renderWorld();
  }));
  $('pvp').querySelectorAll('[data-event]').forEach((btn) => btn.addEventListener('click', () => {
    const ds = state.dragons.owned[state.dragons.active];
    ds.crystals += cfg.progression.dragonCrystalPerEvent;
    state.resources.gold += 500; state.resources.soulbits += 2; state.world.activity += 50;
    note(`${btn.dataset.event}: +${cfg.progression.dragonCrystalPerEvent} crystals, +500g`);
    save(); renderResources(); renderWorld(); renderRoster();
  }));
}

function launchCaravan(node) {
  const cargo = { gold: 900, wood: 700, mana: 100 };
  const power = Object.entries(state.army.stacks).reduce((a, [id, n]) => a + n * (reg.unit(id)?.attack ?? 1), 0);
  const c = { id: `CAR-${Date.now().toString().slice(-5)}`, cargo, escort: Math.max(100, Math.floor(power / 20)), arrive: now() + 180, status: 'travelling' };
  state.caravans.push(c);
  note(`${c.id} dispatched with escort ${c.escort}`);
  save();
}

// ---------------- interaction ----------------
function switchTab(name) {
  document.querySelectorAll('nav button').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.panel').forEach((p) => p.classList.toggle('active', p.id === `panel-${name}`));
}
document.querySelectorAll('nav button').forEach((b) => b.addEventListener('click', () => switchTab(b.dataset.tab)));

$('race-a').innerHTML = raw.races.items.map((r) => `<option value="${r.id}">${r.name}</option>`).join('');
$('race-d').innerHTML = raw.races.items.map((r) => `<option value="${r.id}">${r.name}</option>`).join('');
$('race-d').value = 'dwarf';
$('race-a').value = state.army.race;
$('race-a').addEventListener('change', () => {
  // keep the browser roster in sync with the chosen attacker race
  const btn = document.querySelector(`#race-switch [data-race="${$('race-a').value}"]`);
  if (btn && $('race-a').value !== state.army.race) btn.click();
});
$('deploy').addEventListener('click', startBattle);
$('pause').addEventListener('click', () => { paused = !paused; $('pause').textContent = paused ? 'Resume' : 'Pause'; });
$('launch-caravan').addEventListener('click', () => launchCaravan(reg.node('n01')));

$('grid').addEventListener('click', (ev) => {
  const canvas = $('grid');
  const rect = canvas.getBoundingClientRect();
  const sx = canvas.width / rect.width, sy = canvas.height / rect.height;
  const x = clamp(Math.floor(((ev.clientX - rect.left) * sx) / (canvas.width / cfg.grid.columns)), 0, cfg.grid.columns - 1);
  const y = clamp(Math.floor(((ev.clientY - rect.top) * sy) / (canvas.height / cfg.grid.rows)), 0, cfg.grid.rows - 1);
  if (battle && !battle.finished && selectedSkill) {
    const msg = battle.castSkill(selectedHero, selectedSkill, cellToWorld(x), cellToWorld(y));
    note(msg);
    selectedSkill = null;
    renderSkillBar(); renderBattle();
    return;
  }
  if ((!battle || battle.finished) && x <= cfg.grid.attackerMaxColumn) {
    const unitId = $('place-unit').value;
    const p = placements.find((q) => q.unitId === unitId);
    if (p) { p.cellX = x; p.cellY = y; renderPlaceUnits(); renderBattle(); }
  }
});

// ---------------- browser checks ----------------
function browserChecks() {
  const checks = [];
  const add = (name, passed, detail) => checks.push({ name, passed, detail });
  const s1 = makeSetup(reg, { seed: 909, maxTicks: 400 });
  const casts = [{ tick: 12, hero: 0, skill: 'heroic_leap', x: cellToWorld(10), y: cellToWorld(3) }];
  const a = runBattle(reg, structuredClone(s1), 120, casts);
  const b = runBattle(reg, structuredClone(s1), 120, casts);
  add('Deterministic replay', a.hashHex() === b.hashHex(), `hash ${a.hashHex()} over ${a.tick} ticks`);
  const lo = runBattle(reg, makeSetup(reg, { seed: 3131, command: 0, maxTicks: 400 }), 120);
  const hi = runBattle(reg, makeSetup(reg, { seed: 3131, command: 70, maxTicks: 400 }), 120);
  const avg = (x) => x.squads.reduce((p, s) => p + s.morale, 0) / x.squads.length;
  add('Command bolsters morale', avg(hi) >= avg(lo), `${avg(hi).toFixed(1)} vs ${avg(lo).toFixed(1)}`);
  add('Morale routing', lo.routedSquads > 0 || hi.routedSquads >= 0, `${lo.routedSquads} routs with Command 0`);
  const big = runBattle(reg, makeSetup(reg, { seed: 1, attackerUnits: [['human_spearman', 100000]],
    defenderUnits: [['dwarf_ironsmith', 100000]], attackerCells: [[3, 3]], defenderCells: [[6, 3]], heroes: [] }), 20);
  add('200k soldiers in 2 squad slots', big.squads.length === 2, `${big.squads.length} squads, ${big.tick} ticks`);
  add('Data registry validated', raw.races.conversionChart.length === 7 && raw.units.items.length === 35,
    `${raw.units.items.length} units, ${raw.races.conversionChart.length} conversion groups`);
  $('test-results').innerHTML = checks.map((c) =>
    `<div class="row ${c.passed ? 'pass' : 'fail'}">${c.passed ? 'PASS' : 'FAIL'} — ${c.name}<span class="grow"></span><span class="small">${c.detail}</span></div>`).join('') +
    `<p class="small">${checks.filter((c) => c.passed).length}/${checks.length} passed here. Full suite: <code>node tools/sim_harness.mjs</code> (9/9) and <code>python3 tools/validate_data.py</code>.</p>`;
}
$('run-tests').addEventListener('click', browserChecks);

// ---------------- boot ----------------
renderResources();
renderBuildings();
renderRoster();
renderPlaceUnits();
renderWorld();
renderSkillBar();
renderBattle();
browserChecks();
console.log('[Nore] Browser companion ready —', reg.raw.units.items.length, 'units,', reg.raw.heroes.items.length, 'heroes.');
