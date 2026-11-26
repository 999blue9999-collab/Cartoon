using System;
using System.Collections.Generic;
using System.IO;
using Nore.Core;
using UnityEngine;

namespace Nore.Data
{
    /// <summary>
    /// Single source of truth for every gameplay number. JSON lives in
    /// Assets/Resources/Data (shipped with the build) and can be overridden at
    /// runtime from Application.persistentDataPath/Data without recompiling.
    /// </summary>
    public sealed class DataRegistry
    {
        public static readonly string[] Files =
        {
            "config", "buildings", "races", "units", "heroes",
            "skills", "gear", "dragons", "world"
        };

        public ConfigData Config { get; private set; } = new ConfigData();
        public BuildingTable Buildings { get; private set; } = new BuildingTable();
        public RaceTable Races { get; private set; } = new RaceTable();
        public UnitTable Units { get; private set; } = new UnitTable();
        public HeroTable Heroes { get; private set; } = new HeroTable();
        public SkillTable Skills { get; private set; } = new SkillTable();
        public GearTable Gear { get; private set; } = new GearTable();
        public DragonTable Dragons { get; private set; } = new DragonTable();
        public WorldTable World { get; private set; } = new WorldTable();

        private readonly Dictionary<string, BuildingDef> _buildings = new Dictionary<string, BuildingDef>();
        private readonly Dictionary<string, RaceDef> _races = new Dictionary<string, RaceDef>();
        private readonly Dictionary<string, UnitDef> _units = new Dictionary<string, UnitDef>();
        private readonly Dictionary<string, HeroDef> _heroes = new Dictionary<string, HeroDef>();
        private readonly Dictionary<string, SkillDef> _skills = new Dictionary<string, SkillDef>();
        private readonly Dictionary<string, GearDef> _gear = new Dictionary<string, GearDef>();
        private readonly Dictionary<string, DragonDef> _dragons = new Dictionary<string, DragonDef>();
        private readonly Dictionary<string, TalentTreeDef> _trees = new Dictionary<string, TalentTreeDef>();
        private readonly Dictionary<string, ConversionGroup> _conversion = new Dictionary<string, ConversionGroup>();
        private readonly Dictionary<string, KingdomDef> _kingdoms = new Dictionary<string, KingdomDef>();
        private readonly Dictionary<string, CityDef> _cities = new Dictionary<string, CityDef>();
        private readonly Dictionary<string, MapNodeDef> _nodes = new Dictionary<string, MapNodeDef>();
        private readonly Dictionary<string, string> _roleCounters = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _roleCounterBp = new Dictionary<string, int>();

        public List<string> LoadWarnings { get; } = new List<string>();

        /// <summary>Unity entry point: Resources/Data plus optional persistent-data overrides.</summary>
        public static DataRegistry LoadDefault()
        {
            var texts = new Dictionary<string, string>();
            TextAsset[] assets = Resources.LoadAll<TextAsset>("Data");
            for (int i = 0; i < assets.Length; i++) texts[assets[i].name] = assets[i].text;

            // Editor/standalone fallback: read the source files directly if a Unity version
            // does not import .json as TextAsset (Android/WebGL always use Resources).
#if UNITY_EDITOR || UNITY_STANDALONE
            foreach (string file in Files)
            {
                if (texts.ContainsKey(file)) continue;
                string direct = Path.Combine(Application.dataPath, "Resources", "Data", file + ".json");
                if (File.Exists(direct)) texts[file] = File.ReadAllText(direct);
            }
#endif

            foreach (string file in Files)
            {
                try
                {
                    string overridePath = Path.Combine(Application.persistentDataPath, "Data", file + ".json");
                    if (File.Exists(overridePath)) texts[file] = File.ReadAllText(overridePath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Nore] Could not read data override for " + file + ": " + e.Message);
                }
            }

            var registry = new DataRegistry();
            registry.Load(texts);
            return registry;
        }

        /// <summary>Headless entry point (editor tests, tooling, deterministic fixtures).</summary>
        public void Load(Dictionary<string, string> texts)
        {
            LoadWarnings.Clear();
            Config = Parse<ConfigData>(texts, "config");
            Buildings = Parse<BuildingTable>(texts, "buildings");
            Races = Parse<RaceTable>(texts, "races");
            Units = Parse<UnitTable>(texts, "units");
            Heroes = Parse<HeroTable>(texts, "heroes");
            Skills = Parse<SkillTable>(texts, "skills");
            Gear = Parse<GearTable>(texts, "gear");
            Dragons = Parse<DragonTable>(texts, "dragons");
            World = Parse<WorldTable>(texts, "world");
            RebuildIndexes();
        }

        private T Parse<T>(Dictionary<string, string> texts, string key) where T : new()
        {
            string json;
            if (!texts.TryGetValue(key, out json))
            {
                LoadWarnings.Add("Missing data file: " + key + ".json (using defaults)");
                return new T();
            }
            try
            {
                T parsed = JsonUtility.FromJson<T>(json);
                return parsed == null ? new T() : parsed;
            }
            catch (Exception e)
            {
                LoadWarnings.Add("Malformed " + key + ".json: " + e.Message);
                return new T();
            }
        }

        private void RebuildIndexes()
        {
            Index(_buildings, Buildings.items, d => d.id);
            Index(_races, Races.items, d => d.id);
            Index(_units, Units.items, d => d.id);
            Index(_heroes, Heroes.items, d => d.id);
            Index(_skills, Skills.items, d => d.id);
            Index(_gear, Gear.items, d => d.id);
            Index(_dragons, Dragons.items, d => d.id);
            Index(_trees, Dragons.trees, d => d.id);
            Index(_conversion, Races.conversionChart, d => d.id);
            Index(_kingdoms, World.kingdoms, d => d.id);
            Index(_cities, World.cities, d => d.id);
            Index(_nodes, World.nodes, d => d.id);

            _roleCounters.Clear();
            _roleCounterBp.Clear();
            for (int i = 0; i < Config.roleCounters.Count; i++)
            {
                RoleCounter rc = Config.roleCounters[i];
                string key = rc.attacker + ">" + rc.defender;
                _roleCounters[key] = key;
                _roleCounterBp[key] = rc.bp;
            }
        }

        private static void Index<T>(Dictionary<string, T> map, List<T> list, Func<T, string> keyOf)
        {
            map.Clear();
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                string key = keyOf(list[i]);
                if (string.IsNullOrEmpty(key)) continue;
                if (map.ContainsKey(key)) continue;
                map.Add(key, list[i]);
            }
        }

        public BuildingDef Building(string id) => Lookup(_buildings, id);
        public RaceDef Race(string id) => Lookup(_races, id);
        public UnitDef Unit(string id) => Lookup(_units, id);
        public HeroDef Hero(string id) => Lookup(_heroes, id);
        public SkillDef Skill(string id) => Lookup(_skills, id);
        public GearDef GearItem(string id) => Lookup(_gear, id);
        public DragonDef Dragon(string id) => Lookup(_dragons, id);
        public TalentTreeDef Tree(string id) => Lookup(_trees, id);
        public ConversionGroup Conversion(string id) => Lookup(_conversion, id);
        public KingdomDef Kingdom(string id) => Lookup(_kingdoms, id);
        public CityDef City(string id) => Lookup(_cities, id);
        public MapNodeDef Node(string id) => Lookup(_nodes, id);

        private static T Lookup<T>(Dictionary<string, T> map, string id) where T : class
        {
            T value;
            return string.IsNullOrEmpty(id) ? null : (map.TryGetValue(id, out value) ? value : null);
        }

        public IEnumerable<BuildingDef> AllBuildings => Buildings.items;
        public IEnumerable<RaceDef> AllRaces => Races.items;
        public IEnumerable<UnitDef> AllUnits => Units.items;
        public IEnumerable<HeroDef> AllHeroes => Heroes.items;
        public IEnumerable<SkillDef> AllSkills => Skills.items;
        public IEnumerable<GearDef> AllGear => Gear.items;
        public IEnumerable<DragonDef> AllDragons => Dragons.items;
        public IEnumerable<ConversionGroup> AllConversionGroups => Races.conversionChart;

        public List<UnitDef> UnitsForRace(string raceId)
        {
            var result = new List<UnitDef>();
            for (int i = 0; i < Units.items.Count; i++)
                if (Units.items[i].race == raceId) result.Add(Units.items[i]);
            return result;
        }

        /// <summary>Role-vs-role counter multiplier in basis points.</summary>
        public int CounterBp(string attackerRole, string defenderRole)
        {
            int value;
            return _roleCounterBp.TryGetValue(attackerRole + ">" + defenderRole, out value) ? value : Fixed.Bp;
        }

        public int EffectTotal(string stat)
        {
            // Used by tooling/UI for display only; StrongholdState computes level-scaled totals.
            int total = 0;
            for (int i = 0; i < Buildings.items.Count; i++)
            {
                var effects = Buildings.items[i].effects;
                for (int j = 0; j < effects.Count; j++)
                    if (effects[j].stat == stat) total += effects[j].baseValue + effects[j].perLevel;
            }
            return total;
        }

        /// <summary>Checks every cross-file reference. Returns human-readable errors (empty = OK).</summary>
        public List<string> Validate()
        {
            var errors = new List<string>(LoadWarnings);
            if (Config.tickRate <= 0) errors.Add("config.tickRate must be > 0");
            if (Config.maxSquads <= 0) errors.Add("config.maxSquads must be > 0");

            foreach (var race in Races.items)
            {
                if (string.IsNullOrEmpty(race.id)) errors.Add("race with empty id");
                if (!string.IsNullOrEmpty(race.homeKingdom) && Kingdom(race.homeKingdom) == null)
                    errors.Add("race " + race.id + " -> unknown kingdom " + race.homeKingdom);
                foreach (string unitId in race.unitIds)
                    if (Unit(unitId) == null) errors.Add("race " + race.id + " -> unknown unit " + unitId);
                if (!string.IsNullOrEmpty(race.modifiers.summonUnitId) && Unit(race.modifiers.summonUnitId) == null)
                    errors.Add("race " + race.id + " -> unknown summon unit " + race.modifiers.summonUnitId);
            }

            foreach (var group in Races.conversionChart)
            {
                if (group.entries.Count != Races.items.Count)
                    errors.Add("conversion group " + group.id + " must cover all " + Races.items.Count + " races");
                foreach (var entry in group.entries)
                {
                    if (Race(entry.race) == null) errors.Add("conversion " + group.id + " -> unknown race " + entry.race);
                    UnitDef unit = Unit(entry.unitId);
                    if (unit == null) errors.Add("conversion " + group.id + " -> unknown unit " + entry.unitId);
                    else if (unit.race != entry.race)
                        errors.Add("conversion " + group.id + " maps " + entry.unitId + " to wrong race " + entry.race);
                }
            }

            foreach (var unit in Units.items)
            {
                if (Race(unit.race) == null) errors.Add("unit " + unit.id + " -> unknown race " + unit.race);
                if (unit.hp <= 0 || unit.attack <= 0) errors.Add("unit " + unit.id + " has non-positive hp/attack");
                if (unit.attackIntervalTicks <= 0) errors.Add("unit " + unit.id + " has non-positive attack interval");
                if (unit.damageType != "physical" && unit.damageType != "magic")
                    errors.Add("unit " + unit.id + " has unknown damageType " + unit.damageType);
            }

            foreach (var hero in Heroes.items)
            {
                if (Race(hero.race) == null) errors.Add("hero " + hero.id + " -> unknown race " + hero.race);
                foreach (string skillId in hero.skillIds)
                    if (Skill(skillId) == null) errors.Add("hero " + hero.id + " -> unknown skill " + skillId);
            }

            foreach (var skill in Skills.items)
            {
                if (!string.IsNullOrEmpty(skill.summonUnitId) && Unit(skill.summonUnitId) == null)
                    errors.Add("skill " + skill.id + " -> unknown summon unit " + skill.summonUnitId);
                if (!string.IsNullOrEmpty(skill.heroId) && Hero(skill.heroId) == null)
                    errors.Add("skill " + skill.id + " -> unknown hero " + skill.heroId);
            }

            foreach (var dragon in Dragons.items)
                if (Tree(dragon.primaryTree) == null)
                    errors.Add("dragon " + dragon.id + " -> unknown primary tree " + dragon.primaryTree);

            foreach (var city in World.cities)
                if (Kingdom(city.kingdom) == null) errors.Add("city " + city.id + " -> unknown kingdom " + city.kingdom);
            foreach (var node in World.nodes)
                if (!string.IsNullOrEmpty(node.kingdom) && Kingdom(node.kingdom) == null)
                    errors.Add("node " + node.id + " -> unknown kingdom " + node.kingdom);

            return errors;
        }

        public string Summary() =>
            Buildings.items.Count + " buildings, " + Races.items.Count + " races, " +
            Units.items.Count + " units, " + Heroes.items.Count + " heroes, " +
            Skills.items.Count + " skills, " + Gear.items.Count + " gear, " +
            Dragons.items.Count + " dragons, " + World.cities.Count + " cities, " +
            World.nodes.Count + " map nodes";
    }
}
