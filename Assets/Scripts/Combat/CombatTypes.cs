using System;
using System.Collections.Generic;

namespace Nore.Combat
{
    public enum BattleTeam { Attacker = 0, Defender = 1 }
    public enum BattleOutcome { Ongoing, AttackerWin, DefenderWin, Draw }
    public enum SkillKind { Damage, Heal, Buff, Summon, Stun }
    public enum SkillTarget { Point, Allies, Enemies, Self }

    [Serializable] public class SquadTemplate
    {
        public string unitId;
        public int count;
        public int cellX, cellY;
        public int team;
    }

    /// <summary>Resolved hero snapshot handed to a battle (no save-file coupling).</summary>
    [Serializable] public class HeroSpec
    {
        public string heroId;
        public int team;
        public int cellX, cellY;
        public int might, magic, stamina, command;
        public int hp;
        public List<string> skillIds = new List<string>();
    }

    [Serializable] public class BattleSetup
    {
        public int seed = 12345;
        public string label = "Skirmish";
        public bool siege;
        public bool defenderOnWalls;
        public int wallHp;
        public int maxTicks;
        public List<SquadTemplate> squads = new List<SquadTemplate>();
        public List<HeroSpec> heroes = new List<HeroSpec>();
    }

    [Serializable] public class SquadOutcome
    {
        public int team;
        public string unitId;
        public int deployed, survivors, killed, routed;
    }

    [Serializable] public class BattleReport
    {
        public BattleOutcome outcome;
        public int winnerTeam = -1;
        public int ticks;
        public int attackerRemaining, defenderRemaining;
        public int routedSquads;
        public int totalKilled;
        public string stateHash = "";
        public string summary = "";
        public List<SquadOutcome> outcomes = new List<SquadOutcome>();
        public List<string> log = new List<string>();
        public long lootGold, lootWood, lootMana;
        public int heroXp;
        public bool DeterministicReplay { get; set; }
    }

    /// <summary>Static factory helpers used by UI, world encounters and tests.</summary>
    public static class BattleFactory
    {
        public static SquadTemplate Squad(string unitId, int count, int cellX, int cellY, int team)
            => new SquadTemplate { unitId = unitId, count = count, cellX = cellX, cellY = cellY, team = team };

        public static HeroSpec Hero(string heroId, int team, int cellX, int cellY,
                                    int might, int magic, int stamina, int command, int hp, List<string> skillIds)
            => new HeroSpec
            {
                heroId = heroId, team = team, cellX = cellX, cellY = cellY,
                might = might, magic = magic, stamina = stamina, command = command,
                hp = hp, skillIds = skillIds
            };

        /// <summary>Builds a mirror PvE army from a race's roster, placed on the defender grid.</summary>
        public static List<SquadTemplate> GarrisonFor(string raceId, int columns, int rows,
                                                     Nore.Data.DataRegistry data, int strength)
        {
            var result = new List<SquadTemplate>();
            var units = data.UnitsForRace(raceId);
            int row = 0;
            for (int i = 0; i < units.Count && result.Count < 12; i++)
            {
                Nore.Data.UnitDef u = units[i];
                int count = Math.Max(4, u.stackSize * strength / 100);
                result.Add(Squad(u.id, count, columns - 1 - (i % 3), row % rows, (int)BattleTeam.Defender));
                row += 2;
            }
            return result;
        }
    }
}
