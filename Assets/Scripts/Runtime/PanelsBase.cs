using System;
using System.Collections.Generic;
using Nore.Core;
using Nore.Data;
using Nore.Economy;
using Nore.State;
using UnityEngine;

namespace Nore.Runtime
{
    public static class OverviewPanel
    {
        public static void Draw(GameController g)
        {
            Widgets.Title("NORE — MMORTS/RPG prototype");
            Widgets.Note("Unity 6 / C# · data-driven JSON · deterministic fixed-tick combat · offline progression. " +
                         "An original, mechanics-inspired clone: no Lilith Games art, audio or assets are used.");
            Widgets.HRule();
            DrawResources(g);
            Widgets.HRule();

            Widgets.BeginRow();
            Widgets.Begin(Widgets.PanelLight);
            Widgets.Label("REALM", Widgets.Gold, 14);
            Widgets.Label("Stronghold Lv." + g.State.Stronghold.level + "  |  Prosperity " + g.State.Stronghold.resources.prosperity, Widgets.Text);
            Widgets.Label("Kingdom " + g.Data.Kingdom(g.State.World.kingdomId).name + "  |  House " + g.State.World.houseId, Widgets.Text);
            Widgets.Label("Territory: " + g.State.World.TerritoryCount() + " cities  |  Emperor points " + g.State.World.emperorPoints, Widgets.Text);
            Widgets.Label("Activity " + g.State.World.activity + "  |  Void " + g.State.World.voidLevel +
                          "  |  Duel Tower " + g.State.World.duelTowerFloor + "  |  Arena " + g.State.World.arenaRating, Widgets.Text);
            Widgets.End();
            Widgets.Begin(Widgets.PanelLight);
            Widgets.Label("ARMY", Widgets.Gold, 14);
            Widgets.Label("Race: " + g.Data.Race(g.State.Army.raceId).name + " — " + g.Data.Race(g.State.Army.raceId).playstyle, Widgets.Text);
            Widgets.Label("Troops " + g.State.Army.TotalTroops() + "  |  Supply " + g.State.Army.SupplyUsed(g.Data) +
                          "/" + g.State.Stronghold.Effect("troopCapacity", g.Data), Widgets.Text);
            Widgets.Label("Army power " + g.State.Army.ArmyPower(g.Data) + "  |  Deploy slots " + g.MaxDeployableSquads(), Widgets.Text);
            Widgets.Label("Heroes " + g.State.Heroes.heroes.Count + " owned / " + g.State.Heroes.deployed.Count +
                          " deployed  |  Dragon " + g.State.Dragons.activeDragonId, Widgets.Text);
            Widgets.End();
            Widgets.EndRow();

            if (g.LastOffline != null)
            {
                Widgets.HRule();
                Widgets.Label("OFFLINE PROGRESS", Widgets.Green, 13);
                Widgets.Label(g.LastOffline.Display(), Widgets.Text);
                for (int i = Math.Max(0, g.LastOffline.lines.Count - 5); i < g.LastOffline.lines.Count; i++)
                    Widgets.Note(g.LastOffline.lines[i]);
            }

            Widgets.HRule();
            Widgets.Label("QUICK BATTLE", Widgets.Gold, 14);
            foreach (RaceDef race in g.Data.AllRaces)
            {
                Widgets.BeginRow();
                if (Widgets.Btn("Skirmish vs " + race.name, Widgets.Accent))
                    g.StartSkirmish(race.id, false);
                Widgets.Label(race.playstyle, Widgets.Muted, 11);
                Widgets.EndRow();
            }
            CityDef city = g.Data.World.cities.Count > 0 ? g.Data.World.cities[0] : null;
            if (city != null && Widgets.Btn("Siege " + city.name + " (rampart defenders)", Widgets.Purple))
                g.SiegeCity(city.id);

            Widgets.HRule();
            Widgets.Label("HEALTH", Widgets.Gold, 14);
            int passed = 0;
            for (int i = 0; i < g.TestResults.Count; i++) if (g.TestResults[i].passed) passed++;
            Widgets.Label("Self-tests " + passed + "/" + g.TestResults.Count + " passed (full list in System tab)", Widgets.Text);
            Widgets.Label("Data: " + g.Data.Summary() + (g.DataErrors.Count == 0 ? " — valid" : " — " + g.DataErrors.Count + " errors"),
                          g.DataErrors.Count == 0 ? Widgets.Green : Widgets.Red);
            for (int i = 0; i < Math.Min(3, g.DataErrors.Count); i++) Widgets.Note(g.DataErrors[i]);
        }

        public static void DrawResources(GameController g)
        {
            ResourceSet r = g.State.Stronghold.resources;
            Widgets.BeginRow();
            ResourceChip("GOLD", r.gold, Widgets.Gold);
            ResourceChip("WOOD", r.wood, Widgets.Green);
            ResourceChip("MANA", r.mana, Widgets.Accent);
            ResourceChip("PROSPERITY", r.prosperity, Widgets.Purple);
            ResourceChip("LINARI", r.linari, Widgets.Gold);
            ResourceChip("SOULBITS", r.soulbits, Widgets.Muted);
            ResourceChip("ACTIVITY", r.activity, Widgets.Muted);
            ResourceChip("CITY CREDIT", g.State.World.cityCredit, Widgets.Muted);
            Widgets.EndRow();
        }

        private static void ResourceChip(string label, long value, Color color)
        {
            GUILayout.BeginVertical(Widgets.PanelStyle(Widgets.PanelLight), GUILayout.MinWidth(110));
            GUILayout.Label(label, Widgets.Box(new Color(0, 0, 0, 0), color, 10, TextAnchor.MiddleCenter, FontStyle.Bold));
            GUILayout.Label(value.ToString("N0"), Widgets.Box(new Color(0, 0, 0, 0), Widgets.Text, 14, TextAnchor.MiddleCenter));
            GUILayout.EndVertical();
        }
    }

    public static class StrongholdPanel
    {
        public static void Draw(GameController g)
        {
            Widgets.Title("STRONGHOLD & ECONOMY");
            OverviewPanel.DrawResources(g);
            long now = GameTime.NowUnix();
            StrongholdState s = g.State.Stronghold;
            Widgets.Label("Builders " + s.ActiveUpgrades(now) + "/" + s.UpgradeSlots() +
                          "  |  Storage cap " + s.StorageCap(g.Data) +
                          "  |  Wall HP " + s.Effect("wallHp", g.Data) +
                          "  |  Hero cap Lv." + s.Effect("heroLevelCap", g.Data), Widgets.Text);
            Widgets.Label("Income / minute: " + s.Effect("goldPerMinute", g.Data) + " gold, " +
                          s.Effect("woodPerMinute", g.Data) + " wood, " + s.Effect("manaPerMinute", g.Data) + " mana", Widgets.Green);
            Widgets.HRule();

            foreach (BuildingDef def in g.Data.AllBuildings)
            {
                BuildingState state = s.Get(def.id);
                if (state == null) continue;
                Widgets.Begin(Widgets.PanelLight);
                Widgets.BeginRow();
                GUILayout.Label(def.name + "  Lv." + state.level + "/" + def.maxLevel,
                                Widgets.Box(new Color(0, 0, 0, 0), Widgets.Text, 14, TextAnchor.MiddleLeft, FontStyle.Bold),
                                GUILayout.MinWidth(210));
                GUILayout.Label(def.category, Widgets.Box(new Color(0, 0, 0, 0), Widgets.Muted, 11), GUILayout.MinWidth(90));
                string reason;
                bool can = s.CanUpgrade(def.id, now, g.Data, out reason);
                if (state.IsUpgrading(now))
                {
                    GUILayout.Label(" upgrading: " + GameTime.Duration(state.Remaining(now)),
                                    Widgets.Box(new Color(0, 0, 0, 0), Widgets.Gold, 11), GUILayout.MinWidth(160));
                    if (Widgets.Btn("Speed up", Widgets.Gold, GUILayout.Width(90))) g.SpeedUp(def.id);
                }
                else
                {
                    ResourceSet cost = s.CostFor(def, state.level + 1, g.Data);
                    GUILayout.Label(can ? ("cost " + cost.gold + "g / " + cost.wood + "w · " +
                                           GameTime.Duration(s.SecondsFor(def, state.level + 1))) : reason,
                                    Widgets.Box(new Color(0, 0, 0, 0), can ? Widgets.Muted : Widgets.Red, 10),
                                    GUILayout.MinWidth(220));
                    if (can && Widgets.Btn("Upgrade", Widgets.Green, GUILayout.Width(90))) g.Upgrade(def.id);
                }
                Widgets.EndRow();
                string effects = "";
                for (int i = 0; i < def.effects.Count; i++)
                    effects += def.effects[i].stat + " +" + (def.effects[i].baseValue + def.effects[i].perLevel * state.level) +
                               (i + 1 < def.effects.Count ? "   " : "");
                Widgets.Note(effects + "   |   +" + def.prosperityPerLevel * (state.level + 1) + " Prosperity on next level");
                Widgets.End();
            }

            Widgets.HRule();
            Widgets.Label("CARAVANS (transport / interception)", Widgets.Gold, 14);
            List<CaravanState> active = g.State.Caravans.Active();
            if (active.Count == 0) Widgets.Note("No caravans on the road. Explore a caravan node to launch one.");
            foreach (CaravanState c in active)
            {
                Widgets.BeginRow();
                GUILayout.Label(c.id + "  " + c.cargo.gold + "g / " + c.cargo.wood + "w  escort " + c.escortPower +
                                "  ETA " + GameTime.Duration(c.Remaining(now)),
                                Widgets.Box(new Color(0, 0, 0, 0), Widgets.Text, 11));
                if (Widgets.Btn("Intercept (PvP)", Widgets.Red, GUILayout.Width(130))) g.Intercept(c.id);
                Widgets.EndRow();
            }
        }
    }

    public static class ArmyPanel
    {
        public static void Draw(GameController g)
        {
            Widgets.Title("FIVE RACES · UNITS · CONVERSION");
            RaceDef race = g.Data.Race(g.State.Army.raceId);
            Widgets.Label("Current race: " + race.name + " — " + race.playstyle, Widgets.Gold, 14);
            Widgets.Label(race.summary, Widgets.Muted);
            Widgets.Label("Troops " + g.State.Army.TotalTroops() + "  |  Supply " + g.State.Army.SupplyUsed(g.Data) +
                          "/" + g.State.Stronghold.Effect("troopCapacity", g.Data) +
                          "  |  Power " + g.State.Army.ArmyPower(g.Data) +
                          "  |  Deployment slots " + g.MaxDeployableSquads(), Widgets.Text);
            Widgets.HRule();

            Widgets.Label("SWITCH RACE (free switching + conversion chart)", Widgets.Gold, 13);
            Widgets.BeginRow();
            foreach (RaceDef r in g.Data.AllRaces)
                if (Widgets.Btn(r.id == g.State.Army.raceId ? "[" + r.name + "]" : r.name,
                                r.id == g.State.Army.raceId ? Widgets.PanelLight : Widgets.Purple))
                    g.SwitchRace(r.id);
            Widgets.EndRow();
            for (int i = 0; i < race.traits.Count; i++)
                Widgets.Note("• " + race.traits[i].label + ": " + race.traits[i].description);

            Widgets.HRule();
            Widgets.Label("CONVERSION CHART", Widgets.Gold, 13);
            foreach (ConversionGroup group in g.Data.AllConversionGroups)
            {
                string line = group.label + ": ";
                for (int i = 0; i < group.entries.Count; i++)
                {
                    UnitDef u = g.Data.Unit(group.entries[i].unitId);
                    line += group.entries[i].race + "=" + (u == null ? "?" : u.name) + (i + 1 < group.entries.Count ? " · " : "");
                }
                Widgets.Note(line);
            }

            Widgets.HRule();
            Widgets.Label("ROSTER (train · deploy on grid)", Widgets.Gold, 13);
            foreach (UnitDef u in g.Data.UnitsForRace(g.State.Army.raceId))
            {
                int count = g.State.Army.Count(u.id);
                int[] cell = g.GetDeployment(u.id);
                Widgets.Begin(Widgets.PanelLight);
                Widgets.BeginRow();
                GUILayout.Label(u.name + "  x" + count,
                                Widgets.Box(new Color(0, 0, 0, 0), Widgets.Text, 13, TextAnchor.MiddleLeft, FontStyle.Bold),
                                GUILayout.MinWidth(160));
                GUILayout.Label(u.role + " T" + u.tier + "  HP " + u.hp + " ATK " + u.attack +
                                " DEF " + u.defense + " RNG " + u.range + " SUP " + u.supply,
                                Widgets.Box(new Color(0, 0, 0, 0), Widgets.Muted, 10), GUILayout.MinWidth(320));
                if (Widgets.Btn("+10", Widgets.Green, GUILayout.Width(55))) g.Train(u.id, 10);
                if (Widgets.Btn("+50", Widgets.Green, GUILayout.Width(55))) g.Train(u.id, 50);
                if (Widgets.Btn("cell " + cell[0] + "," + cell[1] + " ↻", Widgets.Accent, GUILayout.Width(90)))
                    g.SetDeployment(u.id, (cell[0] + 1) % (g.Data.Config.grid.attackerMaxColumn + 1),
                                    (cell[1] + 1) % g.Data.Config.grid.rows);
                Widgets.EndRow();
                Widgets.Note(u.damageType + " · ammo " + u.ammo + " · tags " + string.Join(",", u.tags.ToArray()) + " · " + u.special);
                Widgets.End();
            }

            Widgets.HRule();
            Widgets.Label("QUEUES", Widgets.Gold, 13);
            long now = GameTime.NowUnix();
            foreach (var order in g.State.Army.training)
                Widgets.Note("Training " + order.count + " x " + order.unitId + " — " + GameTime.Duration(order.finishUnix - now));
            foreach (var w in g.State.Army.hospital)
                Widgets.Note("Hospital " + w.count + " x " + w.unitId + " — " + GameTime.Duration(w.finishUnix - now));
            if (g.State.Army.training.Count == 0 && g.State.Army.hospital.Count == 0) Widgets.Note("Queues empty.");
        }
    }

    public static class SystemPanel
    {
        private static Vector2 _scroll;

        public static void Draw(GameController g)
        {
            Widgets.Title("SYSTEM · SAVE · TESTS · DATA");
            Widgets.BeginRow();
            if (Widgets.Btn("Save", Widgets.Green)) g.Save();
            if (Widgets.Btn("Load + offline catch-up", Widgets.Accent)) g.Load();
            if (Widgets.Btn("New realm", Widgets.Red)) g.NewGame();
            if (Widgets.Btn("Export data overrides", Widgets.Purple))
            {
                g.status = SaveService.ExportDataOverrides(g.Data);
                g.State.AddLog(g.status);
            }
            Widgets.EndRow();
            Widgets.Note("Save: " + SaveService.SavePath);
            Widgets.Note("JSON data ships in Assets/Resources/Data. Copy to persistentDataPath/Data to override stats without recompiling.");
            Widgets.HRule();

            Widgets.Label("SELF-TESTS", Widgets.Gold, 14);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(230));
            for (int i = 0; i < g.TestResults.Count; i++)
            {
                var r = g.TestResults[i];
                GUILayout.Label((r.passed ? "PASS  " : "FAIL  ") + r.name + " — " + r.detail,
                                Widgets.Box(new Color(0, 0, 0, 0), r.passed ? Widgets.Green : Widgets.Red, 11));
            }
            GUILayout.EndScrollView();
            Widgets.HRule();

            Widgets.Label("EVENT LOG (last 30)", Widgets.Gold, 14);
            for (int i = Math.Max(0, g.State.Log.Count - 30); i < g.State.Log.Count; i++)
                Widgets.Note(g.State.Log[i]);
            Widgets.HRule();
            Widgets.Label("ARCHITECTURE", Widgets.Gold, 14);
            Widgets.Note("Core (fixed-point, RNG, tests) → Data (JSON registry) → Economy / Military / Combat / World → Runtime (MonoBehaviour + IMGUI).");
            Widgets.Note("Combat uses one aggregated squad per unit stack (struct-of-arrays) + spatial hash for AoE, so very large troop counts cost O(squads), not O(soldiers).");
            Widgets.Note("INetworkHooks is the seam for a real MMO backend; this prototype runs every system locally and deterministically.");
        }
    }
}
