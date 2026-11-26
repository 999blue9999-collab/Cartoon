using System;
using System.Collections.Generic;
using Nore.Core;
using Nore.Data;

namespace Nore.Military
{
    /// <summary>
    /// Dragon with three draconic talent trees. Points are spent with crystals from
    /// events/daily chests. Awakening requires 159 in every tree and 109 in the
    /// dragon's primary tree (which unlocks that tree's final ability).
    /// </summary>
    [Serializable]
    public class DragonState
    {
        public string dragonId;
        public int level = 1;
        public int tierIndex;
        public int crystals = 24;
        public int[] points = new int[3]; // aligned with DragonTable.trees

        public static DragonState CreateNew(string id, DataRegistry data)
        {
            var d = new DragonState { dragonId = id };
            if (data.Dragons.trees.Count != d.points.Length) d.points = new int[data.Dragons.trees.Count];
            return d;
        }

        public int TreeIndex(string treeId, DataRegistry data)
        {
            for (int i = 0; i < data.Dragons.trees.Count; i++)
                if (data.Dragons.trees[i].id == treeId) return i;
            return -1;
        }

        public int Points(string treeId, DataRegistry data)
        {
            int idx = TreeIndex(treeId, data);
            return idx < 0 || idx >= points.Length ? 0 : points[idx];
        }

        public bool FinalAbilityUnlocked(DataRegistry data)
        {
            DragonDef def = data.Dragon(dragonId);
            return def != null && Points(def.primaryTree, data) >= data.Dragons.awakening.primaryTreePoints;
        }

        public bool IsAwakened(DataRegistry data)
        {
            int required = data.Dragons.awakening.allTreesPoints;
            if (!FinalAbilityUnlocked(data)) return false;
            for (int i = 0; i < points.Length; i++) if (points[i] < required) return false;
            return true;
        }

        public bool Spend(string treeId, int amount, DataRegistry data, out string message)
        {
            int idx = TreeIndex(treeId, data);
            if (idx < 0) { message = "Unknown talent tree"; return false; }
            if (amount <= 0) { message = "Bad amount"; return false; }
            if (crystals < amount) { message = "Needs " + amount + " draconic crystals"; return false; }
            crystals -= amount;
            points[idx] += amount;
            message = "+" + amount + " " + data.Dragons.trees[idx].name + " (" + points[idx] + " total)";
            if (FinalAbilityUnlocked(data)) message += " | Final ability unlocked!";
            if (IsAwakened(data)) message += " | DRACONIC AWAKENING!";
            return true;
        }

        /// <summary>Aggregated milestone bonuses, returned as stat -> basis points / points.</summary>
        public Dictionary<string, int> Bonuses(DataRegistry data)
        {
            var result = new Dictionary<string, int>();
            for (int t = 0; t < data.Dragons.trees.Count && t < points.Length; t++)
            {
                TalentTreeDef tree = data.Dragons.trees[t];
                for (int m = 0; m < tree.milestones.Count; m++)
                {
                    TalentMilestone milestone = tree.milestones[m];
                    if (points[t] < milestone.points) continue;
                    int current;
                    result.TryGetValue(milestone.stat, out current);
                    result[milestone.stat] = current + milestone.valueBp;
                }
            }
            if (IsAwakened(data))
            {
                AwakeningDef a = data.Dragons.awakening;
                Add(result, "dragonHpBp", a.bonusHpBp);
                Add(result, "dragonAttackBp", a.bonusAttackBp);
                Add(result, "commandBonus", a.bonusCommandBonus);
            }
            return result;
        }

        private static void Add(Dictionary<string, int> map, string key, int value)
        {
            int current;
            map.TryGetValue(key, out current);
            map[key] = current + value;
        }

        public int Bonus(string stat, DataRegistry data)
        {
            int value;
            return Bonuses(data).TryGetValue(stat, out value) ? value : 0;
        }

        public string Tier(DataRegistry data)
        {
            DragonDef def = data.Dragon(dragonId);
            return def == null || def.tiers.Count == 0 ? "rare" : def.tiers[Fixed.Clamp(tierIndex, 0, def.tiers.Count - 1)];
        }

        public int Attack(DataRegistry data)
        {
            DragonDef def = data.Dragon(dragonId);
            if (def == null) return 0;
            return Fixed.BpMul(def.baseAttack + level * 12, Fixed.Bp + Bonus("dragonAttackBp", data));
        }

        public int Hp(DataRegistry data)
        {
            DragonDef def = data.Dragon(dragonId);
            if (def == null) return 0;
            return Fixed.BpMul(def.baseHp + level * 90, Fixed.Bp + Bonus("dragonHpBp", data));
        }
    }

    [Serializable]
    public class DragonManager
    {
        public List<DragonState> dragons = new List<DragonState>();
        public string activeDragonId = "azure";

        public static DragonManager CreateNew(DataRegistry data)
        {
            var m = new DragonManager();
            m.dragons.Add(DragonState.CreateNew("azure", data));
            return m;
        }

        public DragonState Active(DataRegistry data)
        {
            for (int i = 0; i < dragons.Count; i++) if (dragons[i].dragonId == activeDragonId) return dragons[i];
            return dragons.Count > 0 ? dragons[0] : null;
        }

        public bool Hatch(string dragonId, DataRegistry data, Nore.Economy.StrongholdState stronghold, out string message)
        {
            DragonDef def = data.Dragon(dragonId);
            if (def == null) { message = "Unknown dragon"; return false; }
            for (int i = 0; i < dragons.Count; i++)
                if (dragons[i].dragonId == dragonId) { message = "Already hatched"; return false; }
            if (stronghold.resources.linari < 150) { message = "Needs 150 Linari for an egg"; return false; }
            stronghold.resources.linari -= 150;
            dragons.Add(DragonState.CreateNew(dragonId, data));
            message = "Hatched " + def.name + " (" + def.element + ")";
            return true;
        }

        public void GrantEventCrystals(DataRegistry data, List<string> log)
        {
            int amount = data.Config.progression.dragonCrystalPerEvent;
            for (int i = 0; i < dragons.Count; i++) dragons[i].crystals += amount;
            if (log != null) log.Add("Daily activity chest: +" + amount + " draconic crystals");
        }
    }
}
