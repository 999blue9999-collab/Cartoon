using System;
using System.Collections.Generic;
using Nore.Core;
using Nore.Data;

namespace Nore.Economy
{
    [Serializable]
    public class BuildingState
    {
        public string id;
        public int level;
        public long upgradeFinishUnix;

        public bool IsUpgrading(long now) => upgradeFinishUnix > now;
        public long Remaining(long now) => upgradeFinishUnix > now ? upgradeFinishUnix - now : 0;
    }

    /// <summary>
    /// Stronghold, buildings, timers, prosperity and resource production.
    /// Timers are absolute Unix seconds so offline progress is just arithmetic,
    /// never "time spent in the app".
    /// </summary>
    [Serializable]
    public class StrongholdState
    {
        public int level = 1;
        public ResourceSet resources = new ResourceSet();
        public List<BuildingState> buildings = new List<BuildingState>();
        public long lastProductionUnix;

        public static StrongholdState CreateNew(DataRegistry data, long now)
        {
            var s = new StrongholdState();
            s.level = Math.Max(1, data.Config.start.strongholdLevel);
            s.resources = new ResourceSet
            {
                gold = data.Config.start.gold,
                wood = data.Config.start.wood,
                mana = data.Config.start.mana,
                prosperity = data.Config.start.prosperity,
                linari = data.Config.start.linari,
                soulbits = data.Config.start.soulbits,
                activity = data.Config.start.activity
            };
            s.lastProductionUnix = now;
            foreach (BuildingDef def in data.AllBuildings)
                s.buildings.Add(new BuildingState
                {
                    id = def.id,
                    level = def.unlockStrongholdLevel <= s.level ? 1 : 0
                });
            return s;
        }

        public BuildingState Get(string id)
        {
            for (int i = 0; i < buildings.Count; i++) if (buildings[i].id == id) return buildings[i];
            return null;
        }

        public int Level(string id)
        {
            BuildingState b = Get(id);
            return b == null ? 0 : b.level;
        }

        public int ActiveUpgrades(long now)
        {
            int count = 0;
            for (int i = 0; i < buildings.Count; i++) if (buildings[i].IsUpgrading(now)) count++;
            return count;
        }

        public int UpgradeSlots() => 1 + Math.Max(0, (level - 1) / 4);

        public int Effect(string stat, DataRegistry data)
        {
            int total = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                BuildingState state = buildings[i];
                if (state.level <= 0) continue;
                BuildingDef def = data.Building(state.id);
                if (def == null) continue;
                for (int j = 0; j < def.effects.Count; j++)
                {
                    BuildingEffect e = def.effects[j];
                    if (e.stat == stat) total += e.baseValue + e.perLevel * state.level;
                }
            }
            return total;
        }

        public long StorageCap(DataRegistry data)
        {
            long cap = Effect("storageCap", data);
            return cap <= 0 ? 50000 : cap;
        }

        public ResourceSet CostFor(BuildingDef def, int nextLevel, DataRegistry data)
        {
            ResourceSet cost = def.cost.Clone();
            int bp = Fixed.Bp;
            for (int l = 1; l < nextLevel; l++) bp = Fixed.BpMul(bp, def.costGrowthBp);
            cost.Scale(bp);
            return cost;
        }

        public int SecondsFor(BuildingDef def, int nextLevel)
        {
            int bp = Fixed.Bp;
            for (int l = 1; l < nextLevel; l++) bp = Fixed.BpMul(bp, def.buildGrowthBp);
            long seconds = Fixed.BpMul(def.buildSeconds, bp);
            return (int)Fixed.Clamp(seconds, 1, 60L * 60 * 24 * 30);
        }

        public bool CanUpgrade(string id, long now, DataRegistry data, out string reason)
        {
            reason = "";
            BuildingDef def = data.Building(id);
            BuildingState state = Get(id);
            if (def == null || state == null) { reason = "Unknown building"; return false; }
            if (def.unlockStrongholdLevel > level) { reason = "Needs Stronghold " + def.unlockStrongholdLevel; return false; }
            if (state.level >= def.maxLevel) { reason = "Max level"; return false; }
            if (state.IsUpgrading(now)) { reason = "Already upgrading"; return false; }
            if (ActiveUpgrades(now) >= UpgradeSlots()) { reason = "No free builder slot"; return false; }
            ResourceSet cost = CostFor(def, state.level + 1, data);
            if (!resources.Covers(cost)) { reason = "Not enough resources"; return false; }
            return true;
        }

        public bool StartUpgrade(string id, long now, DataRegistry data, out string message)
        {
            string reason;
            if (!CanUpgrade(id, now, data, out reason)) { message = reason; return false; }
            BuildingDef def = data.Building(id);
            BuildingState state = Get(id);
            ResourceSet cost = CostFor(def, state.level + 1, data);
            resources.Subtract(cost);
            int seconds = SecondsFor(def, state.level + 1);
            state.upgradeFinishUnix = now + seconds;
            message = def.name + " -> Lv." + (state.level + 1) + " (" + GameTime.Duration(seconds) + ")";
            return true;
        }

        public bool SpeedUp(string id, long now, DataRegistry data, out string message)
        {
            BuildingState state = Get(id);
            if (state == null || !state.IsUpgrading(now)) { message = "Nothing to speed up"; return false; }
            long remaining = state.Remaining(now);
            long cost = Math.Min(2000, 1 + remaining / 60);
            if (resources.linari < cost) { message = "Needs " + cost + " Linari"; return false; }
            resources.linari -= cost;
            state.upgradeFinishUnix = now;
            message = "Speed-up spent " + cost + " Linari";
            Update(now, data, null);
            return true;
        }

        /// <summary>Idempotent catch-up: production + completed timers. Safe to call every frame.</summary>
        public void Update(long now, DataRegistry data, List<string> log)
        {
            if (now < lastProductionUnix) lastProductionUnix = now;
            long elapsed = now - lastProductionUnix;
            if (elapsed > 0)
            {
                long cap = StorageCap(data);
                long gold = (long)Effect("goldPerMinute", data) * elapsed / 60;
                long wood = (long)Effect("woodPerMinute", data) * elapsed / 60;
                long mana = (long)Effect("manaPerMinute", data) * elapsed / 60;
                resources.gold = Math.Min(cap, resources.gold + gold);
                resources.wood = Math.Min(cap, resources.wood + wood);
                resources.mana = Math.Min(cap, resources.mana + mana);
                lastProductionUnix = now;
            }

            for (int i = 0; i < buildings.Count; i++)
            {
                BuildingState state = buildings[i];
                if (state.upgradeFinishUnix > 0 && state.upgradeFinishUnix <= now)
                {
                    BuildingDef def = data.Building(state.id);
                    state.level++;
                    state.upgradeFinishUnix = 0;
                    if (def != null)
                    {
                        resources.prosperity += def.prosperityPerLevel * state.level;
                        if (state.id == "stronghold") level = state.level;
                        if (log != null) log.Add(def.name + " reached Lv." + state.level + " (+" + (def.prosperityPerLevel * state.level) + " Prosperity)");
                    }
                }
            }
        }
    }

    [Serializable]
    public class CaravanState
    {
        public string id;
        public string fromNodeId;
        public ResourceSet cargo = new ResourceSet();
        public long departUnix, arriveUnix;
        public int escortPower;
        public string ownerHouse = "";
        public int status; // 0 = travelling, 1 = delivered, 2 = intercepted/lost
        public string interceptedBy = "";
        public long Remaining(long now) => arriveUnix > now ? arriveUnix - now : 0;
    }

    /// <summary>Caravan transport and PvP interception, fully offline-safe.</summary>
    [Serializable]
    public class CaravanSystem
    {
        public List<CaravanState> caravans = new List<CaravanState>();
        public int nextId = 1;
        public long lastRaidUnix;

        public CaravanState Launch(string nodeId, ResourceSet cargo, long now, int escortPower, int travelSeconds, string ownerHouse)
        {
            var c = new CaravanState
            {
                id = "CAR-" + nextId++,
                fromNodeId = nodeId,
                cargo = cargo.Clone(),
                departUnix = now,
                arriveUnix = now + Math.Max(5, travelSeconds),
                escortPower = Math.Max(1, escortPower),
                ownerHouse = ownerHouse,
                status = 0
            };
            caravans.Add(c);
            return c;
        }

        public void Update(long now, StrongholdState stronghold, DataRegistry data, List<string> log)
        {
            int delivered = 0;
            for (int i = 0; i < caravans.Count; i++)
            {
                CaravanState c = caravans[i];
                if (c.status != 0 || c.arriveUnix > now) continue;
                c.status = 1;
                ResourceSet bonus = c.cargo.Clone();
                bonus.Scale(Fixed.Bp + stronghold.Effect("caravanBonusBp", data));
                stronghold.resources.Add(bonus);
                delivered++;
                if (log != null) log.Add(c.id + " delivered cargo: +" + bonus.gold + "g / +" + bonus.wood + "w");
            }
            if (delivered > 0) caravans.RemoveAll(c => c.status != 0);
        }

        /// <summary>
        /// A rival attacks a caravan. The owner's escort power plus a small deterministic
        /// variance decides the outcome; a successful raid steals the cargo.
        /// </summary>
        public bool Intercept(string caravanId, int attackerPower, string attackerName, long now,
                              DeterministicRng rng, out string message, out ResourceSet stolen)
        {
            stolen = new ResourceSet();
            CaravanState target = null;
            for (int i = 0; i < caravans.Count; i++)
                if (caravans[i].id == caravanId && caravans[i].status == 0) { target = caravans[i]; break; }
            if (target == null) { message = "Caravan already arrived or lost"; return false; }

            int variance = rng.Range(8500, 11500);
            int attack = Fixed.BpMul(attackerPower, variance);
            if (attack <= target.escortPower)
            {
                message = attackerName + " failed to break the escort (" + attack + " vs " + target.escortPower + ")";
                return false;
            }
            target.status = 2;
            target.interceptedBy = attackerName;
            stolen = target.cargo.Clone();
            lastRaidUnix = now;
            message = attackerName + " intercepted " + target.id + " and stole " + stolen.gold + "g / " + stolen.wood + "w";
            caravans.Remove(target);
            return true;
        }

        public List<CaravanState> Active()
        {
            var result = new List<CaravanState>();
            for (int i = 0; i < caravans.Count; i++) if (caravans[i].status == 0) result.Add(caravans[i]);
            return result;
        }
    }
}
