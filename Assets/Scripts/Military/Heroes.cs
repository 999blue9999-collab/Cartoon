using System;
using System.Collections.Generic;
using Nore.Core;
using Nore.Data;

namespace Nore.Military
{
    /// <summary>One owned hero. Gear is stored slot-aligned with HeroDef.gearSlots.</summary>
    [Serializable]
    public class HeroState
    {
        public string heroId;
        public int level = 1;
        public int xp;
        public int skillPoints = 1;
        public int allocatedMight, allocatedMagic, allocatedStamina, allocatedCommand;
        public int energy = 20;
        public long lastEnergyUnix;
        public List<string> gear = new List<string>();     // one entry per gear slot, "" = empty
        public List<int> skillRanks = new List<int>();     // one entry per HeroDef.skillIds

        public int GearBonus(string stat, DataRegistry data)
        {
            int total = 0;
            for (int i = 0; i < gear.Count; i++)
            {
                GearDef item = data.GearItem(gear[i]);
                if (item != null && item.stat == stat) total += item.value;
            }
            return total;
        }

        public int Might(DataRegistry data) => data.Hero(heroId).stats.might + allocatedMight + GearBonus("might", data);
        public int Magic(DataRegistry data) => data.Hero(heroId).stats.magic + allocatedMagic + GearBonus("magic", data);
        public int Stamina(DataRegistry data) => data.Hero(heroId).stats.stamina + allocatedStamina + GearBonus("stamina", data);
        public int Command(DataRegistry data) => data.Hero(heroId).stats.command + allocatedCommand + GearBonus("command", data);

        public int TroopSlots(DataRegistry data)
        {
            int slots = data.Hero(heroId).maxTroopSlots;
            if (heroId == "avalon") slots += 1; // Natural Leader (data already includes 3; kept explicit for clarity)
            if (slots > 3) slots = 3;
            return slots;
        }

        public int Hp(DataRegistry data) => 800 + Stamina(data) * 22;

        public bool Allocate(string stat, out string message)
        {
            if (skillPoints <= 0) { message = "No skill points"; return false; }
            switch (stat)
            {
                case "might": allocatedMight++; break;
                case "magic": allocatedMagic++; break;
                case "stamina": allocatedStamina++; break;
                case "command": allocatedCommand++; break;
                default: message = "Unknown stat"; return false;
            }
            skillPoints--;
            message = "+1 " + stat;
            return true;
        }

        public void AddXp(int amount, DataRegistry data, StrongholdHolder holder, out string message)
        {
            xp += amount;
            int needed = level * data.Config.progression.heroXpPerLevel;
            int levels = 0;
            while (xp >= needed && level < holder.HeroLevelCap)
            {
                xp -= needed;
                level++;
                skillPoints += data.Config.progression.heroSkillPointsPerLevel;
                levels++;
                needed = level * data.Config.progression.heroXpPerLevel;
            }
            message = levels > 0 ? "Level up! Lv." + level + " (+" + levels + " skill points)" : "+" + amount + " XP";
        }

        public bool Equip(string slot, string gearId, DataRegistry data, out string message)
        {
            HeroDef def = data.Hero(heroId);
            int index = def.gearSlots.IndexOf(slot);
            if (index < 0) { message = "No such slot"; return false; }
            while (gear.Count < def.gearSlots.Count) gear.Add("");
            while (skillRanks.Count < def.skillIds.Count) skillRanks.Add(0);
            gear[index] = gearId;
            message = string.IsNullOrEmpty(gearId) ? slot + " unequipped" : slot + " equipped: " + gearId;
            return true;
        }
    }

    /// <summary>Tiny adapter so HeroState stays independent of the stronghold implementation.</summary>
    public sealed class StrongholdHolder
    {
        public int HeroLevelCap = 99;
    }

    [Serializable]
    public class HeroManager
    {
        public List<HeroState> heroes = new List<HeroState>();
        public List<string> deployed = new List<string>();
        public List<string> ownedGear = new List<string>();
        public int chestsOpened;

        public static HeroManager CreateNew(DataRegistry data, long now)
        {
            var m = new HeroManager();
            m.heroes.Add(Make("rufio", data, now));
            m.heroes.Add(Make("avalon", data, now));
            m.heroes.Add(Make("avril", data, now));
            m.deployed.Add("rufio");
            m.deployed.Add("avalon");
            m.ownedGear.Add("claymore");
            m.ownedGear.Add("conjurers_cap");
            m.ownedGear.Add("commander_helm");
            m.ownedGear.Add("war_banner");
            m.ownedGear.Add("frost_trinket");
            return m;
        }

        private static HeroState Make(string heroId, DataRegistry data, long now)
        {
            HeroDef def = data.Hero(heroId);
            var h = new HeroState { heroId = heroId, lastEnergyUnix = now, energy = 20 };
            for (int i = 0; i < def.gearSlots.Count; i++) h.gear.Add("");
            for (int i = 0; i < def.skillIds.Count; i++) h.skillRanks.Add(1);
            return h;
        }

        public HeroState Get(string heroId)
        {
            for (int i = 0; i < heroes.Count; i++) if (heroes[i].heroId == heroId) return heroes[i];
            return null;
        }

        public void UpdateEnergy(long now, DataRegistry data, List<string> log)
        {
            long period = Math.Max(1, data.Config.offline.energyRegenSeconds);
            for (int i = 0; i < heroes.Count; i++)
            {
                HeroState h = heroes[i];
                long elapsed = now - h.lastEnergyUnix;
                if (elapsed < period) continue;
                int gained = (int)Math.Min(20 - h.energy, elapsed / period);
                if (gained <= 0) { h.lastEnergyUnix = now; continue; }
                h.energy = Math.Min(20, h.energy + gained);
                h.lastEnergyUnix = now - (elapsed % period);
                if (log != null) log.Add(data.Hero(h.heroId).name + " recovered " + gained + " Energy");
            }
        }

        public bool SpendEnergy(string heroId, int cost, out string message)
        {
            HeroState h = Get(heroId);
            if (h == null) { message = "Hero not owned"; return false; }
            if (h.energy < cost) { message = "Not enough Energy"; return false; }
            h.energy -= cost;
            message = "-" + cost + " Energy";
            return true;
        }

        /// <summary>Hero chest: 10 Soulbits, deterministic roll, never yields shop-only Jacques.</summary>
        public bool OpenChest(DeterministicRng rng, DataRegistry data, Nore.Economy.StrongholdState stronghold, out string message)
        {
            if (stronghold.resources.soulbits < 10) { message = "Needs 10 Soulbits"; return false; }
            stronghold.resources.soulbits -= 10;
            chestsOpened++;
            var pool = new List<HeroDef>();
            foreach (HeroDef def in data.AllHeroes)
                if (def.unlock == "chest" && Get(def.id) == null) pool.Add(def);
            if (pool.Count == 0) { message = "All chest heroes owned (+20 Soulbits refunded)"; stronghold.resources.soulbits += 20; return true; }
            HeroDef picked = pool[rng.NextInt(pool.Count)];
            heroes.Add(Make(picked.id, data, GameTime.NowUnix()));
            message = "Hero chest: " + picked.name + " (" + picked.role + ")";
            return true;
        }

        public bool BuyShopHero(string heroId, DataRegistry data, Nore.Economy.StrongholdState stronghold, out string message)
        {
            HeroDef def = data.Hero(heroId);
            if (def == null || def.unlock != "shop") { message = "Not a shop hero"; return false; }
            if (Get(heroId) != null) { message = "Already owned"; return false; }
            if (stronghold.resources.linari < 100) { message = "Needs 100 Linari (first hero deal)"; return false; }
            stronghold.resources.linari -= 100;
            heroes.Add(Make(heroId, data, GameTime.NowUnix()));
            message = "Recruited " + def.name;
            return true;
        }

        public void ToggleDeploy(string heroId, DataRegistry data, out string message)
        {
            if (deployed.Contains(heroId)) { deployed.Remove(heroId); message = "Removed from formation"; return; }
            if (deployed.Count >= 3) { message = "Formation holds at most 3 heroes"; return; }
            deployed.Add(heroId);
            message = "Added to formation";
        }

        public int TotalCommand(DataRegistry data)
        {
            int total = 0;
            for (int i = 0; i < deployed.Count; i++)
            {
                HeroState h = Get(deployed[i]);
                if (h != null) total += h.Command(data);
            }
            return total;
        }
    }
}
