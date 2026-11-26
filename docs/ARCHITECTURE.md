# Architecture

## Layers

| Layer | Namespace | Responsibility | Engine dependency |
|---|---|---|---|
| Core | `Nore.Core` | fixed-point math, xorshift32 RNG, FNV-1a hash, Unix clock, self-tests | none |
| Data | `Nore.Data` | serializable DTOs, `DataRegistry` (load/index/validate) | `JsonUtility`, `Resources`, `Application` only |
| Economy | `Nore.Economy` | `StrongholdState`, `CaravanSystem`, timers, production | none |
| Military | `Nore.Military` | `ArmyState`, `HeroManager`/`HeroState`, `DragonManager`/`DragonState` | none |
| Combat | `Nore.Combat` | `BattleSetup`, `BattleSimulation`, `BattleReport` | none |
| World | `Nore.World` | `WorldState`, exploration, sieges, House, PvP hooks | none |
| State | `Nore.State` | `GameState` aggregate, `SaveData`, `SaveService`, `OfflineReport` | `Application.persistentDataPath` |
| Runtime | `Nore.Runtime` | `GameController` (MonoBehaviour), IMGUI panels | Unity |
| Editor | `Nore.EditorTools` | validation menu, demo scene | UnityEditor |

Everything below `Runtime` is plain C# and can be unit-tested or moved into a headless server
without touching `UnityEngine`. `SelfTests.RunAll()` is exactly that: 15 headless contract tests.

## Boot sequence

```
RuntimeInitializeOnLoadMethod(AfterSceneLoad)
  → create GameObject("Nore…") + GameController + RuntimeUI
  → GameController.Awake
      DataRegistry.LoadDefault()              (Resources/Data + persistentDataPath overrides)
      Data.Validate()                         (log errors, do not crash)
      SaveService.Load() ? GameState.FromSave + ApplyOffline() : GameState.New()
      SelfTests.RunAll(Data) → console + System tab
  → Update()
      once/sec: GameState.Tick(now)  → economy / training / hospital / energy / caravans / daily reset
      fixed accumulator (1/tickRate): Battle.Step()
          + Battle.AutoCast(defender) every 25 ticks
      on finish: ResolveBattle() → ApplyLosses, loot, hero XP, siege progress, autosave
```

## Determinism contract

1. All gameplay state is integers (`int`/`long`). Multipliers are basis points; positions are
   `Q = 1000` fixed-point units per grid cell.
2. Randomness comes only from `DeterministicRng(seed)`; `UnityEngine.Random` is never used.
3. `BattleSimulation.Step()` is the only mutator; the UI never writes simulation arrays.
4. A cast schedule is part of the input: `CastSkill(hero, skill, x, y)` at tick T is recorded in
   the log and changes the rolling hash.
5. `Hashing.Fnv1a` folds `(tick, squadCount, Σhp, Σmorale, kills, routs)` every tick into a
   rolling 64-bit hash; `BattleReport.stateHash` is the replay fingerprint.

To replay a battle: same `config.json`, same data files, same `BattleSetup.seed`, same
`BattleSetup.squads/heroes` order, same cast schedule → identical `stateHash`.

## Performance design

* **Aggregated squads.** A squad holds `maxAlive`, `alive`, `hpPool`, `maxHpPool`, `hpPerUnit`.
  Damage reduces a pool; `alive = ceil(pool / hpPerUnit)`. 100,000 troops = 1 squad slot.
* **Struct of arrays.** `BattleSimulation` exposes parallel `int[]`/`long[]` arrays for every
  field, sized `config.maxSquads` (96) — cache-friendly, no per-soldier objects.
* **Spatial hash.** `BuildBuckets()` buckets squads into 2-cell cells; `Query(cx, cy, radius)`
  walks only overlapping buckets into one reused `List<int>` buffer (zero allocations) for
  hero AoE skills and clergy healing. Target acquisition is an O(n²) scan over ≤96 squads,
  which is cheaper than maintaining a dynamic nearest-neighbour structure at this scale.
* **No per-tick allocations.** Only `AddLog` allocates strings, and the log is capped at 240 lines.
* **Frame-rate independence.** `GameController.Update()` uses `Time.unscaledDeltaTime` with a
  fixed accumulator and a 60-step guard; `battleSpeed` multiplies the accumulator, not the tick.

## Save / offline

`SaveData` is a flat `[Serializable]` object (no dictionaries, no Unity object references) written
to `Application.persistentDataPath/nore_save.json` with `JsonUtility`.

```
SaveData { version, savedAtUnix, rngSeed, profileName,
           stronghold, army, heroes, dragons, caravans, world }
```

Timers are absolute Unix seconds (`upgradeFinishUnix`, `finishUnix`, `arriveUnix`,
`lastEnergyUnix`, `lastProductionUnix`). `GameState.ApplyOffline(now)` clamps elapsed time to
`config.offline.maxCatchupSeconds` (8 h), shifts the production baseline forward, then calls the
same idempotent `Update` methods used online. That makes "offline progress" a pure function of
the saved timestamps — no wall-clock simulation loop, no drift, no double-credit.

## Networking seam

`Nore.World.INetworkHooks` has exactly three methods (`ReportBattleResult`, `ClaimCity`,
`InterceptCaravan`). A real MMORTS backend would:

1. validate the battle seed + `stateHash` server-side against a deterministic sim replay,
2. own city/House territory and caravan state authoritatively,
3. push world events, and keep the client's `GameState` as a replicated view.

The rest of the code (data, combat, economy) is already engine- and transport-agnostic.

## Extending

* **New unit/race:** add rows to `units.json` + `races.json` and a conversion-chart entry.
  Run `python3 tools/validate_data.py`.
* **New hero skill:** add a `skills.json` row; `BattleSimulation.ApplyOffensiveSkill` /
  `ApplyHealSkill` / `ApplyBuffSkill` / `SpawnSummon` handle the five `kind` values.
* **New building effect:** add a `stat` to `effects`; read it with
  `StrongholdState.Effect(stat, data)` anywhere.
* **New tick mechanic:** add it to `BattleSimulation.UpdateRaceMechanics()` and keep it integer-only.
* **New PvP mode:** add a `pvpModes` row; `WorldState.RunPvp` reads it generically.
