using System;
using System.Collections.Generic;
using Nore.Core;

namespace Nore.Data
{
    // ---------------- config.json ----------------
    [Serializable] public class ConfigData
    {
        public int version;
        public int tickRate = 10;
        public int maxBattleTicks = 1800;
        public int maxSquads = 96;
        public GridConfig grid = new GridConfig();
        public MoraleConfig morale = new MoraleConfig();
        public CombatConfig combat = new CombatConfig();
        public List<RoleCounter> roleCounters = new List<RoleCounter>();
        public OfflineConfig offline = new OfflineConfig();
        public StartConfig start = new StartConfig();
        public TrainingConfig training = new TrainingConfig();
        public ProgressionConfig progression = new ProgressionConfig();
    }

    [Serializable] public class GridConfig
    {
        public int columns = 14;
        public int rows = 8;
        public int attackerMaxColumn = 4;
        public int defenderMinColumn = 9;
    }

    [Serializable] public class MoraleConfig
    {
        public int baseValue = 1000;
        public int commandPerPoint = 12;
        public int casualtyShockPerPercent = 9;
        public int routThreshold = 260;
        public int rallyThreshold = 380;
        public int rallyTicks = 30;
        public int allyRoutShock = 45;
        public int heroDeathShock = 120;
        public int updateIntervalTicks = 5;
        public int shockDecayPerTick = 1;
    }

    [Serializable] public class CombatConfig
    {
        public int defenseSoftCapBp = 30000;
        public int magicResistCapBp = 9000;
        public int moraleDamageFloorBp = 5000;
        public int routedEscapeTicks = 60;
        public int commandAuraRange = 4000;
        public int commandAuraFalloffBp = 5000;
        public int criticalChanceBp = 500;
        public int criticalBonusBp = 15000;
    }

    [Serializable] public class RoleCounter { public string attacker; public string defender; public int bp = 10000; }

    [Serializable] public class OfflineConfig
    {
        public int maxCatchupSeconds = 28800;
        public int productionBatchSeconds = 60;
        public int energyRegenSeconds = 300;
        public int dailyResetSeconds = 86400;
    }

    [Serializable] public class StartConfig
    {
        public long gold, wood, mana, prosperity, linari, soulbits, activity;
        public int strongholdLevel = 1;
        public string race = "human";
        public string kingdom = "athaly";
    }

    [Serializable] public class TrainingConfig
    {
        public int baseSecondsPerUnit = 4;
        public int hospitalHealSecondsPerUnit = 3;
        public int conversionCostBp = 2500;
    }

    [Serializable] public class ProgressionConfig
    {
        public int heroXpPerLevel = 100;
        public int heroSkillPointsPerLevel = 1;
        public int dragonCrystalPerEvent = 12;
        public int siegeStrongholdsPerCity = 3;
    }

    // ---------------- shared ----------------
    /// <summary>All currencies in one flat object so JsonUtility can serialize it (no dictionaries).</summary>
    [Serializable] public class ResourceSet
    {
        public long gold, wood, mana, prosperity, linari, soulbits, activity, cityCredit;

        public static ResourceSet Zero() => new ResourceSet();

        public ResourceSet Clone() => (ResourceSet)MemberwiseClone();

        public void Add(ResourceSet other, long multiplier = 1)
        {
            if (other == null) return;
            gold += other.gold * multiplier;
            wood += other.wood * multiplier;
            mana += other.mana * multiplier;
            prosperity += other.prosperity * multiplier;
            linari += other.linari * multiplier;
            soulbits += other.soulbits * multiplier;
            activity += other.activity * multiplier;
            cityCredit += other.cityCredit * multiplier;
        }

        public void Scale(int bp)
        {
            gold = Fixed.BpMul(gold, bp);
            wood = Fixed.BpMul(wood, bp);
            mana = Fixed.BpMul(mana, bp);
            prosperity = Fixed.BpMul(prosperity, bp);
            linari = Fixed.BpMul(linari, bp);
            soulbits = Fixed.BpMul(soulbits, bp);
            activity = Fixed.BpMul(activity, bp);
            cityCredit = Fixed.BpMul(cityCredit, bp);
        }

        public bool Covers(ResourceSet cost)
        {
            if (cost == null) return true;
            return gold >= cost.gold && wood >= cost.wood && mana >= cost.mana &&
                   linari >= cost.linari && soulbits >= cost.soulbits;
        }

        public void Subtract(ResourceSet cost)
        {
            if (cost == null) return;
            gold -= cost.gold; wood -= cost.wood; mana -= cost.mana;
            linari -= cost.linari; soulbits -= cost.soulbits;
        }

        public override string ToString() =>
            "G " + gold + "  W " + wood + "  M " + mana + "  P " + prosperity +
            "  L " + linari + "  S " + soulbits;
    }

    // ---------------- buildings.json ----------------
    [Serializable] public class BuildingEffect { public string stat; public int baseValue; public int perLevel; }

    [Serializable] public class BuildingDef
    {
        public string id, name, category;
        public int maxLevel = 18;
        public int unlockStrongholdLevel = 1;
        public ResourceSet cost = new ResourceSet();
        public int costGrowthBp = 15000;
        public int buildSeconds = 60;
        public int buildGrowthBp = 15500;
        public int prosperityPerLevel = 10;
        public List<BuildingEffect> effects = new List<BuildingEffect>();
    }

    [Serializable] public class BuildingTable { public List<BuildingDef> items = new List<BuildingDef>(); }

    // ---------------- units.json ----------------
    [Serializable] public class UnitDef
    {
        public string id, race, name, role, damageType, special;
        public int tier = 1;
        public int hp, attack, defense, magicResistBp;
        public int range, attackIntervalTicks, moveSpeed, supply = 1, stackSize = 20, ammo;
        public ResourceSet cost = new ResourceSet();
        public List<string> tags = new List<string>();
        public bool HasTag(string tag) => tags != null && tags.Contains(tag);
    }

    [Serializable] public class UnitTable { public List<UnitDef> items = new List<UnitDef>(); }

    // ---------------- races.json ----------------
    [Serializable] public class RaceTrait { public string id, label, description; }

    [Serializable] public class RaceModifiers
    {
        public int damageDealtBp = 10000, damageTakenBp = 10000, magicResistBp;
        public int moraleShockBp = 10000, injuryLossBp = 5000, ammoReserve;
        public int ammoBurstBp = 10000, ammoDryBp = 10000, rampartBonusBp = 10000;
        public int activeBuffBp = 10000, clergyHealBp, speedBp = 10000, hasteBp = 10000;
        public int freeSummonCount, summonIntervalTicks, verdantHealBp;
        public string summonUnitId = "";
    }

    [Serializable] public class RaceDef
    {
        public string id, name, playstyle, homeKingdom, primaryColor, summary;
        public RaceModifiers modifiers = new RaceModifiers();
        public List<RaceTrait> traits = new List<RaceTrait>();
        public List<string> unitIds = new List<string>();
    }

    [Serializable] public class ConversionEntry { public string race, unitId; }

    [Serializable] public class ConversionGroup
    {
        public string id, label;
        public ResourceSet cost = new ResourceSet();
        public List<ConversionEntry> entries = new List<ConversionEntry>();
        public string UnitFor(string race)
        {
            for (int i = 0; i < entries.Count; i++) if (entries[i].race == race) return entries[i].unitId;
            return "";
        }
    }

    [Serializable] public class RaceTable
    {
        public List<RaceDef> items = new List<RaceDef>();
        public List<ConversionGroup> conversionChart = new List<ConversionGroup>();
    }

    // ---------------- heroes.json / skills.json / gear.json ----------------
    [Serializable] public class HeroStats { public int might, magic, stamina, command; }

    [Serializable] public class HeroDef
    {
        public string id, name, role, race, unlock, description;
        public int maxTroopSlots = 2, energyCost = 2;
        public HeroStats stats = new HeroStats();
        public List<string> skillIds = new List<string>();
        public List<string> gearSlots = new List<string>();
    }

    [Serializable] public class HeroTable { public List<HeroDef> items = new List<HeroDef>(); }

    [Serializable] public class SkillDef
    {
        public string id, name, heroId, kind, target, damageType, buffStat, summonUnitId, description;
        public int manaCost, cooldownTicks, radius, power;
        public int mightScaleBp, magicScaleBp, durationTicks, magnitudeBp, summonCount;
    }

    [Serializable] public class SkillTable { public List<SkillDef> items = new List<SkillDef>(); }

    [Serializable] public class GearDef
    {
        public string id, name, slot, stat, rarity, description;
        public int value;
    }

    [Serializable] public class GearTable { public List<GearDef> items = new List<GearDef>(); }

    // ---------------- dragons.json ----------------
    [Serializable] public class TalentMilestone { public int points, valueBp; public string label, stat; }

    [Serializable] public class TalentTreeDef
    {
        public string id, name, description;
        public List<TalentMilestone> milestones = new List<TalentMilestone>();
    }

    [Serializable] public class DragonDef
    {
        public string id, name, element, role, primaryTree, ability, description;
        public int baseAttack, baseHp;
        public List<string> tiers = new List<string>();
    }

    [Serializable] public class AwakeningDef
    {
        public int allTreesPoints = 159, primaryTreePoints = 109;
        public int bonusHpBp = 2500, bonusAttackBp = 2500, bonusCommandBonus = 10;
        public string bonusLabel = "Draconic Awakening";
    }

    [Serializable] public class DragonTable
    {
        public List<TalentTreeDef> trees = new List<TalentTreeDef>();
        public List<DragonDef> items = new List<DragonDef>();
        public AwakeningDef awakening = new AwakeningDef();
    }

    // ---------------- world.json ----------------
    [Serializable] public class KingdomDef { public string id, name, color, location, bonus, description; }
    [Serializable] public class CityDef
    {
        public string id, name, kingdom, resourceBonus;
        public int tier, strongholds, garrisonPower, x, y;
    }
    [Serializable] public class MapNodeDef
    {
        public string id, name, kind, region, kingdom, enemyRace, label;
        public int x, y;
        public ResourceSet reward = new ResourceSet();
    }
    [Serializable] public class PvpModeDef
    {
        public string id, name, scoring, description;
        public int teamSize, rewardLinari;
    }
    [Serializable] public class EventDef
    {
        public string id, name, cadence, reward, description;
        public int partySize;
    }
    [Serializable] public class HouseDef { public string id, name, motto; public int level; }

    [Serializable] public class WorldTable
    {
        public List<string> regions = new List<string>();
        public List<KingdomDef> kingdoms = new List<KingdomDef>();
        public List<CityDef> cities = new List<CityDef>();
        public List<MapNodeDef> nodes = new List<MapNodeDef>();
        public List<PvpModeDef> pvpModes = new List<PvpModeDef>();
        public List<EventDef> events = new List<EventDef>();
        public List<HouseDef> houses = new List<HouseDef>();
    }
}
