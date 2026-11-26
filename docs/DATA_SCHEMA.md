# Data schema

Nine JSON files live in `Assets/Resources/Data/`. Unity imports them as `TextAsset`;
`DataRegistry.LoadDefault()` also checks `Application.persistentDataPath/Data/<name>.json` first,
so you can override any number at runtime without recompiling.

Conventions:

* **`...Bp`** = basis points: `10000 = 1.00x`, `16500 = 1.65x`, `5000 = 0.50x`.
* **`range`, `radius`, `moveSpeed`, `x/y` positions** = fixed-point grid units (`Q = 1000` per cell).
* **`ResourceSet`** = `{ gold, wood, mana, prosperity, linari, soulbits, activity, cityCredit }`
  (missing keys default to `0`).
* **`baseValue`** (not `base`) is used in JSON because `base` is a C# keyword; `tools/validate_data.py`
  rejects any C# keyword used as a key.
* JsonUtility cannot deserialize dictionaries or top-level arrays, so every list is wrapped in
  `{ "items": [...] }` and key→value maps are `List<{key,value}>`-style entry arrays.

## config.json
`version, tickRate (10 Hz), maxBattleTicks, maxSquads, grid {columns=14, rows=8,
attackerMaxColumn=4, defenderMinColumn=9}, morale {...}, combat {...}, roleCounters[],
offline {...}, start {...}, training {...}, progression {...}`.

`roleCounters` is a list of `{attacker: role, defender: role, bp}`; missing pairs default to 10000.

| morale key | meaning |
|---|---|
| `baseValue` | starting morale (0..1000) |
| `commandPerPoint` | aura points per hero Command stat |
| `casualtyShockPerPercent` | morale lost per 1% HP-pool losses |
| `routThreshold` / `rallyThreshold` | morale below/above which squads rout / rally |
| `allyRoutShock`, `heroDeathShock` | one-off shock on nearby rout / hero death |

## buildings.json
`items[]`: `id, name, category, maxLevel, unlockStrongholdLevel, cost, costGrowthBp,
buildSeconds, buildGrowthBp, prosperityPerLevel, effects[{stat, baseValue, perLevel}]`.

`value(level) = baseValue + perLevel * level`. Upgrade `n` costs
`cost * (costGrowthBp/10000)^(n-1)` and takes `buildSeconds * (buildGrowthBp/10000)^(n-1)`.

Stats consumed by code: `troopCapacity, heroLevelCap, caravanBonusBp, wallHp, buildingSlots,
goldPerMinute, woodPerMinute, manaPerMinute, storageCap, trainSpeedBp, researchSlots,
hospitalCap, healRateBp, defenseBp, rampartHp, garrisonSlots, ammoReserve, moraleBonus`.

## races.json
`items[]`: `id, name, playstyle, homeKingdom, primaryColor, summary, modifiers{...}, traits[],
unitIds[]`.

`modifiers` (all five races must define every key so JsonUtility defaults never silently become 0):

| key | effect |
|---|---|
| `damageDealtBp`, `damageTakenBp` | global damage multipliers |
| `magicResistBp` | magic reduction for units tagged `magical`/`undead` (Lich = 5000) |
| `moraleShockBp` | multiplier on casualty morale shock (Human = 11500) |
| `injuryLossBp` | fraction of routed troops permanently lost (Lich 1000, Sylvani 1500) |
| `ammoReserve`, `ammoBurstBp`, `ammoDryBp` | Dwarf magazine size, burst multiplier, depleted multiplier |
| `rampartBonusBp` | Sylvani damage vs line units on walls (16000) |
| `activeBuffBp` | Rakan Fury attack-speed bonus per stack (11500) |
| `clergyHealBp`, `verdantHealBp` | periodic heal as bp of max HP pool |
| `speedBp`, `hasteBp` | movement / attack-rate multipliers |
| `freeSummonCount`, `summonIntervalTicks`, `summonUnitId` | Lich Mummy loop |

`conversionChart[]`: `{id, label, cost, entries[{race, unitId}]}` — 7 groups × 5 races = 35
mappings. `ArmyState.SwitchRace` charges `group.cost * training.conversionCostBp` per 10 troops.

## units.json
35 units (7 per race: heavy, line, light, cavalry, clergy, siege, elite). Fields:
`id, race, name, role, tier, hp, attack, defense, magicResistBp, damageType (physical|magic),
range, attackIntervalTicks, moveSpeed, supply, stackSize, cost, ammo, tags[], special`.

`tags` drive mechanics: `siege`/`mechanical` are morale-immune, `magical`/`undead` get the race
magic resist, `ammo` units start with a magazine, `healer`/`summoner` map to role `clergy`.

## heroes.json
17 heroes: `id, name, role, race, maxTroopSlots, energyCost, stats{might,magic,stamina,command},
skillIds[], gearSlots[], unlock, description`.

Slot exceptions from the brief: Avalon 3, Bane 1, Fenris 0; Jacques/Floras cost 3 Energy;
Jacques is `unlock: "shop"` (never appears in chests).

## skills.json
23 skills: `id, name, heroId, kind (damage|heal|buff|summon|stun),
target (point|allies|enemies|self), manaCost, cooldownTicks, radius, power,
mightScaleBp, magicScaleBp, damageType, durationTicks, magnitudeBp, buffStat,
summonUnitId, summonCount, description`.

`buffStat` values: `hasteBp, attackBp, hpBp, intimidateBp, resistBp, ammo, magicBp, rally`.
Damage = `power + bpMul(might, mightScaleBp) + bpMul(magic, magicScaleBp)`, then target
defence/magic-resist applies. `durationTicks` also expires summons.

## gear.json
14 items: `id, name, slot, stat (might|magic|stamina|command), value, rarity, description`.
`HeroState.GearBonus` sums equipped item values per stat.

## dragons.json
`trees[]`: `{id, name, description, milestones[{points, label, stat, valueBp}]}` for
protection / destruction / support (milestones 25, 50, 109, 159).
`items[]`: `{id, name, element, role, primaryTree, baseAttack, baseHp, tiers[], ability, description}`
for Azure (ice), Ruby (fire), Jade (poison), Onyx (lava).
`awakening`: `{allTreesPoints: 159, primaryTreePoints: 109, bonusHpBp, bonusAttackBp,
bonusCommandBonus, bonusLabel}`. `DragonState.IsAwakened` requires all three trees ≥159 and the
primary tree ≥109 (which unlocks that tree's final ability).

## world.json
`regions[], kingdoms[6], cities[12], nodes[18], pvpModes[6], events[5], houses[3]`.

* `kingdoms`: `id, name, color, location, bonus, description` (Athaly, Dunwolf, Hyral, Icarum,
  Neferak, Skarn).
* `cities`: `id, name, kingdom, tier, strongholds, garrisonPower, resourceBonus, x, y`.
  A city is captured only when `strongholdsRemaining` reaches 0.
* `nodes`: `id, name, kind (gold|wood|mana|enemy|event|boss|caravan), region, kingdom, x, y,
  reward, enemyRace, label`. Enemy/boss nodes launch a battle; caravan nodes launch transport.
* `pvpModes`: arena, territory war, world duel, duel tower, Nexus, Clash of Fate.
* `events`: Balmford Swamp, daily activity, Void, awakening, Lucky Wheel.

## Validation

`DataRegistry.Validate()` (C#) and `tools/validate_data.py` (Python) check: duplicate ids,
schema keys, all cross-file references, conversion-chart completeness, unit/race consistency,
skill summon targets, dragon primary trees, node/city kingdoms, and C# keyword keys. Run them
after every data edit.
