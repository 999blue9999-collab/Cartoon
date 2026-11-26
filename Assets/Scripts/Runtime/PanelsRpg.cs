using System;
using System.Collections.Generic;
using Nore.Core;
using Nore.Data;
using Nore.Military;
using UnityEngine;

namespace Nore.Runtime
{
    public static class HeroPanel
    {
        public static void Draw(GameController g)
        {
            Widgets.Title("HEROES · MIGHT / MAGIC / STAMINA / COMMAND");
            Widgets.Label("Formation: " + g.State.Heroes.deployed.Count + "/3 heroes · " +
                          "Total Command " + g.State.Heroes.TotalCommand(g.Data) +
                          " · Soulbits " + g.State.Stronghold.resources.soulbits +
                          " · Linari " + g.State.Stronghold.resources.linari, Widgets.Text);
            Widgets.BeginRow();
            if (Widgets.Btn("Open Hero Chest (10 Soulbits)", Widgets.Purple))
            {
                string message;
                g.State.Heroes.OpenChest(g.State.Rng, g.Data, g.State.Stronghold, out message);
                g.status = message;
                g.State.AddLog(message);
            }
            if (Widgets.Btn("Buy Jacques (shop only, 100 Linari)", Widgets.Gold))
            {
                string message;
                g.State.Heroes.BuyShopHero("jacques", g.Data, g.State.Stronghold, out message);
                g.status = message;
                g.State.AddLog(message);
            }
            Widgets.EndRow();
            Widgets.HRule();

            foreach (HeroState h in g.State.Heroes.heroes)
            {
                HeroDef def = g.Data.Hero(h.heroId);
                bool deployed = g.State.Heroes.deployed.Contains(h.heroId);
                Widgets.Begin(Widgets.PanelLight);
                Widgets.BeginRow();
                GUILayout.Label(def.name + "  Lv." + h.level + "  [" + def.role + "]",
                                Widgets.Box(new Color(0, 0, 0, 0), Widgets.Gold, 14, TextAnchor.MiddleLeft, FontStyle.Bold),
                                GUILayout.MinWidth(240));
                GUILayout.Label("Energy " + h.energy + "/20 · deploy cost " + def.energyCost +
                                " · troop slots " + h.TroopSlots(g.Data) + " · HP " + h.Hp(g.Data),
                                Widgets.Box(new Color(0, 0, 0, 0), Widgets.Muted, 11));
                if (Widgets.Btn(deployed ? "In formation ✓" : "Add to formation",
                                deployed ? Widgets.Green : Widgets.Accent, GUILayout.Width(150)))
                {
                    string message;
                    g.State.Heroes.ToggleDeploy(h.heroId, g.Data, out message);
                    g.status = message;
                }
                Widgets.EndRow();
                Widgets.Note(def.description);

                Widgets.BeginRow();
                Widgets.Label("Might " + h.Might(g.Data), Widgets.Text, 11);
                Widgets.Label("Magic " + h.Magic(g.Data), Widgets.Text, 11);
                Widgets.Label("Stamina " + h.Stamina(g.Data), Widgets.Text, 11);
                Widgets.Label("Command " + h.Command(g.Data), Widgets.Green, 11);
                Widgets.Label("Skill points " + h.skillPoints, Widgets.Gold, 11);
                Widgets.EndRow();
                if (h.skillPoints > 0)
                {
                    Widgets.BeginRow();
                    foreach (string stat in new[] { "might", "magic", "stamina", "command" })
                        if (Widgets.Btn("+" + stat, Widgets.Purple, GUILayout.Width(110)))
                        {
                            string message;
                            h.Allocate(stat, out message);
                            g.status = def.name + ": " + message;
                        }
                    Widgets.EndRow();
                }

                // Gear slots: click to cycle the next owned item that fits the slot.
                for (int slot = 0; slot < def.gearSlots.Count; slot++)
                {
                    string slotName = def.gearSlots[slot];
                    string equipped = slot < h.gear.Count ? h.gear[slot] : "";
                    Widgets.BeginRow();
                    GUILayout.Label(slotName + ": " + (string.IsNullOrEmpty(equipped) ? "empty" : equipped),
                                    Widgets.Box(new Color(0, 0, 0, 0), Widgets.Muted, 10), GUILayout.MinWidth(200));
                    if (Widgets.Btn("equip ⇄", Widgets.Accent, GUILayout.Width(90)))
                    {
                        string next = NextGear(g, slotName, equipped);
                        string message;
                        h.Equip(slotName, next, g.Data, out message);
                        g.status = def.name + " " + message;
                    }
                    Widgets.EndRow();
                }

                for (int k = 0; k < def.skillIds.Count; k++)
                {
                    var skill = g.Data.Skill(def.skillIds[k]);
                    if (skill == null) continue;
                    Widgets.Note("◆ " + skill.name + " [" + skill.kind + "/" + skill.target + "] " +
                                 skill.manaCost + " mana · " + skill.cooldownTicks + "t CD · " + skill.description);
                }
                Widgets.End();
            }
        }

        private static string NextGear(GameController g, string slot, string current)
        {
            var options = new List<string> { "" };
            foreach (string id in g.State.Heroes.ownedGear)
            {
                var item = g.Data.GearItem(id);
                if (item != null && item.slot == slot) options.Add(id);
            }
            int index = Math.Max(0, options.IndexOf(current));
            return options[(index + 1) % options.Count];
        }
    }

    public static class DragonPanel
    {
        public static void Draw(GameController g)
        {
            Widgets.Title("DRAGONS · ELEMENTS · TALENT TREES · AWAKENING");
            var awakening = g.Data.Dragons.awakening;
            Widgets.Label("Awakening requires " + awakening.allTreesPoints + " points in all three trees and " +
                          awakening.primaryTreePoints + " in the primary tree (final ability).", Widgets.Gold);
            Widgets.HRule();

            foreach (DragonState d in g.State.Dragons.dragons)
            {
                DragonDef def = g.Data.Dragon(d.dragonId);
                bool active = d.dragonId == g.State.Dragons.activeDragonId;
                Widgets.Begin(Widgets.PanelLight);
                Widgets.BeginRow();
                GUILayout.Label(def.name + " (" + def.element + ")  Lv." + d.level + "  " + d.Tier(g.Data),
                                Widgets.Box(new Color(0, 0, 0, 0), Widgets.Gold, 14, TextAnchor.MiddleLeft, FontStyle.Bold),
                                GUILayout.MinWidth(260));
                GUILayout.Label("ATK " + d.Attack(g.Data) + " · HP " + d.Hp(g.Data) +
                                " · crystals " + d.crystals + (d.IsAwakened(g.Data) ? " · AWAKENED" : ""),
                                Widgets.Box(new Color(0, 0, 0, 0), d.IsAwakened(g.Data) ? Widgets.Green : Widgets.Text, 11));
                if (Widgets.Btn(active ? "Active ✓" : "Set active", active ? Widgets.Green : Widgets.Accent, GUILayout.Width(110)))
                    g.State.Dragons.activeDragonId = d.dragonId;
                Widgets.EndRow();
                Widgets.Note(def.role + ": " + def.ability + " — " + def.description);

                for (int t = 0; t < g.Data.Dragons.trees.Count && t < d.points.Length; t++)
                {
                    TalentTreeDef tree = g.Data.Dragons.trees[t];
                    int points = d.points[t];
                    Widgets.Bar(tree.name, points, awakening.allTreesPoints,
                                tree.id == def.primaryTree ? Widgets.Gold : Widgets.Accent);
                    Widgets.BeginRow();
                    if (Widgets.Btn("+1", Widgets.Purple, GUILayout.Width(55))) Spend(g, d, tree.id, 1);
                    if (Widgets.Btn("+10", Widgets.Purple, GUILayout.Width(55))) Spend(g, d, tree.id, 10);
                    string milestones = "";
                    for (int m = 0; m < tree.milestones.Count; m++)
                    {
                        var ms = tree.milestones[m];
                        milestones += (points >= ms.points ? "✓" : "·") + ms.points + " " + ms.label + "  ";
                    }
                    GUILayout.Label(milestones, Widgets.Box(new Color(0, 0, 0, 0), Widgets.Muted, 10));
                    Widgets.EndRow();
                }
                Widgets.End();
            }

            Widgets.HRule();
            Widgets.Label("HATCH EGGS", Widgets.Gold, 13);
            Widgets.BeginRow();
            foreach (DragonDef def in g.Data.AllDragons)
            {
                bool owned = false;
                for (int i = 0; i < g.State.Dragons.dragons.Count; i++)
                    if (g.State.Dragons.dragons[i].dragonId == def.id) owned = true;
                if (owned) continue;
                if (Widgets.Btn("Hatch " + def.name + " (150 Linari)", Widgets.Purple))
                {
                    string message;
                    g.State.Dragons.Hatch(def.id, g.Data, g.State.Stronghold, out message);
                    g.status = message;
                    g.State.AddLog(message);
                }
            }
            Widgets.EndRow();
        }

        private static void Spend(GameController g, DragonState dragon, string treeId, int amount)
        {
            string message;
            dragon.Spend(treeId, amount, g.Data, out message);
            g.status = message;
            g.State.AddLog("Dragon: " + message);
        }
    }

    public static class WorldPanel
    {
        public static void Draw(GameController g)
        {
            var w = g.State.World;
            Widgets.Title("WORLD MAP · KINGDOMS · SIEGES · HOUSE · PVP");
            Widgets.Label("Kingdom " + g.Data.Kingdom(w.kingdomId).name + " · " +
                          g.Data.Kingdom(w.kingdomId).description, Widgets.Text);
            Widgets.Label("House " + w.houseId + " controls " + w.TerritoryCount() + " cities · " +
                          "Nexus karma " + w.nexusKarma + " · CoF rounds won " + w.clashRoundsWon +
                          " · Emperor points " + w.emperorPoints, Widgets.Green);
            Widgets.HRule();
            Widgets.Label("JOIN A HOUSE", Widgets.Gold, 13);
            Widgets.BeginRow();
            foreach (var house in g.Data.World.houses)
                if (Widgets.Btn(house.name, w.houseId == house.id ? Widgets.Green : Widgets.Accent))
                {
                    string message;
                    w.JoinHouse(house.id, g.Data, out message);
                    g.status = message;
                }
            Widgets.EndRow();

            Widgets.HRule();
            Widgets.Label("SEMI-OPEN WORLD NODES (tap to explore; enemy/boss nodes start a battle)", Widgets.Gold, 13);
            foreach (MapNodeDef node in g.Data.World.nodes)
            {
                bool explored = w.exploredNodes.Contains(node.id);
                Widgets.BeginRow();
                if (Widgets.Btn((explored ? "✓ " : "· ") + node.name + " [" + node.kind + "]",
                                node.kind == "boss" ? Widgets.Red : (node.kind == "caravan" ? Widgets.Gold : Widgets.Accent),
                                GUILayout.Width(260)))
                    g.Explore(node.id);
                GUILayout.Label(node.region + " / " + node.kingdom + " — " + node.label +
                                " · " + node.reward.gold + "g " + node.reward.wood + "w " + node.reward.mana + "m",
                                Widgets.Box(new Color(0, 0, 0, 0), Widgets.Muted, 10));
                Widgets.EndRow();
            }

            Widgets.HRule();
            Widgets.Label("CITY STRONGHOLD SIEGES (capture when all strongholds fall)", Widgets.Gold, 13);
            foreach (CityDef city in g.Data.World.cities)
            {
                var state = w.City(city.id);
                Widgets.BeginRow();
                GUILayout.Label(city.name + " (" + g.Data.Kingdom(city.kingdom).name + ") T" + city.tier +
                                " · " + state.strongholdsRemaining + "/" + city.strongholds + " strongholds · " +
                                (string.IsNullOrEmpty(state.ownerHouseId) ? "neutral" : state.ownerHouseId),
                                Widgets.Box(new Color(0, 0, 0, 0), Widgets.Text, 11), GUILayout.MinWidth(420));
                if (Widgets.Btn("Siege", Widgets.Red, GUILayout.Width(90))) g.SiegeCity(city.id);
                Widgets.EndRow();
            }

            Widgets.HRule();
            Widgets.Label("PVP MODES", Widgets.Gold, 13);
            Widgets.BeginRow();
            foreach (PvpModeDef mode in g.Data.World.pvpModes)
                if (Widgets.Btn(mode.name, Widgets.Purple)) g.RunPvp(mode.id);
            Widgets.EndRow();
            Widgets.Label("EVENTS", Widgets.Gold, 13);
            Widgets.BeginRow();
            foreach (EventDef ev in g.Data.World.events)
                if (Widgets.Btn(ev.name, Widgets.Green)) g.RunEvent(ev.id);
            Widgets.EndRow();

            Widgets.HRule();
            Widgets.Label("JOURNAL", Widgets.Gold, 13);
            for (int i = Math.Max(0, w.journal.Count - 12); i < w.journal.Count; i++)
                Widgets.Note(w.journal[i]);
        }
    }
}
