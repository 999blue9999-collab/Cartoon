# Nore — an "Art of Conquest"-style MMORTS/RPG prototype (Unity 6 / C#)

A from-scratch, playable prototype of the systems described in your brief: stronghold economy,
five races with role counters and cross-race conversion, heroes and dragons, deterministic
real-time grid combat with morale/routing and manual hero skill casting, a semi-open world with
city sieges / House territory / PvP hooks, externalized data, save + offline progress, and
performance-oriented aggregated squads.

> **Scope decision.** The template placeholders were left unfilled, so I used these defaults:
> **Unity 6 / C#**, **PC-first (mobile-friendly IMGUI layout)**, and the deliverable is the
> **whole playable prototype** — architecture + data schemas + working real-time grid combat and
> morale + every other system stubbed in at a genuinely functional level. Everything is local
> and single-player; `INetworkHooks` is the seam for a real MMO backend.
>
> **IP note.** This is an original implementation inspired by the *mechanics* in your brief.
> It ships **no** Lilith Games art, audio, text assets or code. Names from the brief are used as
> data labels for a private prototype; replace them if you publish anything.

---

## 1. Run it

### Unity (primary deliverable)
1. Open this repository folder with **Unity 6 (6000.0.x)**. No packages, prefabs or scene setup are
   needed — `GameController` bootstraps itself via `[RuntimeInitializeOnLoadMethod]`.
2. Press **Play**. The IMGUI window is the whole game.
3. Optional menus: **Nore → Create Demo Scene**, **Nore → Validate Data**, **Nore → Run Self Tests**,
   and **Nore → Cloud Build → Ensure Build Scene / Verify Project / Build Player**.
   A committed empty scene (`Assets/Scenes/NoreDemo.unity`) is already enabled in
   `ProjectSettings/EditorBuildSettings.asset`, so Player/cloud builds need no scene authoring.

Tabs: **Overview · Stronghold · Army · Heroes · Dragons · Battle · World · System**.

| Tab | What you can do |
|---|---|
| Overview | Resources, realm stats, offline report, quick skirmish vs any of the 5 races, siege |
| Stronghold | Upgrade 14 buildings with costs, timers, Prosperity, speed-ups; launch/intercept caravans |
| Army | Switch race (conversion chart), train units, set deployment cells, view training/hospital queues |
| Heroes | Might/Magic/Stamina/Command, skill points, 6 gear slots, Energy, chests, shop-only Jacques, formation |
| Dragons | 4 elemental dragons, 3 talent trees, 109/159 milestones, awakening |
| Battle | Live 14×8 grid, unit stacks, morale/HP bars, **click a hero skill then click a grid cell to cast**, pause/x1/x2/x4, event log, state hash, result |
| World | 18 map nodes (loot / events / bosses / caravans), 12 cities with stronghold sieges, Houses, 6 PvP modes, 5 events |
| System | Save/Load/New realm, export JSON overrides, 15 self-tests, event log |

### Unity Cloud / Build Automation
The repo is cloud-build-ready: committed empty scene + build settings GUID chain, an
`[InitializeOnLoad]` safety net (`Assets/Editor/CloudBuildSetup.cs`), and `-executeMethod` entry
points for a verify gate and a Player build. Creating the cloud project, connecting the Git repo
and starting a build are **dashboard-only** steps — see [`docs/UNITY_CLOUD.md`](docs/UNITY_CLOUD.md).
Arena does not provide a Unity Cloud connector, so those steps must be done in the Unity Dashboard
(Project Settings → Services to link a Project ID; Build Automation → Source Control to connect
GitHub; build target at Unity `6000.0.32f1`).

```bash
# optional local/CI batch verification + build
Unity -batchmode -quit -executeMethod Nore.EditorTools.CloudBuildSetup.Verify
Unity -batchmode -quit -executeMethod Nore.EditorTools.CloudBuildSetup.Build
```

### Browser companion (no Unity install needed)
```bash
cd game
node server.js            # → http://localhost:3000   (PORT=8080 node server.js to change)
```
`game/server.js` is a dependency-free static server that serves the repository root (so the
companion can `fetch` the shared JSON in `Assets/Resources/Data`) and redirects `/` to
`/preview/`. It binds `0.0.0.0`, honours `PORT`/`HOST`, refuses dotfiles (`.git`, saves) and
path traversal, and adds `GET /healthz`. Any static file server works just as well, e.g.
`python3 -m http.server 8000 --bind 0.0.0.0` → `http://localhost:8000/preview/`.

The companion loads the **same JSON files** and a JS reference implementation of the same combat
spec (`preview/sim.mjs`), so you can play the loop and inspect morale/ammo/counters immediately.
Unity's C# `BattleSimulation` is authoritative; the JS mirror exists for headless verification.

---

## 2. Requirement → implementation map

| # | Requirement | Where |
|---|---|---|
| 1 | Stronghold & economy: upgrades + timers, Prosperity, Gold, Wood, Mana, caravans + interception | `Assets/Scripts/Economy/Stronghold.cs`, `Assets/Resources/Data/buildings.json`, `StrongholdPanel` |
| 2 | 5 races, role counters, Dwarf ammo + mechs, Lich 50% magic resist + free summons, Rakan active Fury, Sylvani rampart bonus + low injury loss, conversion chart | `races.json`, `units.json` (35 units), `BattleSimulation.UpdateRaceMechanics/RampartBp/ApplyDamage`, `ArmyState.SwitchRace` |
| 3 | Heroes: 4 stats, skill points, 6 gear slots, Energy, troop-slot limits (Avalon 3 / Bane 1 / Fenris 0), dragons with elements, talent trees, awakening | `heroes.json` (17), `skills.json` (23), `gear.json`, `dragons.json`, `Heroes.cs`, `Dragons.cs` |
| 4 | Real-time tactical combat: grid deployment, tick simulation, manual casting, counters, Morale/routing tied to Command | `Combat/*`, `BattlePanel.cs`, `docs/COMBAT_SPEC.md` |
| 5 | World map & modes: exploration, city sieges, House territory, PvP/event hooks | `World/WorldState.cs`, `world.json`, `WorldPanel.cs`, `INetworkHooks` |
| 6 | Data-driven stats, deterministic tick state, save/load + offline, large-troop performance | `Data/*`, `Core/*`, `SaveGame.cs`, struct-of-arrays squads + spatial hash |

---

## 3. Architecture

```
                    Assets/Resources/Data/*.json   (9 files, overrideable at runtime)
                                  │
                          DataRegistry (validate + index)
                                  │
   ┌──────────┬──────────┬───────┴───────┬──────────┬────────────┐
   │ Economy  │ Military │    Combat     │  World   │    Core     │
   │ strong-  │ army /   │ BattleSimu-   │ nodes /  │ Fixed math, │
   │ hold,    │ heroes / │ lation (10Hz  │ cities / │ xorshift32, │
   │ caravans │ dragons  │ fixed ticks)  │ PvP      │ save/offline│
   └──────────┴──────────┴───────┬───────┴──────────┴────────────┘
                                  │
                    GameState (owns all mutable state)
                                  │
             GameController (MonoBehaviour) + RuntimeUI (IMGUI)
```

* **Integer-only gameplay math.** Multipliers are basis points (`10000 = 1.00x`), positions are
  fixed point (`Q = 1000` per grid cell). No floats, no `UnityEngine.Random`, no per-platform drift.
* **Determinism.** `DeterministicRng` (xorshift32) + a fixed-tick accumulator + a rolling FNV-1a
  state hash. Same seed, deployment and skill-cast schedule ⇒ same report hash.
* **Aggregated squads.** One squad = a stack of N identical troops with an HP pool, so 100,000
  soldiers occupy one array slot. Struct-of-arrays + a spatial hash for AoE queries; no
  `GameObject` per soldier, no per-tick allocations (logs excepted).
* **Offline progress.** All timers are absolute Unix seconds. Loading a save computes a capped
  (8 h) catch-up for production, completed upgrades, training, hospital, Energy and caravans.
* **Externalized data.** JSON ships in `Assets/Resources/Data`; `DataRegistry` also reads
  `Application.persistentDataPath/Data/*.json` overrides without recompiling
  (**Nore → Export data overrides** / System tab).

See `docs/ARCHITECTURE.md`, `docs/DATA_SCHEMA.md` and `docs/COMBAT_SPEC.md` for the details.

---

## 4. Verification (all run in this workspace)

| Command | Result |
|---|---|
| `python3 tools/validate_data.py` | ✅ 9 JSON files, schemas, reserved-word guard, all cross-references, 35 conversion mappings |
| `python3 tools/validate_unity_project.py` | ✅ 30 checks: scene/meta GUID chain, `EditorBuildSettings`, Unity version, cloud build hooks |
| `node tools/syntax_check.mjs` | ✅ 20/20 C# files parse cleanly (tree-sitter C# grammar) |
| `node tools/sim_harness.mjs` | ✅ 9/9 combat checks (determinism, ammo, Lich resist, Command morale, Sylvani ramparts, Rakan Fury, 200k troops, offline math, conversion) |
| `node tools/dom_smoke.mjs` | ✅ 12/12 browser UI smoke checks (boot, deploy, cast, upgrade, race switch, explore, tests) |
| `node tools/server_smoke.mjs` | ✅ 12/12 `game/server.js` checks (redirect, preview assets, 9/9 data files, 404/403/405, traversal) |
| Unity Play | `Nore.Core.SelfTests` runs 15 checks automatically; results in the **System** tab / console |

`tools/syntax_check.mjs` needs a one-time `cd tools/.syntax && npm install --build-from-source tree-sitter@0.25.1 tree-sitter-c-sharp@0.24.0`;
`tools/dom_smoke.mjs` needs `cd tools/.dom && npm install jsdom`. Generated `node_modules` are gitignored.

The harness also prints a race matchup matrix into `tools/out/harness-report.json`. Balance is
intentionally a work in progress (the Dwarf ammo opening is very strong — that is the design in the
brief). Every number lives in JSON; tune `races.json` / `units.json` and re-run the harness.

---

## 5. File map

```
Assets/
  Resources/Data/    config, buildings, races, units, heroes, skills, gear, dragons, world (.json)
  Scripts/
    Core/            CoreMath.cs, SaveGame.cs, SelfTests.cs
    Data/            Models.cs, DataRegistry.cs
    Economy/         Stronghold.cs
    Military/        Army.cs, Heroes.cs, Dragons.cs
    Combat/          CombatTypes.cs, BattleSimulation.cs
    World/           WorldState.cs
    Runtime/         GameController.cs, RuntimeUI.cs, Widgets.cs, PanelsBase.cs,
                     PanelsRpg.cs, BattlePanel.cs
  Editor/            NoreMenu.cs, CloudBuildSetup.cs
  Scenes/            NoreDemo.unity (+ .meta)   (empty committed build scene)
ProjectSettings/     ProjectVersion.txt, EditorBuildSettings.asset
Packages/manifest.json
preview/             index.html, style.css, app.js, sim.mjs   (browser companion)
game/                server.js            (zero-dependency static host for the browser companion)
tools/               validate_data.py, validate_unity_project.py, syntax_check.mjs, sim_harness.mjs,
                     dom_smoke.mjs, server_smoke.mjs
docs/                ARCHITECTURE.md, DATA_SCHEMA.md, COMBAT_SPEC.md, UNITY_CLOUD.md
```

---

## 6. Known limitations / next steps

* Single-player and offline: no server, matchmaking, accounts, anti-cheat or persistence beyond
  the local JSON save. `INetworkHooks` marks the integration boundary.
* No 3D/2D art, animation, VFX or audio — the UI is code-generated IMGUI (Unity) and canvas/DOM
  (browser) so the prototype runs with zero asset imports.
* Research/Academy, gear drops, hero cards, dragon riding VFX and the full 18-level Stronghold
  curve are data-shaped hooks rather than fully tuned content trees.
* World duels / Nexus / Clash of Fate are deterministic local scoring hooks, not live 20v20 services.
* City garrison race is currently fixed to Dwarf in the Unity controller; make it data-driven in
  `world.json` when you add per-city armies.
* Balance: see the harness matrix; Dwarf's opening burst dominates short fights. Tune via JSON.
