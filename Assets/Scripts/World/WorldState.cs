using System;
using System.Collections.Generic;
using Nore.Core;
using Nore.Data;
using Nore.Economy;
using Nore.Military;

namespace Nore.World
{
    [Serializable] public class CityState
    {
        public string cityId;
        public int strongholdsRemaining;
        public string ownerHouseId = "";
    }

    /// <summary>
    /// Semi-open world exploration, city/stronghold sieges, House territory control
    /// and PvP/event hooks. All rules are local and deterministic; the networking
    /// boundary is a single interface you can implement later (see INetworkHooks).
    /// </summary>
    [Serializable]
    public class WorldState
    {
        public string kingdomId = "athaly";
        public string houseId = "house_ember";
        public string currentNodeId = "n01";
        public List<string> exploredNodes = new List<string>();
        public List<CityState> cities = new List<CityState>();
        public int activity, cityCredit, voidLevel = 1, duelTowerFloor = 1;
        public int arenaRating = 1000, clashRoundsWon, nexusKarma, emperorPoints;
        public long lastDailyResetUnix;
        public List<string> journal = new List<string>();

        public static WorldState CreateNew(DataRegistry data, long now)
        {
            var w = new WorldState
            {
                kingdomId = data.Config.start.kingdom,
                lastDailyResetUnix = now
            };
            foreach (CityDef city in data.World.cities)
                w.cities.Add(new CityState { cityId = city.id, strongholdsRemaining = city.strongholds, ownerHouseId = "" });
            w.exploredNodes.Add("n01");
            w.Note("Began in " + data.Kingdom(w.kingdomId).name + " as a member of " + data.World.houses[0].name);
            return w;
        }

        public CityState City(string id)
        {
            for (int i = 0; i < cities.Count; i++) if (cities[i].cityId == id) return cities[i];
            return null;
        }

        public int TerritoryCount()
        {
            int count = 0;
            for (int i = 0; i < cities.Count; i++) if (cities[i].ownerHouseId == houseId) count++;
            return count;
        }

        public void Note(string line)
        {
            if (journal.Count >= 120) journal.RemoveAt(0);
            journal.Add(line);
        }

        /// <summary>Move to a map node, collect loot, and report whether it starts a battle.</summary>
        public bool Explore(string nodeId, DataRegistry data, StrongholdState stronghold,
                            ArmyState army, DeterministicRng rng,
                            out string message, out MapNodeDef encounter, out ResourceSet reward)
        {
            encounter = null;
            reward = new ResourceSet();
            MapNodeDef node = data.Node(nodeId);
            if (node == null) { message = "Unknown map node"; return false; }
            if (node.region != "nore" && stronghold.level < 5)
            { message = "Stronghold 5 required to reach " + node.region; return false; }

            currentNodeId = nodeId;
            bool firstVisit = !exploredNodes.Contains(nodeId);
            if (firstVisit) exploredNodes.Add(nodeId);
            reward = node.reward.Clone();
            if (!firstVisit) reward.Scale(2500); // repeatable nodes give 25%
            stronghold.resources.Add(reward);
            activity += 10;
            message = node.name + ": " + node.label + " (+" + reward.gold + "g / +" + reward.wood + "w / +" + reward.mana + "m)";

            if (node.kind == "enemy" || node.kind == "boss")
            {
                encounter = node;
                message += " -> BATTLE";
            }
            else if (node.kind == "event")
            {
                stronghold.resources.activity += 25;
                activity += 15;
                message += " -> event triggered";
            }
            Note("T+" + GameTime.NowUnix() + " " + message);
            return true;
        }

        /// <summary>Siege one stronghold of a city; capture only when all are destroyed.</summary>
        public bool Siege(string cityId, bool battleWon, DataRegistry data, StrongholdState stronghold, out string message)
        {
            CityDef def = data.City(cityId);
            CityState state = City(cityId);
            if (def == null || state == null) { message = "Unknown city"; return false; }
            if (state.ownerHouseId == houseId) { message = "Your House already holds " + def.name; return false; }
            if (!battleWon)
            {
                message = "Siege of " + def.name + " repulsed (" + state.strongholdsRemaining + " strongholds left)";
                Note(message);
                return false;
            }
            state.strongholdsRemaining = Math.Max(0, state.strongholdsRemaining - 1);
            activity += 20;
            stronghold.resources.prosperity += def.tier * 25;
            if (state.strongholdsRemaining > 0)
            {
                message = "Stronghold destroyed! " + state.strongholdsRemaining + " remain in " + def.name;
                Note(message);
                return true;
            }
            state.ownerHouseId = houseId;
            cityCredit += def.tier * 100;
            emperorPoints += def.tier * 10;
            message = "CITY CAPTURED: " + def.name + " now belongs to " + houseId + " (+" + (def.tier * 100) + " City Credit)";
            Note(message);
            return true;
        }

        public bool JoinHouse(string newHouseId, DataRegistry data, out string message)
        {
            HouseDef house = null;
            for (int i = 0; i < data.World.houses.Count; i++)
                if (data.World.houses[i].id == newHouseId) house = data.World.houses[i];
            if (house == null) { message = "Unknown House"; return false; }
            houseId = newHouseId;
            message = "Joined " + house.name + " (" + house.motto + ")";
            Note(message);
            return true;
        }

        /// <summary>Local PvP/event hook. Replace with a server call for a real MMO.</summary>
        public bool RunPvp(string modeId, int armyPower, DeterministicRng rng, DataRegistry data,
                           StrongholdState stronghold, DragonManager dragons, out string message)
        {
            PvpModeDef mode = null;
            for (int i = 0; i < data.World.pvpModes.Count; i++)
                if (data.World.pvpModes[i].id == modeId) mode = data.World.pvpModes[i];
            if (mode == null) { message = "Unknown PvP mode"; return false; }

            int opponent = 3000 + rng.Range(0, 6000) + duelTowerFloor * 120;
            int variance = Fixed.BpMul(armyPower, rng.Range(8500, 11500));
            bool win = variance >= opponent;
            int delta = win ? rng.Range(12, 32) : -rng.Range(8, 20);
            arenaRating = Math.Max(0, arenaRating + delta);
            if (modeId == "duel_tower" && win) duelTowerFloor++;
            if (modeId == "nexus") { nexusKarma += win ? 35 : 10; activity += 25; }
            if (modeId == "clash_of_fate" && win) clashRoundsWon++;
            if (modeId == "void") voidLevel += win ? 1 : 0;
            int linari = win ? mode.rewardLinari : mode.rewardLinari / 4;
            stronghold.resources.linari += linari;
            stronghold.resources.activity += 20;
            activity += 20;
            if (win) dragons.GrantEventCrystals(data, null);
            message = mode.name + ": " + (win ? "WIN" : "LOSS") + " (" + variance + " vs " + opponent + ") " +
                      (delta >= 0 ? "+" : "") + delta + " rating, +" + linari + " Linari";
            Note(message);
            return win;
        }

        public bool RunEvent(string eventId, DataRegistry data, StrongholdState stronghold,
                             DragonManager dragons, List<string> log, out string message)
        {
            EventDef def = null;
            for (int i = 0; i < data.World.events.Count; i++)
                if (data.World.events[i].id == eventId) def = data.World.events[i];
            if (def == null) { message = "Unknown event"; return false; }
            dragons.GrantEventCrystals(data, log);
            stronghold.resources.soulbits += 2;
            stronghold.resources.activity += 50;
            activity += 50;
            stronghold.resources.gold += 500;
            message = def.name + " cleared: +" + data.Config.progression.dragonCrystalPerEvent +
                      " crystals, +500g, +2 Soulbits, +50 Activity";
            Note(message);
            if (log != null) log.Add(message);
            return true;
        }

        public void DailyReset(long now, DataRegistry data, DragonManager dragons, List<string> log)
        {
            if (now - lastDailyResetUnix < Math.Max(1, data.Config.offline.dailyResetSeconds)) return;
            lastDailyResetUnix = now;
            activity = 0;
            dragons.GrantEventCrystals(data, log);
            if (log != null) log.Add("Daily reset: activity cleared, daily chest granted");
            Note("Daily reset");
        }
    }

    /// <summary>Where a real MMORTS backend plugs in; the prototype runs everything locally.</summary>
    public interface INetworkHooks
    {
        void ReportBattleResult(string battleId, string winnerId, string stateHash);
        void ClaimCity(string cityId, string houseId);
        void InterceptCaravan(string caravanId, string attackerId);
    }
}
