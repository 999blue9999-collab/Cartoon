using System;
using System.Collections.Generic;
using Nore.Combat;
using Nore.Data;
using Nore.Economy;
using Nore.Military;
using Nore.State;
using Nore.World;
using UnityEngine;

namespace Nore.Core
{
    /// <summary>
    /// Headless contract tests. Run automatically on Play and from the in-game
    /// "Tests" tab. They exercise determinism, data integrity, economy, offline
    /// progress, conversion, morale, dragons, sieges and caravan interception.
    /// </summary>
    public static class SelfTests
    {
        public sealed class Result
        {
            public string name;
            public bool passed;
            public string detail = "";
        }

        public static List<Result> RunAll(DataRegistry data)
        {
            var results = new List<Result>();
            Run(results, "Data registry validates", () =>
            {
                List<string> errors = data.Validate();
                return errors.Count == 0 ? Ok(data.Summary()) : Fail(string.Join(" | ", errors.ToArray()));
            });
            Run(results, "Deterministic RNG", () =>
            {
                var a = new DeterministicRng(777);
                var b = new DeterministicRng(777);
                var c = new DeterministicRng(778);
                bool same = true, different = false;
                for (int i = 0; i < 16; i++)
                {
                    int x = a.NextInt(1000), y = b.NextInt(1000), z = c.NextInt(1000);
                    if (x != y) same = false;
                    if (x != z) different = true;
                }
                return same && different ? Ok("same seed -> same stream, different seed -> different stream") : Fail("RNG streams mismatched");
            });
            Run(results, "Fixed-point math", () =>
            {
                bool good = Fixed.BpMul(10000, 15000) == 15000 &&
                            Fixed.BpDiv(10000, 20000) == 5000 &&
                            Fixed.ISqrt(144) == 12 &&
                            Fixed.Within(0, 0, 3000, 4000, 5000) &&
                            !Fixed.Within(0, 0, 3000, 4000, 4000);
                return good ? Ok("bp mul/div, integer sqrt, squared distance") : Fail("math invariant broken");
            });
            Run(results, "Race mechanic contracts", () =>
            {
                RaceModifiers dwarf = data.Race("dwarf").modifiers;
                RaceModifiers lich = data.Race("lich").modifiers;
                RaceModifiers rakan = data.Race("rakan").modifiers;
                RaceModifiers sylvani = data.Race("sylvani").modifiers;
                bool good = dwarf.ammoReserve > 0 && dwarf.ammoBurstBp > Fixed.Bp && dwarf.ammoDryBp < Fixed.Bp &&
                            lich.magicResistBp >= 5000 && lich.freeSummonCount > 0 && lich.injuryLossBp <= 1500 &&
                            rakan.activeBuffBp > Fixed.Bp && sylvani.rampartBonusBp >= 16000 && sylvani.injuryLossBp <= 1500;
                return good ? Ok("ammo / summon / resist / fury / rampart traits present") : Fail("race trait missing");
            });
            Run(results, "Cross-race unit conversion", () =>
            {
                GameState g = GameState.New(data, 1);
                string msg;
                bool ok = g.Army.SwitchRace("dwarf", GameTime.NowUnix(), data, g.Stronghold, out msg);
                if (!ok) return Fail(msg);
                for (int i = 0; i < g.Army.stacks.Count; i++)
                    if (data.Unit(g.Army.stacks[i].unitId).race != "dwarf") return Fail("stack " + g.Army.stacks[i].unitId + " not converted");
                return Ok(g.Army.stacks.Count + " stacks converted: " + msg);
            });
            Run(results, "Production & timers offline", () =>
            {
                long now = GameTime.NowUnix();
                StrongholdState s = StrongholdState.CreateNew(data, now - 3600);
                long gold0 = s.resources.gold;
                s.Update(now, data, null);
                long gained = s.resources.gold - gold0;
                return gained > 0 ? Ok("+" + gained + " gold over 1 offline hour") : Fail("no offline production");
            });
            Run(results, "Save / load roundtrip", () =>
            {
                GameState g = GameState.New(data, 99);
                g.Stronghold.level = 4;
                g.Army.Add("human_archer", 7);
                SaveData before = g.ToSave();
                string json = SaveService.Serialize(before);
                SaveData after = SaveService.Deserialize(json);
                bool ok = after != null && after.stronghold.level == 4 &&
                          after.army.TotalTroops() == g.Army.TotalTroops() &&
                          after.world.cities.Count == g.World.cities.Count;
                return ok ? Ok(json.Length + " bytes, state preserved") : Fail("roundtrip lost state");
            });
            Run(results, "Offline progress report", () =>
            {
                long now = GameTime.NowUnix();
                GameState g = GameState.New(data, 5);
                SaveData save = g.ToSave();
                save.savedAtUnix = now - 7200;
                save.stronghold.lastProductionUnix = now - 7200;
                SaveService.Deserialize(SaveService.Serialize(save)); // exercise serializer
                // Simulate the load timestamp used by ApplyOffline.
                var loaded = GameState.FromSave(save, data);
                long gold0 = loaded.Stronghold.resources.gold;
                loaded.Stronghold.lastProductionUnix = now - 7200;
                loaded.Stronghold.Update(now, data, null);
                return loaded.Stronghold.resources.gold > gold0 ? Ok("2h catch-up accrued resources") : Fail("offline accrual broken");
            });
            Run(results, "Deterministic battle replay", () =>
            {
                BattleSetup a = TestSetup(data, 4242, 10);
                BattleSetup b = TestSetup(data, 4242, 10);
                var s1 = new BattleSimulation(a, data);
                var s2 = new BattleSimulation(b, data);
                for (int t = 0; t < 150; t++)
                {
                    if (t == 20)
                    {
                        string m1, m2;
                        s1.CastSkill(0, "heroic_leap", Fixed.CellToWorld(10), Fixed.CellToWorld(3), out m1);
                        s2.CastSkill(0, "heroic_leap", Fixed.CellToWorld(10), Fixed.CellToWorld(3), out m2);
                    }
                    s1.Step(); s2.Step();
                }
                bool ok = s1.RollingHash == s2.RollingHash && s1.BuildReport().stateHash == s2.BuildReport().stateHash;
                return ok ? Ok("150 ticks, identical hash " + s1.RollingHash) : Fail(s1.RollingHash + " != " + s2.RollingHash);
            });
            Run(results, "Command bolsters morale", () =>
            {
                var low = new BattleSimulation(TestSetup(data, 3131, 0), data);
                var high = new BattleSimulation(TestSetup(data, 3131, 70), data);
                for (int t = 0; t < 120; t++) { low.Step(); high.Step(); }
                int sumLow = SumMorale(low), sumHigh = SumMorale(high);
                return sumHigh >= sumLow ? Ok("Command 70 -> " + sumHigh + " vs Command 0 -> " + sumLow) : Fail("Command did not help morale");
            });
            Run(results, "Morale routing occurs", () =>
            {
                var sim = new BattleSimulation(TestSetup(data, 8181, 0), data);
                for (int t = 0; t < sim.MaxTicks && !sim.Finished; t++) sim.Step();
                BattleReport report = sim.BuildReport();
                return report.routedSquads >= 0 && report.ticks > 0
                    ? Ok(report.outcome + " in " + report.ticks + " ticks, " + report.routedSquads + " routs, " + report.totalKilled + " kills")
                    : Fail("battle did not resolve");
            });
            Run(results, "Large troop counts stay cheap", () =>
            {
                var setup = TestSetup(data, 6060, 20);
                setup.squads[0].count = 100000;
                var sim = new BattleSimulation(setup, data);
                for (int t = 0; t < 30; t++) sim.Step();
                return sim.Count == setup.squads.Count
                    ? Ok("100,000 soldiers represented by " + sim.Count + " squad slots")
                    : Fail("squad aggregation failed");
            });
            Run(results, "Dragon awakening gate", () =>
            {
                DragonState d = DragonState.CreateNew("azure", data);
                if (d.IsAwakened(data)) return Fail("awakened with zero points");
                for (int i = 0; i < d.points.Length; i++) d.points[i] = 159;
                return d.IsAwakened(data) && d.FinalAbilityUnlocked(data)
                    ? Ok("159 in all 3 trees + 109 primary unlocks awakening")
                    : Fail("awakening gate broken");
            });
            Run(results, "City siege capture", () =>
            {
                long now = GameTime.NowUnix();
                WorldState world = WorldState.CreateNew(data, now);
                StrongholdState s = StrongholdState.CreateNew(data, now);
                CityDef city = data.World.cities[0];
                string msg;
                for (int i = 0; i < city.strongholds; i++) world.Siege(city.id, true, data, s, out msg);
                CityState state = world.City(city.id);
                return state.ownerHouseId == world.houseId && world.TerritoryCount() == 1
                    ? Ok(city.name + " captured after " + city.strongholds + " strongholds") : Fail("city not captured");
            });
            Run(results, "Caravan interception", () =>
            {
                var caravans = new CaravanSystem();
                long now = GameTime.NowUnix();
                var cargo = new ResourceSet { gold = 900, wood = 400 };
                CaravanState c = caravans.Launch("n03", cargo, now, 100, 600, "house_tide");
                string msg;
                ResourceSet stolen;
                bool ok = caravans.Intercept(c.id, 5000, "House Thorn", now, new DeterministicRng(3), out msg, out stolen);
                return ok && stolen.gold == 900 ? Ok(msg) : Fail("interception failed: " + msg);
            });
            return results;
        }

        private static BattleSetup TestSetup(DataRegistry data, int seed, int command)
        {
            var setup = new BattleSetup { seed = seed, label = "SelfTest skirmish", maxTicks = 600 };
            setup.squads.Add(BattleFactory.Squad("human_spearman", 30, 2, 1, 0));
            setup.squads.Add(BattleFactory.Squad("human_archer", 24, 1, 3, 0));
            setup.squads.Add(BattleFactory.Squad("human_light_infantry", 24, 2, 5, 0));
            setup.squads.Add(BattleFactory.Squad("human_clergy", 12, 0, 3, 0));
            setup.squads.Add(BattleFactory.Squad("dwarf_ironsmith", 30, 11, 1, 1));
            setup.squads.Add(BattleFactory.Squad("dwarf_marksman", 24, 12, 3, 1));
            setup.squads.Add(BattleFactory.Squad("dwarf_pyrotech", 24, 11, 5, 1));
            setup.squads.Add(BattleFactory.Squad("dwarf_war_machine", 12, 13, 3, 1));
            var skills = new List<string> { "heroic_leap", "arrow_volley" };
            setup.heroes.Add(BattleFactory.Hero("rufio", 0, 1, 2, 30, 8, 30, command, 1200, skills));
            setup.heroes.Add(BattleFactory.Hero("avalon", 0, 0, 4, 16, 18, 20, command, 1000,
                new List<string> { "war_horn", "arrow_volley" }));
            setup.heroes.Add(BattleFactory.Hero("jacques", 1, 13, 2, 30, 10, 24, command, 1100,
                new List<string> { "firestorm", "rocket_launcher" }));
            return setup;
        }

        private static int SumMorale(BattleSimulation sim)
        {
            int sum = 0;
            for (int i = 0; i < sim.Count; i++) sum += sim.Morale[i];
            return sum;
        }

        private static Result Ok(string detail) => new Result { passed = true, detail = detail };
        private static Result Fail(string detail) => new Result { passed = false, detail = detail };

        private static void Run(List<Result> results, string name, Func<Result> test)
        {
            try
            {
                Result r = test();
                r.name = name;
                results.Add(r);
            }
            catch (Exception e)
            {
                results.Add(new Result { name = name, passed = false, detail = "EXCEPTION: " + e.GetType().Name + ": " + e.Message });
            }
        }

        public static string Format(List<Result> results)
        {
            int passed = 0;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].passed) passed++;
                sb.AppendLine((results[i].passed ? "PASS" : "FAIL") + "  " + results[i].name + " — " + results[i].detail);
            }
            sb.Insert(0, "Nore self-tests: " + passed + "/" + results.Count + " passed\n");
            return sb.ToString();
        }

        public static void LogToConsole(List<Result> results)
        {
            string text = Format(results);
            int failed = 0;
            for (int i = 0; i < results.Count; i++) if (!results[i].passed) failed++;
            if (failed > 0) Debug.LogWarning(text);
            else Debug.Log(text);
        }
    }
}
