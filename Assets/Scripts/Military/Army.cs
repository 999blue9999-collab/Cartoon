using System;
using System.Collections.Generic;
using Nore.Core;
using Nore.Data;
using Nore.Economy;

namespace Nore.Military
{
    [Serializable] public class UnitStack { public string unitId; public int count; }

    [Serializable] public class TrainingOrder
    {
        public string unitId;
        public int count;
        public long finishUnix;
    }

    [Serializable] public class WoundedOrder
    {
        public string unitId;
        public int count;
        public long finishUnix;
    }

    /// <summary>Race roster, training queue, hospital and cross-race unit conversion.</summary>
    [Serializable]
    public class ArmyState
    {
        public string raceId = "human";
        public List<UnitStack> stacks = new List<UnitStack>();
        public List<TrainingOrder> training = new List<TrainingOrder>();
        public List<WoundedOrder> hospital = new List<WoundedOrder>();

        public static ArmyState CreateNew(DataRegistry data)
        {
            var army = new ArmyState { raceId = data.Config.start.race };
            army.Add("human_spearman", 48);
            army.Add("human_archer", 40);
            army.Add("human_light_infantry", 40);
            army.Add("human_cavalry", 18);
            army.Add("human_clergy", 16);
            army.Add("human_catapult", 6);
            return army;
        }

        public void Add(string unitId, int count)
        {
            if (count <= 0) return;
            for (int i = 0; i < stacks.Count; i++)
                if (stacks[i].unitId == unitId) { stacks[i].count += count; return; }
            stacks.Add(new UnitStack { unitId = unitId, count = count });
        }

        public UnitStack Stack(string unitId)
        {
            for (int i = 0; i < stacks.Count; i++) if (stacks[i].unitId == unitId) return stacks[i];
            return null;
        }

        public int Count(string unitId)
        {
            UnitStack s = Stack(unitId);
            return s == null ? 0 : s.count;
        }

        public void Remove(string unitId, int count)
        {
            UnitStack s = Stack(unitId);
            if (s == null || count <= 0) return;
            s.count = Math.Max(0, s.count - count);
        }

        public int TotalTroops()
        {
            int total = 0;
            for (int i = 0; i < stacks.Count; i++) total += stacks[i].count;
            return total;
        }

        public int SupplyUsed(DataRegistry data)
        {
            int supply = 0;
            for (int i = 0; i < stacks.Count; i++)
            {
                UnitDef u = data.Unit(stacks[i].unitId);
                if (u != null) supply += stacks[i].count * u.supply;
            }
            return supply;
        }

        public int ArmyPower(DataRegistry data)
        {
            int power = 0;
            for (int i = 0; i < stacks.Count; i++)
            {
                UnitDef u = data.Unit(stacks[i].unitId);
                if (u != null) power += stacks[i].count * (u.attack + u.hp / 10 + u.defense);
            }
            return power;
        }

        public bool Train(string unitId, int count, long now, DataRegistry data, StrongholdState stronghold, out string message)
        {
            UnitDef unit = data.Unit(unitId);
            if (unit == null) { message = "Unknown unit"; return false; }
            if (unit.race != raceId) { message = "Switch to " + unit.race + " first (or convert the army)"; return false; }
            if (count <= 0) { message = "Bad count"; return false; }
            if (SupplyUsed(data) + count * unit.supply > stronghold.Effect("troopCapacity", data))
            { message = "Troop supply cap reached (upgrade Stronghold)"; return false; }

            ResourceSet cost = unit.cost.Clone();
            cost.Scale(Fixed.Bp * count);
            if (!stronghold.resources.Covers(cost)) { message = "Not enough resources"; return false; }
            stronghold.resources.Subtract(cost);

            int speedBp = Fixed.Bp + stronghold.Effect("trainSpeedBp", data);
            long seconds = Fixed.BpDiv((long)count * data.Config.training.baseSecondsPerUnit, speedBp);
            training.Add(new TrainingOrder
            {
                unitId = unitId,
                count = count,
                finishUnix = now + Math.Max(1, seconds)
            });
            message = "Training " + count + " " + unit.name + " (" + GameTime.Duration(seconds) + ")";
            return true;
        }

        public void UpdateTraining(long now, List<string> log)
        {
            for (int i = training.Count - 1; i >= 0; i--)
            {
                TrainingOrder order = training[i];
                if (order.finishUnix > now) continue;
                Add(order.unitId, order.count);
                if (log != null) log.Add(order.count + " troops finished training");
                training.RemoveAt(i);
            }
        }

        /// <summary>
        /// Free race switching with a conversion chart: every stack is mapped to the
        /// equivalent role in the new race and a fraction of the unit cost is charged.
        /// </summary>
        public bool SwitchRace(string newRace, long now, DataRegistry data, StrongholdState stronghold, out string message)
        {
            if (newRace == raceId) { message = "Already " + data.Race(newRace).name; return false; }
            if (data.Race(newRace) == null) { message = "Unknown race"; return false; }

            var converted = new List<UnitStack>();
            var totalCost = new ResourceSet();
            for (int i = 0; i < stacks.Count; i++)
            {
                UnitStack stack = stacks[i];
                if (stack.count <= 0) continue;
                ConversionGroup group = FindGroupFor(stack.unitId, data);
                if (group == null) { message = "No conversion entry for " + stack.unitId; return false; }
                string newUnitId = group.UnitFor(newRace);
                if (string.IsNullOrEmpty(newUnitId)) { message = "Conversion chart incomplete for " + newRace; return false; }
                converted.Add(new UnitStack { unitId = newUnitId, count = stack.count });
                ResourceSet cost = group.cost.Clone();
                cost.Scale(data.Config.training.conversionCostBp);
                cost.Scale(Fixed.Bp * Math.Max(1, stack.count / 10));
                totalCost.Add(cost);
            }
            if (!stronghold.resources.Covers(totalCost)) { message = "Conversion needs " + totalCost.gold + "g / " + totalCost.wood + "w"; return false; }

            stronghold.resources.Subtract(totalCost);
            stacks = converted;
            raceId = newRace;
            message = "Converted army to " + data.Race(newRace).name + " for " + totalCost.gold + "g / " + totalCost.wood + "w";
            return true;
        }

        public ConversionGroup FindGroupFor(string unitId, DataRegistry data)
        {
            foreach (ConversionGroup group in data.AllConversionGroups)
                for (int i = 0; i < group.entries.Count; i++)
                    if (group.entries[i].unitId == unitId) return group;
            return null;
        }

        /// <summary>
        /// Post-battle losses. Kills are permanent; routed troops become wounded
        /// according to the race's injuryLossBp (Lich/Sylvani lose very few).
        /// </summary>
        public void ApplyLosses(List<Nore.Combat.SquadOutcome> outcomes, int team, long now,
                                DataRegistry data, StrongholdState stronghold, out string message)
        {
            int killed = 0, wounded = 0, recovered = 0;
            RaceModifiers mods = data.Race(raceId).modifiers;
            var woundedByUnit = new Dictionary<string, int>();
            for (int i = 0; i < outcomes.Count; i++)
            {
                Nore.Combat.SquadOutcome o = outcomes[i];
                if (o.team != team) continue;
                int dead = o.killed;
                int routed = o.routed;
                int permanent = Fixed.BpMul(routed, mods.injuryLossBp);
                int healable = routed - permanent;
                Remove(o.unitId, dead + permanent);
                killed += dead + permanent;
                if (healable > 0)
                {
                    int key;
                    woundedByUnit.TryGetValue(o.unitId, out key);
                    woundedByUnit[o.unitId] = key + healable;
                    wounded += healable;
                }
                recovered += o.survivors;
            }
            foreach (var pair in woundedByUnit)
            {
                int cap = stronghold.Effect("hospitalCap", data);
                int accepted = Math.Min(pair.Value, Math.Max(0, cap - HospitalLoad()));
                if (accepted <= 0) continue;
                long seconds = Math.Max(1, Fixed.BpDiv((long)accepted * data.Config.training.hospitalHealSecondsPerUnit,
                                                      Fixed.Bp + stronghold.Effect("healRateBp", data)));
                hospital.Add(new WoundedOrder { unitId = pair.Key, count = accepted, finishUnix = now + seconds });
            }
            message = "Losses: " + killed + " permanent, " + wounded + " wounded (hospital), " + recovered + " survivors";
        }

        public int HospitalLoad()
        {
            int total = 0;
            for (int i = 0; i < hospital.Count; i++) total += hospital[i].count;
            return total;
        }

        public void UpdateHospital(long now, List<string> log)
        {
            for (int i = hospital.Count - 1; i >= 0; i--)
            {
                WoundedOrder w = hospital[i];
                if (w.finishUnix > now) continue;
                Add(w.unitId, w.count);
                if (log != null) log.Add(w.count + " wounded troops recovered");
                hospital.RemoveAt(i);
            }
        }
    }
}
