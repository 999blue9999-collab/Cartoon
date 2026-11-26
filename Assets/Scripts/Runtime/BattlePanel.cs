using System;
using Nore.Combat;
using Nore.Core;
using Nore.Data;
using UnityEngine;

namespace Nore.Runtime
{
    /// <summary>Real-time grid battle view, manual hero skill casting and morale telemetry.</summary>
    public static class BattlePanel
    {
        private static Vector2 _logScroll;

        public static void Draw(GameController g)
        {
            if (g.Battle == null)
            {
                Widgets.Title("REAL-TIME TACTICAL COMBAT");
                Widgets.Note("No battle running. Deploy your army in the Army tab, then pick an opponent. " +
                             "Squads are placed on a grid; cast hero skills by clicking a grid cell during the fight.");
                Widgets.HRule();
                foreach (RaceDef race in g.Data.AllRaces)
                    if (Widgets.Btn("Start skirmish vs " + race.name, Widgets.Accent)) g.StartSkirmish(race.id, false);
                if (g.Data.World.cities.Count > 0 &&
                    Widgets.Btn("Siege " + g.Data.World.cities[0].name + " (wall defenders)", Widgets.Purple))
                    g.SiegeCity(g.Data.World.cities[0].id);
                if (g.LastReport != null) DrawReport(g, g.LastReport);
                return;
            }

            BattleSimulation sim = g.Battle;
            Widgets.Title("BATTLE — " + (g.LastSetup == null ? "Skirmish" : g.LastSetup.label));
            Widgets.BeginRow();
            Widgets.Label("Tick " + sim.Tick + "/" + sim.MaxTicks + "  ·  " + sim.Outcome +
                          "  ·  hash " + sim.RollingHash, Widgets.Text, 12);
            if (Widgets.Btn(g.paused ? "Resume" : "Pause", Widgets.Accent, GUILayout.Width(100))) g.paused = !g.paused;
            if (Widgets.Btn("x1", g.battleSpeed == 1 ? Widgets.Green : Widgets.PanelLight, GUILayout.Width(45))) g.battleSpeed = 1;
            if (Widgets.Btn("x2", g.battleSpeed == 2 ? Widgets.Green : Widgets.PanelLight, GUILayout.Width(45))) g.battleSpeed = 2;
            if (Widgets.Btn("x4", g.battleSpeed == 4 ? Widgets.Green : Widgets.PanelLight, GUILayout.Width(45))) g.battleSpeed = 4;
            if (Widgets.Btn("Step", Widgets.Purple, GUILayout.Width(60))) sim.Step();
            if (sim.Finished && Widgets.Btn("Apply result", Widgets.Green, GUILayout.Width(110))) g.ResolveBattle();
            Widgets.EndRow();

            DrawSkillBar(g, sim);
            DrawGrid(g, sim);

            Widgets.BeginRow();
            Widgets.Begin(Widgets.PanelLight);
            Widgets.Label("ATTACKER", Widgets.Accent, 13);
            Widgets.Label("Power " + sim.TeamPower(0) / 1000 + " · squads " + ActiveSquads(sim, 0), Widgets.Text);
            Widgets.Label("Avg morale " + AverageMorale(sim, 0) + " · routed " + Routed(sim, 0), Widgets.Text);
            Widgets.End();
            Widgets.Begin(Widgets.PanelLight);
            Widgets.Label("DEFENDER", Widgets.Red, 13);
            Widgets.Label("Power " + sim.TeamPower(1) / 1000 + " · squads " + ActiveSquads(sim, 1), Widgets.Text);
            Widgets.Label("Avg morale " + AverageMorale(sim, 1) + " · routed " + Routed(sim, 1), Widgets.Text);
            Widgets.End();
            Widgets.EndRow();

            if (g.LastReport != null) DrawReport(g, g.LastReport);
            Widgets.HRule();
            Widgets.Label("BATTLE LOG", Widgets.Gold, 13);
            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(150));
            for (int i = 0; i < sim.Log.Count; i++)
                Widgets.Note(sim.Log[i]);
            GUILayout.EndScrollView();
        }

        private static void DrawSkillBar(GameController g, BattleSimulation sim)
        {
            Widgets.Label("HERO SKILLS (select a skill, then click a grid cell to cast in real time)", Widgets.Gold, 13);
            Widgets.BeginRow();
            for (int h = 0; h < sim.HeroCount; h++)
            {
                if (sim.HTeam[h] != 0) continue;
                string heroName = g.Data.Hero(sim.HIds[h]) == null ? sim.HIds[h] : g.Data.Hero(sim.HIds[h]).name;
                if (Widgets.Btn((g.selectedHero == h ? "▶ " : "") + heroName + " " + sim.HHp[h] + "hp / " + sim.HMana[h] + "mp",
                                g.selectedHero == h ? Widgets.Green : Widgets.Accent, GUILayout.Width(190)))
                    g.selectedHero = h;
            }
            Widgets.EndRow();

            int hero = Mathf.Clamp(g.selectedHero, 0, Mathf.Max(0, sim.HeroCount - 1));
            if (hero >= sim.HeroCount || sim.HTeam[hero] != 0) return;
            Widgets.BeginRow();
            for (int k = 0; k < sim.HSkillCount[hero]; k++)
            {
                string skillId = sim.HSkills[hero * BattleSimulation.MaxSkillsPerHero + k];
                SkillDef skill = g.Data.Skill(skillId);
                if (skill == null) continue;
                int slot = hero * BattleSimulation.MaxSkillsPerHero + k;
                string reason;
                bool ready = sim.CanCast(hero, skillId, out reason);
                string label = skill.name + " (" + skill.manaCost + "mp" +
                               (sim.HCooldown[slot] > 0 ? ", CD " + sim.HCooldown[slot] + "t" : "") + ")";
                Color color = !ready ? Widgets.PanelLight : (g.selectedSkill == skillId ? Widgets.Gold : Widgets.Purple);
                if (Widgets.Btn(label, color, GUILayout.Width(190)))
                {
                    g.selectedSkill = skillId;
                    g.targetingSkill = true;
                    g.status = "Targeting " + skill.name + " — click a grid cell";
                }
                if (Widgets.Btn("auto", Widgets.Accent, GUILayout.Width(55)))
                {
                    g.selectedHero = hero;
                    g.CastAtCell(skillId, g.Data.Config.grid.columns - 3, g.Data.Config.grid.rows / 2);
                }
            }
            Widgets.EndRow();
            if (g.targetingSkill) Widgets.Label("TARGETING " + g.selectedSkill + " — click anywhere on the grid", Widgets.Gold, 12);
        }

        private static void DrawGrid(GameController g, BattleSimulation sim)
        {
            int cols = g.Data.Config.grid.columns, rows = g.Data.Config.grid.rows;
            float cellW = Mathf.Max(34f, Mathf.Min(78f, (Screen.width - 80f) / cols));
            float width = cellW * cols, height = cellW * 0.62f * rows;
            Rect area = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));
            float cw = width / cols, ch = height / rows;

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    Color bg = (x + y) % 2 == 0 ? new Color(0.13f, 0.17f, 0.23f, 1f) : new Color(0.11f, 0.15f, 0.20f, 1f);
                    if (x <= g.Data.Config.grid.attackerMaxColumn) bg = Color.Lerp(bg, new Color(0.17f, 0.35f, 0.55f, 1f), 0.25f);
                    if (x >= g.Data.Config.grid.defenderMinColumn) bg = Color.Lerp(bg, new Color(0.50f, 0.20f, 0.20f, 1f), 0.25f);
                    if (g.LastSetup != null && g.LastSetup.defenderOnWalls && x >= g.Data.Config.grid.defenderMinColumn)
                        bg = Color.Lerp(bg, new Color(0.35f, 0.30f, 0.22f, 1f), 0.45f);
                    GUI.Box(new Rect(area.x + x * cw, area.y + y * ch, cw - 1, ch - 1), GUIContent.none,
                            Widgets.Box(bg, Widgets.Text, 9));
                }
            }

            // Squads (aggregated stacks) with morale bars.
            for (int i = 0; i < sim.Count; i++)
            {
                if (sim.Alive[i] <= 0 || sim.Escaped[i] != 0) continue;
                float px = area.x + (sim.X[i] / (float)Fixed.Q) * cw;
                float py = area.y + (sim.Y[i] / (float)Fixed.Q) * ch;
                Rect r = new Rect(px - cw * 0.42f, py - ch * 0.34f, cw * 0.84f, ch * 0.68f);
                Color teamColor = sim.Team[i] == 0 ? new Color(0.22f, 0.52f, 0.82f, 1f) : new Color(0.78f, 0.30f, 0.30f, 1f);
                if (sim.Routed[i] != 0) teamColor = Color.Lerp(teamColor, Color.gray, 0.6f);
                if (sim.IsSummon[i] != 0) teamColor = Color.Lerp(teamColor, Widgets.Purple, 0.4f);
                string shortName = sim.Defs[i].name.Length > 9 ? sim.Defs[i].name.Substring(0, 8) : sim.Defs[i].name;
                GUI.Box(r, shortName + "\n" + sim.Alive[i],
                        Widgets.Box(teamColor, Color.white, 9, TextAnchor.MiddleCenter, FontStyle.Bold, false));
                float frac = sim.MaxHpPool[i] <= 0 ? 0f : (float)(sim.HpPool[i] / (double)sim.MaxHpPool[i]);
                GUI.Box(new Rect(r.x, r.yMax - 3, r.width * Mathf.Clamp01(frac), 3), GUIContent.none,
                        Widgets.Box(Widgets.Green, Widgets.Text, 8));
                GUI.Box(new Rect(r.x, r.y, r.width * (sim.Morale[i] / 1000f), 2), GUIContent.none,
                        Widgets.Box(Widgets.Gold, Widgets.Text, 8));
            }

            // Heroes.
            for (int h = 0; h < sim.HeroCount; h++)
            {
                if (sim.HAlive[h] == 0) continue;
                float px = area.x + (sim.HX[h] / (float)Fixed.Q) * cw;
                float py = area.y + (sim.HY[h] / (float)Fixed.Q) * ch;
                Rect r = new Rect(px - cw * 0.30f, py - ch * 0.28f, cw * 0.60f, ch * 0.56f);
                string name = sim.HIds[h].Length > 7 ? sim.HIds[h].Substring(0, 7) : sim.HIds[h];
                GUI.Box(r, name,
                        Widgets.Box(sim.HTeam[h] == 0 ? Widgets.Gold : Widgets.Red, Color.white, 9,
                                    TextAnchor.MiddleCenter, FontStyle.Bold, false));
            }

            if (g.targetingSkill && Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                area.Contains(Event.current.mousePosition))
            {
                int cx = Mathf.Clamp((int)((Event.current.mousePosition.x - area.x) / cw), 0, cols - 1);
                int cy = Mathf.Clamp((int)((Event.current.mousePosition.y - area.y) / ch), 0, rows - 1);
                g.CastAtCell(g.selectedSkill, cx, cy);
                g.targetingSkill = false;
                Event.current.Use();
            }

            Widgets.Note("Blue = attacker deployment zone · Red = defender zone · brown = ramparts · " +
                         "gold bar = morale · green bar = HP pool");
        }

        private static void DrawReport(GameController g, BattleReport report)
        {
            Widgets.HRule();
            Widgets.Label("RESULT: " + report.summary, report.outcome == BattleOutcome.AttackerWin ? Widgets.Green : Widgets.Red, 14);
            Widgets.Label("Kills " + report.totalKilled + " · routed squads " + report.routedSquads +
                          " · loot " + report.lootGold + "g / " + report.lootWood + "w / " + report.lootMana + "m" +
                          " · hero XP " + report.heroXp + " · state hash " + report.stateHash, Widgets.Text);
            Widgets.BeginRow();
            for (int i = 0; i < report.outcomes.Count; i++)
            {
                var o = report.outcomes[i];
                Widgets.Note((o.team == 0 ? "A " : "D ") + o.unitId + ": " + o.deployed + " → " + o.survivors +
                             " survivors, " + o.killed + " killed, " + o.routed + " routed");
            }
            Widgets.EndRow();
            if (report.outcome == BattleOutcome.AttackerWin && Widgets.Btn("Return to stronghold", Widgets.Green)) g.ClearBattle();
        }

        private static int ActiveSquads(BattleSimulation sim, int team)
        {
            int n = 0;
            for (int i = 0; i < sim.Count; i++)
                if (sim.Team[i] == team && sim.Alive[i] > 0 && sim.Escaped[i] == 0 && sim.Routed[i] == 0) n++;
            return n;
        }

        private static int Routed(BattleSimulation sim, int team)
        {
            int n = 0;
            for (int i = 0; i < sim.Count; i++) if (sim.Team[i] == team && sim.Routed[i] != 0) n++;
            return n;
        }

        private static int AverageMorale(BattleSimulation sim, int team)
        {
            int sum = 0, n = 0;
            for (int i = 0; i < sim.Count; i++)
                if (sim.Team[i] == team && sim.Alive[i] > 0) { sum += sim.Morale[i]; n++; }
            return n == 0 ? 0 : sum / n;
        }
    }
}
