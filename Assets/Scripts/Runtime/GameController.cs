using System;
using System.Collections.Generic;
using Nore.Combat;
using Nore.Core;
using Nore.Data;
using Nore.Economy;
using Nore.Military;
using Nore.State;
using Nore.World;
using UnityEngine;

namespace Nore.Runtime
{
    /// <summary>
    /// Bootstraps itself at runtime (no scene authoring needed), owns the game state,
    /// drives the battle with a fixed accumulator and exposes UI commands.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class GameController : MonoBehaviour
    {
        public DataRegistry Data;
        public GameState State;
        public BattleSimulation Battle;
        public BattleSetup LastSetup;
        public BattleReport LastReport;
        public OfflineReport LastOffline;
        public List<SelfTests.Result> TestResults = new List<SelfTests.Result>();
        public List<string> DataErrors = new List<string>();

        public int tab;
        public int battleSpeed = 1;
        public bool paused;
        public bool autoCastEnemy = true;
        public string status = "Ready";
        public string selectedSkill = "";
        public int selectedHero;
        public bool targetingSkill;
        public string selectedEnemyRace = "dwarf";
        public string selectedCityId = "";
        public string selectedNodeId = "";
        public string selectedPvpMode = "arena";
        public string selectedEventId = "daily_chests";
        public string selectedTreeId = "protection";
        public Dictionary<string, int[]> deployment = new Dictionary<string, int[]>();

        private float _accumulator;
        private float _lastSecondTick;
        private bool _battleResolved;
        private int _lastAutoCastTick;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindFirstObjectByType<GameController>() != null) return;
            var go = new GameObject("Nore — Art of Conquest Prototype");
            go.AddComponent<GameController>();
            go.AddComponent<RuntimeUI>();
            Application.targetFrameRate = 60;
        }

        private void Awake()
        {
            Data = DataRegistry.LoadDefault();
            DataErrors = Data.Validate();
            if (DataErrors.Count > 0) Debug.LogWarning("[Nore] Data errors:\n" + string.Join("\n", DataErrors.ToArray()));

            SaveData save;
            string message;
            if (SaveService.Load(out save, out message))
            {
                State = GameState.FromSave(save, Data);
                LastOffline = State.ApplyOffline(GameTime.NowUnix());
                State.AddLog(LastOffline.Display());
            }
            else
            {
                State = GameState.New(Data, 20260606);
                State.AddLog(message + " — starting a new realm");
            }

            TestResults = SelfTests.RunAll(Data);
            SelfTests.LogToConsole(TestResults);
            State.AddLog(SelfTests.Format(TestResults).Split('\n')[0]);
            status = "Ready — " + Data.Summary();
        }

        private void Update()
        {
            long now = GameTime.NowUnix();
            if (Time.unscaledTime - _lastSecondTick >= 1f)
            {
                _lastSecondTick = Time.unscaledTime;
                State.Tick(now);
            }

            if (Battle == null || Battle.Finished || paused) return;
            float step = 1f / Math.Max(1, Data.Config.tickRate);
            _accumulator += Math.Min(0.25f, Time.unscaledDeltaTime) * Math.Max(1, battleSpeed);
            int guard = 0;
            while (_accumulator >= step && !Battle.Finished && guard++ < 60)
            {
                Battle.Step();
                _accumulator -= step;
                if (autoCastEnemy && Battle.Tick - _lastAutoCastTick >= 25)
                {
                    _lastAutoCastTick = Battle.Tick;
                    Battle.AutoCast((int)BattleTeam.Defender);
                }
            }
            if (Battle.Finished && !_battleResolved) ResolveBattle();
        }

        // ---------------- battle orchestration ----------------

        public int MaxDeployableSquads()
        {
            int slots = 2;
            for (int i = 0; i < State.Heroes.deployed.Count; i++)
            {
                HeroState h = State.Heroes.Get(State.Heroes.deployed[i]);
                if (h != null) slots += h.TroopSlots(Data);
            }
            return Math.Min(12, slots);
        }

        public void SetDeployment(string unitId, int x, int y)
        {
            deployment[unitId] = new[] { Fixed.Clamp(x, 0, Data.Config.grid.attackerMaxColumn),
                                        Fixed.Clamp(y, 0, Data.Config.grid.rows - 1) };
        }

        public int[] GetDeployment(string unitId)
        {
            int[] cell;
            if (deployment.TryGetValue(unitId, out cell)) return cell;
            int index = 0;
            for (int i = 0; i < State.Army.stacks.Count; i++)
                if (State.Army.stacks[i].unitId == unitId) { index = i; break; }
            return new[] { 1 + index % Data.Config.grid.attackerMaxColumn, (index * 2) % Data.Config.grid.rows };
        }

        public void StartSkirmish(string enemyRace, bool siege)
        {
            var setup = new BattleSetup
            {
                seed = State.Rng.NextInt(int.MaxValue - 1) + 1,
                label = siege ? "City siege vs " + enemyRace : "Skirmish vs " + enemyRace,
                siege = siege,
                defenderOnWalls = siege,
                wallHp = siege ? State.Stronghold.Effect("wallHp", Data) / 4 : 0,
                maxTicks = Data.Config.maxBattleTicks
            };

            int limit = MaxDeployableSquads();
            int used = 0;
            for (int i = 0; i < State.Army.stacks.Count && used < limit; i++)
            {
                UnitStack stack = State.Army.stacks[i];
                if (stack.count <= 0) continue;
                int[] cell = GetDeployment(stack.unitId);
                setup.squads.Add(BattleFactory.Squad(stack.unitId, stack.count, cell[0], cell[1], (int)BattleTeam.Attacker));
                used++;
            }
            List<HeroState> deployed = new List<HeroState>();
            for (int i = 0; i < State.Heroes.deployed.Count; i++)
            {
                HeroState h = State.Heroes.Get(State.Heroes.deployed[i]);
                if (h == null) continue;
                deployed.Add(h);
                string msg;
                State.Heroes.SpendEnergy(h.heroId, Data.Hero(h.heroId).energyCost, out msg);
                HeroDef def = Data.Hero(h.heroId);
                setup.heroes.Add(BattleFactory.Hero(h.heroId, (int)BattleTeam.Attacker, 0, i * 2 + 1,
                    h.Might(Data), h.Magic(Data), h.Stamina(Data), h.Command(Data), h.Hp(Data), def.skillIds));
            }
            DragonState dragon = State.Dragons.Active(Data);
            if (dragon != null)
                for (int i = 0; i < setup.heroes.Count; i++)
                    setup.heroes[i].command += dragon.Bonus("commandBonus", Data);

            foreach (SquadTemplate garrison in BattleFactory.GarrisonFor(enemyRace, Data.Config.grid.columns,
                                                                         Data.Config.grid.rows, Data, 100))
                setup.squads.Add(garrison);
            HeroDef enemyHero = PickEnemyHero(enemyRace);
            if (enemyHero != null)
                setup.heroes.Add(BattleFactory.Hero(enemyHero.id, (int)BattleTeam.Defender,
                    Data.Config.grid.columns - 1, 3, enemyHero.stats.might + 10, enemyHero.stats.magic + 10,
                    enemyHero.stats.stamina + 10, enemyHero.stats.command, 1400, enemyHero.skillIds));

            LastSetup = setup;
            Battle = new BattleSimulation(setup, Data);
            LastReport = null;
            _battleResolved = false;
            _accumulator = 0f;
            _lastAutoCastTick = 0;
            selectedHero = 0;
            selectedSkill = "";
            paused = false;
            tab = 5;
            status = setup.label + " started — cast hero skills in real time!";
            State.AddLog(status + " (seed " + setup.seed + ")");
        }

        private HeroDef PickEnemyHero(string raceId)
        {
            foreach (HeroDef hero in Data.AllHeroes)
                if (hero.race == raceId) return hero;
            return Data.Hero("rufio");
        }

        public void CastAtCell(string skillId, int cellX, int cellY)
        {
            if (Battle == null || Battle.Finished) return;
            int heroIndex = selectedHero;
            if (heroIndex < 0 || heroIndex >= Battle.HeroCount) heroIndex = 0;
            string message;
            bool ok = Battle.CastSkill(heroIndex, skillId,
                Fixed.CellToWorld(cellX), Fixed.CellToWorld(cellY), out message);
            status = message;
            State.AddLog(message);
        }

        public void ResolveBattle()
        {
            if (Battle == null || _battleResolved) return;
            _battleResolved = true;
            BattleReport report = Battle.BuildReport();
            LastReport = report;
            bool won = report.outcome == BattleOutcome.AttackerWin;
            string lossMessage;
            State.Army.ApplyLosses(report.outcomes, (int)BattleTeam.Attacker, GameTime.NowUnix(),
                                  Data, State.Stronghold, out lossMessage);
            if (won)
            {
                State.Stronghold.resources.gold += report.lootGold;
                State.Stronghold.resources.wood += report.lootWood;
                State.Stronghold.resources.mana += report.lootMana;
                State.Stronghold.resources.activity += 15;
                State.World.activity += 15;
            }
            var holder = new StrongholdHolder { HeroLevelCap = Math.Max(10, State.Stronghold.Effect("heroLevelCap", Data)) };
            for (int i = 0; i < State.Heroes.deployed.Count; i++)
            {
                HeroState h = State.Heroes.Get(State.Heroes.deployed[i]);
                if (h == null) continue;
                string xpMessage;
                h.AddXp(report.heroXp, Data, holder, out xpMessage);
            }
            if (LastSetup != null && LastSetup.siege && !string.IsNullOrEmpty(selectedCityId))
            {
                string siegeMessage;
                State.World.Siege(selectedCityId, won, Data, State.Stronghold, out siegeMessage);
                State.AddLog(siegeMessage);
            }
            status = report.summary + " | " + lossMessage;
            State.AddLog(status);
            State.AddLog("State hash " + report.stateHash + " (replay with the same seed to verify)");
            Save();
        }

        public void ClearBattle()
        {
            Battle = null;
            LastReport = null;
            tab = 1;
            status = "Returned to the stronghold";
        }

        // ---------------- world / economy commands ----------------

        public void Upgrade(string buildingId)
        {
            string message;
            bool ok = State.Stronghold.StartUpgrade(buildingId, GameTime.NowUnix(), Data, out message);
            status = message;
            State.AddLog((ok ? "Upgrade started: " : "Upgrade refused: ") + message);
        }

        public void SpeedUp(string buildingId)
        {
            string message;
            State.Stronghold.SpeedUp(buildingId, GameTime.NowUnix(), Data, out message);
            status = message;
            State.AddLog("Speed-up: " + message);
        }

        public void Train(string unitId, int count)
        {
            string message;
            State.Army.Train(unitId, count, GameTime.NowUnix(), Data, State.Stronghold, out message);
            status = message;
            State.AddLog("Training: " + message);
        }

        public void SwitchRace(string raceId)
        {
            string message;
            bool ok = State.Army.SwitchRace(raceId, GameTime.NowUnix(), Data, State.Stronghold, out message);
            status = message;
            State.AddLog((ok ? "Race switch: " : "Race switch refused: ") + message);
        }

        public void Explore(string nodeId)
        {
            string message;
            MapNodeDef encounter;
            ResourceSet reward;
            bool ok = State.World.Explore(nodeId, Data, State.Stronghold, State.Army, State.Rng,
                                         out message, out encounter, out reward);
            status = message;
            State.AddLog("Explore: " + message);
            selectedNodeId = nodeId;
            if (ok && encounter != null)
            {
                selectedEnemyRace = string.IsNullOrEmpty(encounter.enemyRace) ? "dwarf" : encounter.enemyRace;
                StartSkirmish(selectedEnemyRace, false);
            }
            else if (ok && Data.Node(nodeId).kind == "caravan")
            {
                LaunchCaravan(nodeId, reward);
            }
        }

        public void LaunchCaravan(string nodeId, ResourceSet cargo)
        {
            int escort = Math.Max(100, State.Army.ArmyPower(Data) / 20);
            CaravanState c = State.Caravans.Launch(nodeId, cargo, GameTime.NowUnix(), escort, 180, State.World.houseId);
            status = c.id + " dispatched with escort power " + escort;
            State.AddLog(status);
        }

        public void Intercept(string caravanId)
        {
            string message;
            ResourceSet stolen;
            bool ok = State.Caravans.Intercept(caravanId, State.Army.ArmyPower(Data), "Rival House",
                GameTime.NowUnix(), State.Rng, out message, out stolen);
            if (ok) State.Stronghold.resources.Add(stolen);
            status = message;
            State.AddLog("Caravan PvP: " + message);
        }

        public void SiegeCity(string cityId)
        {
            CityDef city = Data.City(cityId);
            if (city == null) { status = "Unknown city"; return; }
            selectedCityId = cityId;
            selectedEnemyRace = "dwarf"; // garrison race can be data-driven later
            status = "Marching on " + city.name + " (" + State.World.City(cityId).strongholdsRemaining + " strongholds left)";
            State.AddLog(status);
            StartSkirmish(selectedEnemyRace, true);
        }

        public void RunPvp(string modeId)
        {
            string message;
            State.World.RunPvp(modeId, State.Army.ArmyPower(Data), State.Rng, Data,
                               State.Stronghold, State.Dragons, out message);
            status = message;
            State.AddLog("PvP: " + message);
        }

        public void RunEvent(string eventId)
        {
            string message;
            State.World.RunEvent(eventId, Data, State.Stronghold, State.Dragons, State.Log, out message);
            status = message;
        }

        public void Save()
        {
            string message;
            SaveService.Save(State.ToSave(), out message);
            status = message;
            State.AddLog(message);
        }

        public void Load()
        {
            SaveData save;
            string message;
            if (!SaveService.Load(out save, out message)) { status = message; return; }
            State = GameState.FromSave(save, Data);
            LastOffline = State.ApplyOffline(GameTime.NowUnix());
            status = message + " | " + LastOffline.Display();
            State.AddLog(status);
        }

        public void NewGame()
        {
            State = GameState.New(Data, 20260606);
            deployment.Clear();
            Battle = null;
            LastReport = null;
            status = "New realm created";
            Save();
        }
    }
}
