#!/usr/bin/env python3
"""Validate the shipped JSON data: schema keys plus every cross-file reference.

Run: python3 tools/validate_data.py
Mirrors DataRegistry.Validate() in C#, so a clean run here means a clean run in Unity.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "Assets" / "Resources" / "Data"
FILES = ["config", "buildings", "races", "units", "heroes", "skills", "gear", "dragons", "world"]

RESOURCE_KEYS = {"gold", "wood", "mana", "prosperity", "linari", "soulbits", "activity", "cityCredit"}
UNIT_REQUIRED = {"id", "race", "name", "role", "tier", "hp", "attack", "defense", "magicResistBp",
                 "damageType", "range", "attackIntervalTicks", "moveSpeed", "supply", "stackSize", "cost", "ammo", "tags"}
HERO_REQUIRED = {"id", "name", "role", "race", "maxTroopSlots", "energyCost", "stats", "skillIds", "gearSlots"}
SKILL_REQUIRED = {"id", "name", "heroId", "kind", "target", "manaCost", "cooldownTicks", "radius",
                  "power", "mightScaleBp", "magicScaleBp", "damageType", "durationTicks",
                  "magnitudeBp", "buffStat", "summonUnitId", "summonCount"}


def load() -> dict:
    out = {}
    for name in FILES:
        p = DATA / f"{name}.json"
        if not p.exists():
            print(f"MISSING {p}")
            sys.exit(1)
        out[name] = json.loads(p.read_text())
    return out


def ids(items):
    return {x["id"] for x in items}


def main() -> int:
    d = load()
    errors: list[str] = []
    warnings: list[str] = []

    def check(cond, msg):
        if not cond:
            errors.append(msg)

    cfg = d["config"]
    check(cfg.get("version") == 1, "config.version != 1")
    check(cfg.get("tickRate", 0) > 0, "config.tickRate <= 0")
    check(cfg["morale"].get("baseValue", 0) > 0, "config.morale.baseValue missing (C# field name)")
    check(len(cfg["grid"]) >= 4, "config.grid incomplete")

    buildings = d["buildings"]["items"]
    races = d["races"]["items"]
    units = d["units"]["items"]
    heroes = d["heroes"]["items"]
    skills = d["skills"]["items"]
    gear = d["gear"]["items"]
    dragons = d["dragons"]["items"]
    trees = d["dragons"]["trees"]
    world = d["world"]

    b_ids, r_ids, u_ids = ids(buildings), ids(races), ids(units)
    h_ids, s_ids, g_ids = ids(heroes), ids(skills), ids(gear)
    d_ids, t_ids = ids(dragons), ids(trees)
    k_ids = ids(world["kingdoms"])
    c_ids = ids(world["cities"])
    n_ids = ids(world["nodes"])

    for label, collection in [("buildings", buildings), ("races", races), ("units", units),
                              ("heroes", heroes), ("skills", skills), ("gear", gear),
                              ("dragons", dragons), ("trees", trees), ("kingdoms", world["kingdoms"]),
                              ("cities", world["cities"]), ("nodes", world["nodes"]),
                              ("pvpModes", world["pvpModes"]), ("events", world["events"]),
                              ("houses", world["houses"])]:
        check(len({x["id"] for x in collection}) == len(collection), f"duplicate ids in {label}")

    for b in buildings:
        check(set(b.get("cost", {})) <= RESOURCE_KEYS, f"building {b['id']}: unknown cost key")
        for e in b.get("effects", []):
            check("baseValue" in e and "perLevel" in e, f"building {b['id']}: effect needs baseValue/perLevel (C# reserved word 'base')")
        check(b.get("maxLevel", 0) >= b.get("unlockStrongholdLevel", 99), f"building {b['id']}: max < unlock")

    for r in races:
        check(r.get("homeKingdom") in k_ids, f"race {r['id']}: unknown homeKingdom {r.get('homeKingdom')}")
        mods = r.get("modifiers", {})
        for key in ["damageDealtBp", "damageTakenBp", "magicResistBp", "moraleShockBp",
                    "injuryLossBp", "ammoReserve", "ammoBurstBp", "ammoDryBp", "rampartBonusBp",
                    "activeBuffBp", "clergyHealBp", "speedBp", "hasteBp", "freeSummonCount",
                    "summonIntervalTicks", "summonUnitId", "verdantHealBp"]:
            if key not in mods:
                errors.append(f"race {r['id']}: missing modifier {key}")
        for uid in r["unitIds"]:
            check(uid in u_ids, f"race {r['id']}: unknown unit {uid}")
        if mods.get("summonUnitId"):
            check(mods["summonUnitId"] in u_ids, f"race {r['id']}: unknown summon unit")

    groups = d["races"]["conversionChart"]
    for grp in groups:
        check(len(grp["entries"]) == len(races), f"conversion {grp['id']}: must cover all {len(races)} races")
        check({e["race"] for e in grp["entries"]} == r_ids, f"conversion {grp['id']}: race set mismatch")
        for e in grp["entries"]:
            check(e["unitId"] in u_ids, f"conversion {grp['id']}: unknown unit {e['unitId']}")
            if e["unitId"] in u_ids:
                u = next(x for x in units if x["id"] == e["unitId"])
                check(u["race"] == e["race"], f"conversion {grp['id']}: {e['unitId']} is {u['race']}, not {e['race']}")
        check(set(grp["cost"]) <= RESOURCE_KEYS, f"conversion {grp['id']}: unknown cost key")

    race_units = {r: 0 for r in r_ids}
    for u in units:
        missing = UNIT_REQUIRED - set(u)
        check(not missing, f"unit {u['id']}: missing keys {sorted(missing)}")
        check(u["race"] in r_ids, f"unit {u['id']}: unknown race {u['race']}")
        check(u["damageType"] in {"physical", "magic"}, f"unit {u['id']}: bad damageType")
        check(u["hp"] > 0 and u["attack"] > 0 and u["attackIntervalTicks"] > 0,
              f"unit {u['id']}: non-positive combat stats")
        check(set(u["cost"]) <= RESOURCE_KEYS, f"unit {u['id']}: unknown cost key")
        race_units[u["race"]] += 1
    for r, count in race_units.items():
        check(count >= 6, f"race {r}: only {count} units (need >= 6 roles)")

    for h in heroes:
        missing = HERO_REQUIRED - set(h)
        check(not missing, f"hero {h['id']}: missing keys {sorted(missing)}")
        check(h["race"] in r_ids, f"hero {h['id']}: unknown race")
        check(set(h["stats"]) == {"might", "magic", "stamina", "command"}, f"hero {h['id']}: bad stats")
        for sid in h["skillIds"]:
            check(sid in s_ids, f"hero {h['id']}: unknown skill {sid}")
        check(0 <= h["maxTroopSlots"] <= 3, f"hero {h['id']}: troopSlots out of range")
        check(h["energyCost"] >= 1, f"hero {h['id']}: energyCost < 1")

    for s in skills:
        missing = SKILL_REQUIRED - set(s)
        check(not missing, f"skill {s['id']}: missing keys {sorted(missing)}")
        check(s["kind"] in {"damage", "heal", "buff", "summon", "stun"}, f"skill {s['id']}: bad kind")
        check(s["target"] in {"point", "allies", "enemies", "self"}, f"skill {s['id']}: bad target")
        if s["heroId"]:
            check(s["heroId"] in h_ids, f"skill {s['id']}: unknown hero {s['heroId']}")
        if s["summonUnitId"]:
            check(s["summonUnitId"] in u_ids, f"skill {s['id']}: unknown summon unit {s['summonUnitId']}")
        if s["kind"] == "summon":
            check(s["summonCount"] > 0, f"skill {s['id']}: summon with no count")

    for item in gear:
        check(item["slot"] in {"weapon", "head", "armor", "artifact", "trinket", "prism"},
              f"gear {item['id']}: unknown slot")
        check(item["stat"] in {"might", "magic", "stamina", "command"}, f"gear {item['id']}: unknown stat")

    for dr in dragons:
        check(dr["primaryTree"] in t_ids, f"dragon {dr['id']}: unknown primaryTree")
        check(len(dr["tiers"]) >= 2, f"dragon {dr['id']}: missing rarity tiers")
    for tree in trees:
        points = [m["points"] for m in tree["milestones"]]
        check(points == sorted(points), f"tree {tree['id']}: milestones not sorted")
    check(d["dragons"]["awakening"]["allTreesPoints"] == 159, "awakening.allTreesPoints should be 159")
    check(d["dragons"]["awakening"]["primaryTreePoints"] == 109, "awakening.primaryTreePoints should be 109")

    for c in world["cities"]:
        check(c["kingdom"] in k_ids, f"city {c['id']}: unknown kingdom")
        check(c["strongholds"] > 0, f"city {c['id']}: no strongholds")
    for n in world["nodes"]:
        check(n["kind"] in {"gold", "wood", "mana", "enemy", "event", "boss", "caravan"},
              f"node {n['id']}: unknown kind")
        if n["kingdom"]:
            check(n["kingdom"] in k_ids, f"node {n['id']}: unknown kingdom")
        if n["enemyRace"]:
            check(n["enemyRace"] in r_ids, f"node {n['id']}: unknown enemyRace")
        check(set(n["reward"]) <= RESOURCE_KEYS, f"node {n['id']}: unknown reward key")

    # C# reserved-word guard: keys must not be C# keywords.
    keywords = {"base", "class", "event", "namespace", "params", "string", "int", "bool", "float",
                "default", "interface", "switch", "case", "new", "public", "private", "static"}
    def keyword_scan(obj, where):
        if isinstance(obj, dict):
            for k, v in obj.items():
                if k in keywords:
                    errors.append(f"{where}: JSON key '{k}' is a C# keyword")
                keyword_scan(v, f"{where}.{k}")
        elif isinstance(obj, list):
            for i, v in enumerate(obj):
                keyword_scan(v, f"{where}[{i}]")
    for name, payload in d.items():
        keyword_scan(payload, name)

    # Balance hints (non-fatal).
    for r in races:
        if r["id"] == "dwarf" and not (r["modifiers"]["ammoBurstBp"] > r["modifiers"]["ammoDryBp"]):
            warnings.append("dwarf burst should exceed dry damage")
        if r["id"] == "lich" and r["modifiers"]["magicResistBp"] < 5000:
            warnings.append("lich magic resistance should be >= 50%")

    print(f"Data: {len(buildings)} buildings · {len(races)} races · {len(units)} units · "
          f"{len(heroes)} heroes · {len(skills)} skills · {len(gear)} gear · {len(dragons)} dragons · "
          f"{len(world['cities'])} cities · {len(world['nodes'])} nodes")
    print(f"Unit allocation per race: {race_units}")
    for w in warnings:
        print("WARN  " + w)
    if errors:
        for e in errors:
            print("FAIL  " + e)
        print(f"\n{len(errors)} data error(s).")
        return 1
    print("\nAll data files valid — references, schemas, reserved words and balance contracts OK.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
