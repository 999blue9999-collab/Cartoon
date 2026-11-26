# Combat specification

This document is the contract implemented by **both**
`Assets/Scripts/Combat/BattleSimulation.cs` (Unity, authoritative) and
`preview/sim.mjs` (JS reference for the browser preview and headless harness).
`node tools/sim_harness.mjs` exercises these rules against the shipped JSON.

## Units of measure

| Concept | Representation |
|---|---|
| Multiplier | basis points, `BP = 10000` = 1.00x |
| Position | fixed point, `Q = 1000` per grid cell |
| Tick | `1 / config.tickRate` seconds = 100 ms by default |
| HP | one integer pool per squad; `hpPool = hpPerUnit * alive` |
| Distance | squared Euclidean, compared to `range²` (no sqrt in the hot loop) |

`bpMul(v, bp) = trunc(v * bp / 10000)`, `bpDiv(v, bp) = trunc(v * 10000 / bp)`.
All combat arithmetic uses `long` in C# and integer `Number`s in JS.

## Squad aggregation

```
alive     = ceil(hpPool / hpPerUnit)
damage    → hpPool -= damage
if hpPool <= 0 → alive = 0, squad destroyed
heal      → hpPool = min(maxHpPool, hpPool + heal), alive recomputed
```

Summoned squads (`IsSummon`) are flagged and excluded from post-battle army losses. Siege and
mechanical squads are immune to morale routing.

## Tick order (`BattleSimulation.Step`)

1. `Tick++`
2. `BuildBuckets()` — spatial hash for AoE queries
3. `UpdateTimers()` — attack cooldowns, stuns, shock decay, hero mana regen (+2/tick), cooldowns
4. `UpdateRaceMechanics()` — Lich free summons, clergy/Serpent healing
5. `UpdateMorale()` every `morale.updateIntervalTicks` (5)
6. For each active squad: `AcquireTarget` → move toward target if out of range → `Attack` otherwise
7. `Cleanup()` — expire temporary summons
8. `ApplyHeroCollapse()` — heroes take damage when their whole army is gone
9. `CheckEnd()` — one side has no active squads, or `maxTicks` → compare remaining HP pools
10. Fold `(tick, count, Σhp, Σmorale, kills, routs)` into the rolling state hash

## Target acquisition

Nearest enemy squad by squared distance, with a virtual distance discount when the attacker's
role counters the target's role: `d = bpDiv(d, counterBp)` (e.g. cavalry vs line at 14500 bp
makes line units 31% "closer"). Routed, escaped and dead squads are skipped.

## Attack & damage

```
haste = bpMul(race.hasteBp, furyBp)
if buff haste active:      haste = bpMul(haste, 17000)
if magazine has ammo:      haste = bpMul(haste, 20000)   // double rate of fire
if magazine depleted:      haste = bpMul(haste,  6200)   // 1.6x interval
interval = max(1, attackIntervalTicks * BP / max(2000, haste))
if magazine has ammo: ammo--

damage = attack * alive
       × race.damageDealtBp
       × counterBp(attacker.role, defender.role)
       × (combat.moraleDamageFloorBp + morale * 5)        // 0.5x .. 1.0x
       × (BP + attackBuffBp) × (BP - intimidateBp)
       × ammoBurstBp | ammoDryBp | BP
       × rampartBp
       × criticalBonusBp (rare, seeded)
mitigation = min(defense * 100 + wallBonus, combat.defenseSoftCapBp)
damage = damage * BP / (BP + mitigation)
damage = bpMul(damage, resistBuffBp)
damage = bpMul(damage, targetRace.damageTakenBp)
if magic:
    resist = max(unit.magicResistBp, race.magicResistBp for magical/undead units)
    damage = bpMul(damage, BP - min(resist, combat.magicResistCapBp))
```

**Rampart rule:** in a siege with defenders on walls, an attacker hitting a `line` squad applies
its race `rampartBonusBp` (Sylvani = 16000). Defenders also gain `+1500` mitigation and
`setup.wallHp` bonus HP per unit.

5% of each attack's damage spills onto a nearby enemy hero within 1800 units; a hero at 0 HP
adds `morale.heroDeathShock` to its whole team.

## Morale & routing

```
lossBp    = (maxHpPool - hpPool) * BP / maxHpPool
casualty  = trunc(lossBp / 100) * morale.casualtyShockPerPercent
aura      = max over living allied heroes of (command * commandPerPoint)
            (halved outside combat.commandAuraRange)
morale    = clamp(baseValue + aura
                  - bpMul(casualty, race.moraleShockBp)
                  - shock - heroDeathShock, 0, 1000)
```

* `morale < routThreshold` → **routed**: the squad stops attacking, flees toward its own edge,
  and every nearby ally gains `allyRoutShock`.
* After `combat.routedEscapeTicks` it escapes: troops are recorded as *routed*, not *killed*.
* `morale >= rallyThreshold` for `rallyTicks` → the squad rallies (high Command makes this happen).
* `crusader_resurrection` (`buffStat: rally`) un-routes every allied squad instantly.
* Siege, mechanical and summoned squads never rout.

Post-battle (`ArmyState.ApplyLosses`): `killed` are permanent; `routed * race.injuryLossBp` are
permanent, the rest go to the Hospital queue (Lich 10%, Sylvani 15%, Human 50%).

## Race mechanics

| Race | Rule | Where |
|---|---|---|
| Human | Clergy heal, +15% morale shock | `UpdateRaceMechanics`, `UpdateMorale` |
| Dwarf | `ammoReserve + unit.ammo` volleys at 2× speed / 1.65× damage, then 0.62× speed / 0.68× damage; `double_charge` adds volleys | `Attack`, `ApplyBuffSkill` |
| Lich | Magical/undead frontline halves magic damage; Mummies raise `freeSummonCount` Marauders every `summonIntervalTicks`; 10% injury loss | `ApplyDamage`, `UpdateRaceMechanics` |
| Rakan | Every **manual** hero cast grants Fury: `+15%` haste per stack (max 3) for 60 ticks; high base speed | `CastSkill`, `FuryBp` |
| Sylvani | +60% damage vs line units on ramparts; Serpent `verdantHealBp`; 15% injury loss | `RampartBp`, `UpdateRaceMechanics` |

## Hero skills

`CastSkill(heroIndex, skillId, worldX, worldY)` validates alive/mana/cooldown, spends mana, sets
the cooldown, then dispatches on `kind`:

| kind | behaviour |
|---|---|
| `damage` | `power + bpMul(might, mightScaleBp) + bpMul(magic, magicScaleBp)`, scaled by `magicBp` buff, applied to enemy squads inside `radius` (or all enemies for `target: enemies`) |
| `stun` | as damage plus `stunTicks = durationTicks` |
| `heal` | up to 7 allied squads in radius, `+ (power + magic scale) * 10` HP |
| `buff` | applies `buffStat` to matching squads (`hasteBp/attackBp/hpBp/intimidateBp/resistBp/ammo/rally`), `magicBp` applies to the caster, `self` is limited to squads within 3000 of the hero |
| `summon` | spawns `summonCount` of `summonUnitId` at the target point, expiring after `durationTicks` |

Enemy team uses `AutoCast(team)` every 25 ticks: first ready skill at the enemy centroid.

## Determinism & report

`BattleReport` contains `outcome, ticks, stateHash, totalKilled, routedSquads, loot, heroXp,
outcomes[{team, unitId, deployed, survivors, killed, routed}], log[]`.

The harness verifies:

```
same seed + same setup + same cast schedule  ⇒  same stateHash
Command 70 → higher average morale and fewer routs than Command 0
Dwarf burst 27320 dmg vs depleted 4504 dmg in 30 ticks
Lich takes 334 magic damage where a Human takes 584
Sylvani watchers deal 12908 rampart damage vs Human archers 5841
Rakan Fury: 7 volleys vs 6 without a manual cast
200,000 soldiers simulate in 2 squad slots
```

## Differences between the two implementations

| | C# (Unity) | JS (preview / harness) |
|---|---|---|
| Storage | struct-of-arrays `int[]`/`long[]` | one object per squad (same aggregation) |
| AoE query | spatial hash buckets | linear scan (≤96 squads in practice) |
| Hash | FNV-1a 64-bit over 8 bytes/value | FNV-1a 32-bit reference hash |
| Purpose | shipping gameplay | playable preview + headless verification |

Formulas, tick order, data files and constants are identical; the JS hash is not expected to
equal the C# hash, only to be stable within the JS implementation.
