using System;
using System.Collections.Generic;
using Nore.Core;
using Nore.Data;

namespace Nore.Combat
{
    /// <summary>
    /// Deterministic, fixed-tick real-time battle. One "squad" is an aggregated stack
    /// of identical troops (a hp pool + alive count), so 100k simulated soldiers are
    /// still only ~96 cache-friendly array slots. No floats, no UnityEngine.Random,
    /// no per-tick allocations except log strings.
    ///
    /// Inputs: BattleSetup + DataRegistry + a seed. Same inputs + same skill-cast
    /// schedule => same rolling state hash. See docs/COMBAT_SPEC.md.
    /// </summary>
    public sealed class BattleSimulation
    {
        public const int MaxSkillsPerHero = 6;
        private const int HealIntervalTicks = 50;
        private const int BucketSize = 2000;

        private readonly DataRegistry _data;
        private readonly ConfigData _cfg;
        private readonly BattleSetup _setup;
        private readonly DeterministicRng _rng;
        private readonly int _maxSquads;
        private readonly int _maxTicks;
        private readonly string[] _teamRace = { "human", "human" };

        // ---------- squad state (struct-of-arrays) ----------
        public int Count;
        public int[] Team, X, Y, Alive, MaxAlive, HpPerUnit, Morale, Shock, Routed, RoutedTicks;
        public int[] RoutedLost, Escaped, AttackTimer, Ammo, AmmoActive, StunTicks, Target;
        public int[] IsSummon, ExpireTick, BuffHasteUntil, BuffAttackUntil, BuffAttackBp;
        public int[] BuffIntimUntil, BuffIntimBp, BuffResistUntil, BuffResistBp;
        public long[] HpPool, MaxHpPool;
        public string[] UnitIds, RaceIds;
        private UnitDef[] _defs;
        private RaceModifiers[] _mods;

        // ---------- hero state ----------
        public int HeroCount;
        public int[] HTeam, HX, HY, HHp, HMaxHp, HMana, HMight, HMagic, HStamina, HCommand;
        public int[] HAlive, HMagicBuff, HSkillCount;
        public int[] HCooldown;
        public string[] HIds, HSkills;

        private readonly int[] _furyUntil = new int[2];
        private readonly int[] _furyStacks = new int[2];
        private readonly int[] _heroDeathShock = new int[2];

        private readonly List<string> _log = new List<string>();
        private readonly List<int> _query = new List<int>();
        private int[] _bucketHead = new int[256];
        private int[] _bucketNext = new int[128];
        private int _bucketColumns = 16;
        private int _bucketRows = 8;
        private int _bucketTick = -1;
        private BattleOutcome _outcome = BattleOutcome.Ongoing;
        private ulong _rollingHash = 14695981039346656037UL;
        private int _totalKilled;
        private int _routedSquads;

        public int Tick { get; private set; }
        public BattleOutcome Outcome => _outcome;
        public bool Finished => _outcome != BattleOutcome.Ongoing;
        public int MaxTicks => _maxTicks;
        public IReadOnlyList<string> Log => _log;
        public string RollingHash => Hashing.Hex(_rollingHash);
        public UnitDef[] Defs => _defs;

        public BattleSimulation(BattleSetup setup, DataRegistry data)
        {
            _setup = setup ?? new BattleSetup();
            _data = data;
            _cfg = data.Config;
            _rng = new DeterministicRng(_setup.seed);
            _maxSquads = Math.Max(16, _cfg.maxSquads);
            _maxTicks = _setup.maxTicks > 0 ? _setup.maxTicks : Math.Max(60, _cfg.maxBattleTicks);
            Allocate();
            SpawnSquads();
            SpawnHeroes();
            AddLog("Battle started: " + _setup.label + " (seed " + _setup.seed + ")");
            if (_setup.siege) AddLog("Siege: defenders on ramparts, wall integrity " + _setup.wallHp);
        }

        private void Allocate()
        {
            int n = _maxSquads;
            Team = new int[n]; X = new int[n]; Y = new int[n]; Alive = new int[n]; MaxAlive = new int[n];
            HpPerUnit = new int[n]; Morale = new int[n]; Shock = new int[n]; Routed = new int[n];
            RoutedTicks = new int[n]; RoutedLost = new int[n]; Escaped = new int[n];
            AttackTimer = new int[n]; Ammo = new int[n]; AmmoActive = new int[n]; StunTicks = new int[n];
            Target = new int[n]; IsSummon = new int[n]; ExpireTick = new int[n];
            BuffHasteUntil = new int[n]; BuffAttackUntil = new int[n]; BuffAttackBp = new int[n];
            BuffIntimUntil = new int[n]; BuffIntimBp = new int[n];
            BuffResistUntil = new int[n]; BuffResistBp = new int[n];
            HpPool = new long[n]; MaxHpPool = new long[n];
            UnitIds = new string[n]; RaceIds = new string[n];
            _defs = new UnitDef[n]; _mods = new RaceModifiers[n];
            for (int i = 0; i < n; i++) Target[i] = -1;

            int h = Math.Max(1, _setup.heroes.Count);
            HTeam = new int[h]; HX = new int[h]; HY = new int[h]; HHp = new int[h]; HMaxHp = new int[h];
            HMana = new int[h]; HMight = new int[h]; HMagic = new int[h]; HStamina = new int[h];
            HCommand = new int[h]; HAlive = new int[h]; HMagicBuff = new int[h]; HSkillCount = new int[h];
            HIds = new string[h]; HSkills = new string[h * MaxSkillsPerHero]; HCooldown = new int[h * MaxSkillsPerHero];
        }

        private void SpawnSquads()
        {
            int teamSeen = 0;
            for (int s = 0; s < _setup.squads.Count && Count < _maxSquads; s++)
            {
                SquadTemplate t = _setup.squads[s];
                UnitDef def = _data.Unit(t.unitId);
                if (def == null || t.count <= 0) continue;
                int i = Count++;
                UnitIds[i] = def.id;
                RaceIds[i] = def.race;
                _defs[i] = def;
                RaceDef race = _data.Race(def.race);
                _mods[i] = race == null ? new RaceModifiers() : race.modifiers;
                Team[i] = Fixed.Clamp(t.team, 0, 1);
                X[i] = Fixed.CellToWorld(Fixed.Clamp(t.cellX, 0, _cfg.grid.columns - 1));
                Y[i] = Fixed.CellToWorld(Fixed.Clamp(t.cellY, 0, _cfg.grid.rows - 1));
                int wallBonus = (_setup.siege && _setup.defenderOnWalls && t.team == (int)BattleTeam.Defender)
                    ? _setup.wallHp : 0;
                HpPerUnit[i] = Math.Max(1, def.hp + wallBonus);
                MaxAlive[i] = t.count;
                Alive[i] = t.count;
                HpPool[i] = (long)HpPerUnit[i] * t.count;
                MaxHpPool[i] = HpPool[i];
                Morale[i] = _cfg.morale.baseValue;
                Ammo[i] = def.ammo + _mods[i].ammoReserve;
                AmmoActive[i] = _mods[i].ammoReserve > 0 || def.ammo > 0 ? 1 : 0;
                if ((teamSeen & (1 << Team[i])) == 0)
                {
                    _teamRace[Team[i]] = def.race;
                    teamSeen |= 1 << Team[i];
                }
            }
        }

        private void SpawnHeroes()
        {
            HeroCount = 0;
            for (int s = 0; s < _setup.heroes.Count && HeroCount < HTeam.Length; s++)
            {
                HeroSpec spec = _setup.heroes[s];
                int i = HeroCount++;
                HIds[i] = spec.heroId;
                HTeam[i] = Fixed.Clamp(spec.team, 0, 1);
                HX[i] = Fixed.CellToWorld(Fixed.Clamp(spec.cellX, 0, _cfg.grid.columns - 1));
                HY[i] = Fixed.CellToWorld(Fixed.Clamp(spec.cellY, 0, _cfg.grid.rows - 1));
                HMaxHp[i] = Math.Max(1, spec.hp); HHp[i] = HMaxHp[i];
                HMana[i] = 100;
                HMight[i] = spec.might; HMagic[i] = spec.magic;
                HStamina[i] = spec.stamina; HCommand[i] = spec.command;
                HAlive[i] = 1;
                int slots = spec.skillIds == null ? 0 : Math.Min(MaxSkillsPerHero, spec.skillIds.Count);
                HSkillCount[i] = slots;
                for (int k = 0; k < slots; k++) HSkills[i * MaxSkillsPerHero + k] = spec.skillIds[k];
            }
        }

        // ============================ main tick ============================

        public void Step()
        {
            if (Finished) return;
            Tick++;

            BuildBuckets();
            UpdateTimers();
            UpdateRaceMechanics();
            if (Tick % Math.Max(1, _cfg.morale.updateIntervalTicks) == 0) UpdateMorale();

            for (int i = 0; i < Count; i++)
            {
                if (Alive[i] <= 0 || Escaped[i] != 0 || StunTicks[i] > 0) continue;
                if (Routed[i] != 0) { Flee(i); continue; }
                AcquireTarget(i);
                int t = Target[i];
                if (t < 0) continue;
                int range = _defs[i].range;
                if (!Fixed.Within(X[i], Y[i], X[t], Y[t], range))
                {
                    Fixed.MoveToward(ref X[i], ref Y[i], X[t], Y[t], MoveStep(i));
                }
                else if (AttackTimer[i] <= 0)
                {
                    Attack(i, t);
                }
            }

            Cleanup();
            ApplyHeroCollapse();
            CheckEnd();
            _rollingHash ^= Hashing.Fnv1a(Tick, Count, SumHp(), SumMorale(), _totalKilled, _routedSquads);
        }

        private void UpdateTimers()
        {
            for (int i = 0; i < Count; i++)
            {
                if (Alive[i] <= 0) continue;
                if (AttackTimer[i] > 0) AttackTimer[i]--;
                if (StunTicks[i] > 0) StunTicks[i]--;
                Shock[i] = Math.Max(0, Shock[i] - _cfg.morale.shockDecayPerTick);
            }
            for (int h = 0; h < HeroCount; h++)
            {
                if (HAlive[h] == 0) continue;
                HMana[h] = Math.Min(100, HMana[h] + 2);
                for (int k = 0; k < HSkillCount[h]; k++)
                {
                    int idx = h * MaxSkillsPerHero + k;
                    if (HCooldown[idx] > 0) HCooldown[idx]--;
                }
            }
        }

        private void UpdateRaceMechanics()
        {
            // Lich free-summon loop: each living Mummy-class squad raises fresh troops.
            if (Tick % 20 == 0)
            {
                for (int team = 0; team < 2; team++)
                {
                    RaceModifiers mods = null;
                    string summonId = "";
                    int summonCount = 0;
                    int interval = 0;
                    int anchor = -1;
                    for (int i = 0; i < Count; i++)
                    {
                        if (Team[i] != team || Alive[i] <= 0 || Routed[i] != 0 || Escaped[i] != 0) continue;
                        if (_mods[i].freeSummonCount <= 0) continue;
                        mods = _mods[i];
                        summonId = mods.summonUnitId;
                        summonCount = mods.freeSummonCount;
                        interval = Math.Max(20, mods.summonIntervalTicks);
                        if (_defs[i].role == "clergy") { anchor = i; break; }
                        if (anchor < 0) anchor = i;
                    }
                    if (mods == null || anchor < 0 || Tick % interval != 0) continue;
                    SpawnSummon(summonId, summonCount, team, X[anchor], Y[anchor], 0);
                    AddLog("Lich free summon: +" + summonCount + " " + summonId + " (team " + team + ")");
                }
            }

            // Clergy / Serpent healing every 5 seconds, up to 7 squads per healer.
            if (Tick % HealIntervalTicks == 0)
            {
                for (int i = 0; i < Count; i++)
                {
                    if (Alive[i] <= 0 || Routed[i] != 0 || Escaped[i] != 0) continue;
                    if (_defs[i].role != "clergy") continue;
                    int healBp = _mods[i].clergyHealBp + _mods[i].verdantHealBp;
                    if (healBp <= 0) continue;
                    Query(X[i], Y[i], 4000);
                    int healed = 0;
                    for (int q = 0; q < _query.Count && healed < 7; q++)
                    {
                        int j = _query[q];
                        if (Team[j] != Team[i] || Alive[j] <= 0 || HpPool[j] >= MaxHpPool[j]) continue;
                        long amount = Fixed.BpMul(MaxHpPool[j], healBp);
                        HpPool[j] = Math.Min(MaxHpPool[j], HpPool[j] + amount);
                        Alive[j] = (int)((HpPool[j] + HpPerUnit[j] - 1) / HpPerUnit[j]);
                        healed++;
                    }
                }
            }
        }

        private void UpdateMorale()
        {
            for (int i = 0; i < Count; i++)
            {
                if (Alive[i] <= 0 || Escaped[i] != 0 || IsMoraleImmune(i)) continue;
                long lossBp = MaxHpPool[i] <= 0 ? 0 :
                    (MaxHpPool[i] - HpPool[i]) * Fixed.Bp / MaxHpPool[i];
                int casualty = (int)(lossBp / 100) * _cfg.morale.casualtyShockPerPercent;
                int aura = CommandAura(i);
                int raw = _cfg.morale.baseValue + aura - Fixed.BpMul(casualty, _mods[i].moraleShockBp)
                          - Shock[i] - _heroDeathShock[Team[i]];
                Morale[i] = Fixed.Clamp(raw, 0, 1000);

                if (Routed[i] == 0 && Morale[i] < _cfg.morale.routThreshold)
                {
                    Routed[i] = 1;
                    RoutedTicks[i] = 0;
                    _routedSquads++;
                    AddLog("T" + Tick + " " + _defs[i].name + " (team " + Team[i] + ") ROUTED at morale " + Morale[i]);
                    for (int j = 0; j < Count; j++)
                        if (j != i && Team[j] == Team[i] && Alive[j] > 0 && !IsMoraleImmune(j))
                            Shock[j] += _cfg.morale.allyRoutShock;
                }
                else if (Routed[i] != 0)
                {
                    RoutedTicks[i] += Math.Max(1, _cfg.morale.updateIntervalTicks);
                    if (Morale[i] >= _cfg.morale.rallyThreshold && RoutedTicks[i] >= _cfg.morale.rallyTicks)
                    {
                        Routed[i] = 0;
                        RoutedTicks[i] = 0;
                        Morale[i] = Math.Max(Morale[i], _cfg.morale.rallyThreshold);
                        AddLog("T" + Tick + " " + _defs[i].name + " rallied (Command " + aura / Math.Max(1, _cfg.morale.commandPerPoint) + ")");
                    }
                }
            }
        }

        private int CommandAura(int squad)
        {
            int best = 0;
            for (int h = 0; h < HeroCount; h++)
            {
                if (HAlive[h] == 0 || HTeam[h] != Team[squad]) continue;
                int command = HCommand[h] * _cfg.morale.commandPerPoint;
                int aura = Fixed.Within(X[squad], Y[squad], HX[h], HY[h], _cfg.combat.commandAuraRange)
                    ? command : Fixed.BpMul(command, _cfg.combat.commandAuraFalloffBp);
                if (aura > best) best = aura;
            }
            int fortBonus = _setup.siege && Team[squad] == (int)BattleTeam.Defender ? 40 : 0;
            return best + fortBonus;
        }

        private bool IsMoraleImmune(int i) =>
            _defs[i].HasTag("siege") || _defs[i].HasTag("mechanical") || IsSummon[i] != 0;

        private void AcquireTarget(int i)
        {
            int best = -1;
            long bestDist = long.MaxValue;
            for (int j = 0; j < Count; j++)
            {
                if (Team[j] == Team[i] || Alive[j] <= 0 || Escaped[j] != 0 || Routed[j] != 0) continue;
                long d = Fixed.Dist2(X[i], Y[i], X[j], Y[j]);
                // Prefer counter targets slightly: a 10% virtual distance discount.
                int counter = _data.CounterBp(_defs[i].role, _defs[j].role);
                if (counter > Fixed.Bp) d = Fixed.BpDiv(d, counter);
                if (d < bestDist) { bestDist = d; best = j; }
            }
            Target[i] = best;
        }

        private int MoveStep(int i)
        {
            int step = Fixed.BpMul(_defs[i].moveSpeed, _mods[i].speedBp);
            if (Tick < FuryUntil(i)) step = Fixed.BpMul(step, FuryBp(i));
            if (Tick < BuffHasteUntil[i]) step = Fixed.BpMul(step, 11000);
            return Math.Max(1, step);
        }

        private void Attack(int i, int t)
        {
            UnitDef attacker = _defs[i];
            UnitDef defender = _defs[t];
            bool ammoBurst = AmmoActive[i] != 0 && Ammo[i] > 0;
            bool ammoDry = AmmoActive[i] != 0 && Ammo[i] <= 0;

            int hasteBp = Fixed.BpMul(_mods[i].hasteBp, FuryBp(i));
            if (Tick < BuffHasteUntil[i]) hasteBp = Fixed.BpMul(hasteBp, 17000);
            if (ammoBurst) hasteBp = Fixed.BpMul(hasteBp, 20000);
            else if (ammoDry) hasteBp = Fixed.BpMul(hasteBp, 6200);
            AttackTimer[i] = Math.Max(1, (int)((long)attacker.attackIntervalTicks * Fixed.Bp / Math.Max(2000, hasteBp)));
            if (AmmoActive[i] != 0 && Ammo[i] > 0) Ammo[i]--;

            long damage = (long)attacker.attack * Alive[i];
            damage = Fixed.BpMul(damage, _mods[i].damageDealtBp);
            damage = Fixed.BpMul(damage, _data.CounterBp(attacker.role, defender.role));
            damage = Fixed.BpMul(damage, _cfg.combat.moraleDamageFloorBp + Morale[i] * 5);
            damage = Fixed.BpMul(damage, 10000 + BuffAttackBpValue(i));
            damage = Fixed.BpMul(damage, 10000 - BuffIntimBpValue(i));
            damage = Fixed.BpMul(damage, ammoBurst ? _mods[i].ammoBurstBp : (ammoDry ? _mods[i].ammoDryBp : Fixed.Bp));
            damage = Fixed.BpMul(damage, RampartBp(i, t));
            if (_rng.ChanceBp(_cfg.combat.criticalChanceBp))
                damage = Fixed.BpMul(damage, _cfg.combat.criticalBonusBp);

            ApplyDamage(t, damage, attacker.damageType == "magic", i);
            SpillToHeroes(i, damage / 20);
        }

        private int RampartBp(int i, int t)
        {
            if (!_setup.siege || !_setup.defenderOnWalls) return Fixed.Bp;
            if (Team[i] == (int)BattleTeam.Attacker && _defs[t].role == "line") return _mods[i].rampartBonusBp;
            return Fixed.Bp;
        }

        private void ApplyDamage(int t, long damage, bool magic, int source)
        {
            if (damage <= 0 || Alive[t] <= 0) return;
            int mitigation = _defs[t].defense * 100;
            if (_setup.siege && _setup.defenderOnWalls && Team[t] == (int)BattleTeam.Defender) mitigation += 1500;
            mitigation = Math.Min(mitigation, _cfg.combat.defenseSoftCapBp);
            damage = damage * Fixed.Bp / (Fixed.Bp + mitigation);
            damage = Fixed.BpMul(damage, BuffResistBpValue(t));
            damage = Fixed.BpMul(damage, _mods[t].damageTakenBp);
            if (magic)
            {
                int resist = _defs[t].magicResistBp;
                if (_defs[t].HasTag("magical") || _defs[t].HasTag("undead"))
                    resist = Math.Max(resist, _mods[t].magicResistBp);
                resist = Math.Min(resist, _cfg.combat.magicResistCapBp);
                damage = Fixed.BpMul(damage, Fixed.Bp - resist);
            }
            if (damage <= 0) return;
            long pool = HpPool[t] - damage;
            if (pool <= 0)
            {
                HpPool[t] = 0;
                Alive[t] = 0;
                _totalKilled += MaxAlive[t] - RoutedLost[t];
                AddLog("T" + Tick + " " + _defs[t].name + " (team " + Team[t] + ") destroyed by " + _defs[source].name);
            }
            else
            {
                HpPool[t] = pool;
                Alive[t] = (int)((pool + HpPerUnit[t] - 1) / HpPerUnit[t]);
            }
        }

        private void SpillToHeroes(int source, long damage)
        {
            if (damage <= 0) return;
            for (int h = 0; h < HeroCount; h++)
            {
                if (HAlive[h] == 0 || HTeam[h] == Team[source]) continue;
                if (!Fixed.Within(X[source], Y[source], HX[h], HY[h], 1800)) continue;
                HHp[h] -= (int)Math.Min(HHp[h], damage / 8);
                if (HHp[h] <= 0)
                {
                    HHp[h] = 0;
                    HAlive[h] = 0;
                    _heroDeathShock[HTeam[h]] += _cfg.morale.heroDeathShock;
                    AddLog("T" + Tick + " Hero " + HIds[h] + " fell! Team morale shock +" + _cfg.morale.heroDeathShock);
                }
                break;
            }
        }

        private void Flee(int i)
        {
            RoutedTicks[i]++;
            int edge = Team[i] == (int)BattleTeam.Attacker ? -Fixed.Q : (_cfg.grid.columns + 1) * Fixed.Q;
            Fixed.MoveToward(ref X[i], ref Y[i], edge, Y[i], Math.Max(20, _defs[i].moveSpeed));
            if (RoutedTicks[i] >= _cfg.combat.routedEscapeTicks)
            {
                RoutedLost[i] = Alive[i];
                Escaped[i] = 1;
                HpPool[i] = 0;
                Alive[i] = 0;
            }
        }

        private void Cleanup()
        {
            for (int i = 0; i < Count; i++)
            {
                if (ExpireTick[i] > 0 && Tick >= ExpireTick[i] && Alive[i] > 0)
                {
                    Alive[i] = 0;
                    HpPool[i] = 0;
                    AddLog("T" + Tick + " summoned squad " + _defs[i].name + " expired");
                }
            }
        }

        private void ApplyHeroCollapse()
        {
            for (int h = 0; h < HeroCount; h++)
            {
                if (HAlive[h] == 0) continue;
                bool anySquad = false;
                for (int i = 0; i < Count; i++)
                    if (Team[i] == HTeam[h] && Alive[i] > 0 && Escaped[i] == 0) { anySquad = true; break; }
                if (anySquad) continue;
                HHp[h] -= 60;
                if (HHp[h] <= 0)
                {
                    HHp[h] = 0; HAlive[h] = 0;
                    _heroDeathShock[HTeam[h]] += _cfg.morale.heroDeathShock;
                }
            }
        }

        private void CheckEnd()
        {
            int attacker = 0, defender = 0;
            long attackerPower = 0, defenderPower = 0;
            for (int i = 0; i < Count; i++)
            {
                bool active = Alive[i] > 0 && Escaped[i] == 0 && Routed[i] == 0;
                if (active)
                {
                    if (Team[i] == 0) attacker++; else defender++;
                }
                if (Alive[i] > 0 && Escaped[i] == 0)
                {
                    if (Team[i] == 0) attackerPower += HpPool[i]; else defenderPower += HpPool[i];
                }
            }
            if (attacker == 0 && defender == 0) Finish(BattleOutcome.Draw, -1);
            else if (attacker == 0) Finish(BattleOutcome.DefenderWin, 1);
            else if (defender == 0) Finish(BattleOutcome.AttackerWin, 0);
            else if (Tick >= _maxTicks)
                Finish(attackerPower == defenderPower ? BattleOutcome.Draw :
                       (attackerPower > defenderPower ? BattleOutcome.AttackerWin : BattleOutcome.DefenderWin),
                       attackerPower > defenderPower ? 0 : 1);
        }

        private void Finish(BattleOutcome outcome, int winner)
        {
            _outcome = outcome;
            AddLog("Battle finished at T" + Tick + ": " + outcome + " (hash " + RollingHash + ")");
        }

        // ============================ skills ============================

        public int SkillSlot(int heroIndex, string skillId)
        {
            if (heroIndex < 0 || heroIndex >= HeroCount) return -1;
            for (int k = 0; k < HSkillCount[heroIndex]; k++)
                if (HSkills[heroIndex * MaxSkillsPerHero + k] == skillId) return heroIndex * MaxSkillsPerHero + k;
            return -1;
        }

        public bool CanCast(int heroIndex, string skillId, out string reason)
        {
            reason = "";
            if (Finished) { reason = "Battle over"; return false; }
            int slot = SkillSlot(heroIndex, skillId);
            if (slot < 0) { reason = "Hero does not know that skill"; return false; }
            if (HAlive[heroIndex] == 0) { reason = "Hero is down"; return false; }
            SkillDef skill = _data.Skill(skillId);
            if (skill == null) { reason = "Unknown skill"; return false; }
            if (HCooldown[slot] > 0) { reason = "Cooldown " + HCooldown[slot] + "t"; return false; }
            if (HMana[heroIndex] < skill.manaCost) { reason = "Needs " + skill.manaCost + " Mana"; return false; }
            return true;
        }

        /// <summary>Manual real-time cast. worldX/worldY are fixed-point grid coordinates.</summary>
        public bool CastSkill(int heroIndex, string skillId, int worldX, int worldY, out string message)
        {
            string reason;
            if (!CanCast(heroIndex, skillId, out reason)) { message = reason; return false; }
            SkillDef skill = _data.Skill(skillId);
            int slot = SkillSlot(heroIndex, skillId);
            HMana[heroIndex] -= skill.manaCost;
            HCooldown[slot] = Math.Max(1, skill.cooldownTicks);
            int team = HTeam[heroIndex];
            BuildBuckets(); // positions may have changed since the last simulation tick

            switch (ParseKind(skill.kind))
            {
                case SkillKind.Damage:
                case SkillKind.Stun:
                    ApplyOffensiveSkill(heroIndex, skill, worldX, worldY);
                    break;
                case SkillKind.Heal:
                    ApplyHealSkill(heroIndex, skill, worldX, worldY);
                    break;
                case SkillKind.Buff:
                    ApplyBuffSkill(heroIndex, skill, team);
                    break;
                case SkillKind.Summon:
                    SpawnSummon(skill.summonUnitId, skill.summonCount, team, worldX, worldY, skill.durationTicks);
                    break;
            }

            if (_teamRace[team] == "rakan")
            {
                _furyStacks[team] = Math.Min(3, _furyStacks[team] + 1);
                _furyUntil[team] = Tick + 60;
                AddLog("Rakan Fury x" + _furyStacks[team] + " triggered by manual cast");
            }
            message = "T" + Tick + " " + HIds[heroIndex] + " cast " + skill.name;
            AddLog(message);
            return true;
        }

        private void ApplyOffensiveSkill(int heroIndex, SkillDef skill, int worldX, int worldY)
        {
            long baseDamage = skill.power
                + Fixed.BpMul(HMight[heroIndex], skill.mightScaleBp)
                + Fixed.BpMul(HMagic[heroIndex], skill.magicScaleBp);
            baseDamage = Fixed.BpMul(baseDamage, Fixed.Bp + HMagicBuff[heroIndex]);
            bool magic = skill.damageType == "magic";
            int radius = skill.target == "enemies" ? 20000 : Math.Max(1, skill.radius);
            Query(worldX, worldY, radius);
            int hits = 0;
            for (int q = 0; q < _query.Count; q++)
            {
                int j = _query[q];
                if (HTeam[heroIndex] == Team[j] || Alive[j] <= 0 || Escaped[j] != 0) continue;
                ApplyDamage(j, baseDamage, magic, j);
                if (ParseKind(skill.kind) == SkillKind.Stun) StunTicks[j] = Math.Max(StunTicks[j], skill.durationTicks);
                hits++;
            }
            AddLog("  hit " + hits + " squads for " + baseDamage + " " + skill.damageType + " each");
        }

        private void ApplyHealSkill(int heroIndex, SkillDef skill, int worldX, int worldY)
        {
            long amount = skill.power + Fixed.BpMul(HMagic[heroIndex], skill.magicScaleBp);
            Query(worldX, worldY, Math.Max(1, skill.radius));
            int healed = 0;
            for (int q = 0; q < _query.Count && healed < 7; q++)
            {
                int j = _query[q];
                if (Team[j] != HTeam[heroIndex] || Alive[j] <= 0 || Escaped[j] != 0) continue;
                HpPool[j] = Math.Min(MaxHpPool[j], HpPool[j] + amount * 10);
                Alive[j] = (int)((HpPool[j] + HpPerUnit[j] - 1) / HpPerUnit[j]);
                healed++;
            }
            AddLog("  healed " + healed + " squads");
        }

        private void ApplyBuffSkill(int heroIndex, SkillDef skill, int team)
        {
            int duration = Math.Max(1, skill.durationTicks);
            if (skill.buffStat == "magicBp")
            {
                HMagicBuff[heroIndex] += skill.magnitudeBp;
                AddLog("  spell power +" + skill.magnitudeBp + "bp for " + duration + " ticks");
                return;
            }
            int affected = 0;
            for (int i = 0; i < Count; i++)
            {
                if (Alive[i] <= 0 || Escaped[i] != 0) continue;
                bool isAlly = Team[i] == team;
                if (skill.target == "enemies" && isAlly) continue;
                if (skill.target != "enemies" && !isAlly) continue;
                if (skill.target == "self" && !Fixed.Within(HX[heroIndex], HY[heroIndex], X[i], Y[i], 3000)) continue;
                switch (skill.buffStat)
                {
                    case "hasteBp": BuffHasteUntil[i] = Tick + duration; break;
                    case "attackBp":
                        BuffAttackUntil[i] = Tick + duration;
                        BuffAttackBp[i] = skill.magnitudeBp;
                        break;
                    case "hpBp":
                    {
                        long extra = Fixed.BpMul(MaxHpPool[i], skill.magnitudeBp);
                        MaxHpPool[i] += extra;
                        HpPool[i] += extra;
                        break;
                    }
                    case "intimidateBp":
                        BuffIntimUntil[i] = Tick + duration;
                        BuffIntimBp[i] = skill.magnitudeBp;
                        break;
                    case "resistBp":
                        BuffResistUntil[i] = Tick + duration;
                        BuffResistBp[i] = Fixed.Bp - skill.magnitudeBp;
                        break;
                    case "ammo":
                        if (AmmoActive[i] != 0) Ammo[i] += skill.summonCount;
                        break;
                    case "magicBp":
                        HMagicBuff[heroIndex] += skill.magnitudeBp;
                        break;
                    case "rally":
                        if (Routed[i] != 0)
                        {
                            Routed[i] = 0;
                            RoutedTicks[i] = 0;
                            Morale[i] = Math.Max(Morale[i], _cfg.morale.rallyThreshold);
                            Shock[i] = 0;
                        }
                        break;
                }
                affected++;
            }
            AddLog("  buffed " + affected + " squads (" + skill.buffStat + ")");
        }

        /// <summary>Simple deterministic AI: casts the first ready skill at the enemy centroid.</summary>
        public void AutoCast(int team)
        {
            if (Finished) return;
            int cx = 0, cy = 0, n = 0;
            for (int i = 0; i < Count; i++)
            {
                if (Team[i] == team || Alive[i] <= 0) continue;
                cx += X[i]; cy += Y[i]; n++;
            }
            if (n == 0) return;
            cx /= n; cy /= n;
            for (int h = 0; h < HeroCount; h++)
            {
                if (HTeam[h] != team || HAlive[h] == 0) continue;
                for (int k = 0; k < HSkillCount[h]; k++)
                {
                    string id = HSkills[h * MaxSkillsPerHero + k];
                    SkillDef skill = _data.Skill(id);
                    if (skill == null || skill.kind == "buff" && skill.target == "self") continue;
                    string reason;
                    if (!CanCast(h, id, out reason)) continue;
                    string msg;
                    CastSkill(h, id, cx, cy, out msg);
                    return;
                }
            }
        }

        // ============================ helpers ============================

        private void SpawnSummon(string unitId, int count, int team, int worldX, int worldY, int durationTicks)
        {
            if (Count >= _maxSquads || count <= 0) return;
            UnitDef def = _data.Unit(unitId);
            if (def == null) return;
            int i = Count++;
            UnitIds[i] = def.id; RaceIds[i] = def.race; _defs[i] = def;
            RaceDef race = _data.Race(def.race);
            _mods[i] = race == null ? new RaceModifiers() : race.modifiers;
            Team[i] = team; X[i] = worldX; Y[i] = worldY;
            HpPerUnit[i] = Math.Max(1, def.hp);
            MaxAlive[i] = count; Alive[i] = count;
            HpPool[i] = (long)HpPerUnit[i] * count; MaxHpPool[i] = HpPool[i];
            Morale[i] = _cfg.morale.baseValue;
            Ammo[i] = def.ammo + _mods[i].ammoReserve;
            AmmoActive[i] = _mods[i].ammoReserve > 0 || def.ammo > 0 ? 1 : 0;
            IsSummon[i] = 1;
            ExpireTick[i] = durationTicks > 0 ? Tick + durationTicks : 0;
        }

        private void BuildBuckets()
        {
            _bucketColumns = Math.Max(1, (_cfg.grid.columns * Fixed.Q) / BucketSize + 2);
            _bucketRows = Math.Max(1, (_cfg.grid.rows * Fixed.Q) / BucketSize + 2);
            int needed = _bucketColumns * _bucketRows + 4;
            if (_bucketHead.Length < needed) Array.Resize(ref _bucketHead, needed);
            if (_bucketNext.Length < _maxSquads) Array.Resize(ref _bucketNext, _maxSquads);
            Array.Clear(_bucketHead, 0, needed);
            for (int i = 0; i < Count; i++)
            {
                if (Alive[i] <= 0 || Escaped[i] != 0) continue;
                int key = BucketKey(X[i], Y[i]);
                _bucketNext[i] = _bucketHead[key] - 1;
                _bucketHead[key] = i + 1;
            }
            _bucketTick = Tick;
        }

        private int BucketKey(int x, int y)
        {
            int cx = Fixed.Clamp(x / BucketSize, 0, _bucketColumns - 1);
            int cy = Fixed.Clamp(y / BucketSize, 0, _bucketRows - 1);
            return cy * _bucketColumns + cx + 1;
        }

        /// <summary>
        /// Spatial-hash radius query. Reuses one buffer (zero allocations) and only
        /// visits the buckets overlapping the circle, instead of scanning all squads.
        /// </summary>
        private void Query(int cx, int cy, int radius)
        {
            _query.Clear();
            if (_bucketTick != Tick) BuildBuckets();
            int minCx = Fixed.Clamp((cx - radius) / BucketSize, 0, _bucketColumns - 1);
            int maxCx = Fixed.Clamp((cx + radius) / BucketSize, 0, _bucketColumns - 1);
            int minCy = Fixed.Clamp((cy - radius) / BucketSize, 0, _bucketRows - 1);
            int maxCy = Fixed.Clamp((cy + radius) / BucketSize, 0, _bucketRows - 1);
            long r2 = (long)radius * radius;
            for (int by = minCy; by <= maxCy; by++)
            {
                for (int bx = minCx; bx <= maxCx; bx++)
                {
                    int key = by * _bucketColumns + bx + 1;
                    if (key >= _bucketHead.Length) continue;
                    for (int node = _bucketHead[key] - 1; node >= 0; node = _bucketNext[node] - 1)
                    {
                        if (node >= Count || Alive[node] <= 0 || Escaped[node] != 0) continue;
                        if (Fixed.Dist2(cx, cy, X[node], Y[node]) <= r2) _query.Add(node);
                    }
                }
            }
        }

        public List<int> QueryPublic(int cx, int cy, int radius)
        {
            Query(cx, cy, radius);
            return _query;
        }

        private static SkillKind ParseKind(string kind)
        {
            switch (kind)
            {
                case "heal": return SkillKind.Heal;
                case "buff": return SkillKind.Buff;
                case "summon": return SkillKind.Summon;
                case "stun": return SkillKind.Stun;
                default: return SkillKind.Damage;
            }
        }

        private int FuryUntil(int squad) => _furyUntil[Team[squad]];
        private int FuryBp(int squad)
        {
            if (_teamRace[Team[squad]] != "rakan" || Tick >= _furyUntil[Team[squad]]) return Fixed.Bp;
            int perStack = _mods[squad].activeBuffBp - Fixed.Bp;
            return Fixed.Bp + perStack * _furyStacks[Team[squad]];
        }
        private int BuffAttackBpValue(int i) => Tick < BuffAttackUntil[i] ? BuffAttackBp[i] : 0;
        private int BuffIntimBpValue(int i) => Tick < BuffIntimUntil[i] ? BuffIntimBp[i] : 0;
        private int BuffResistBpValue(int i) => Tick < BuffResistUntil[i] ? Math.Max(1000, BuffResistBp[i]) : Fixed.Bp;

        private long SumHp()
        {
            long sum = 0;
            for (int i = 0; i < Count; i++) sum += HpPool[i];
            return sum;
        }

        private int SumMorale()
        {
            int sum = 0;
            for (int i = 0; i < Count; i++) sum += Morale[i];
            return sum;
        }

        private void AddLog(string line)
        {
            if (_log.Count >= 240) _log.RemoveAt(0);
            _log.Add(line);
        }

        public long TeamPower(int team)
        {
            long power = 0;
            for (int i = 0; i < Count; i++)
                if (Team[i] == team && Alive[i] > 0 && Escaped[i] == 0) power += HpPool[i];
            return power;
        }

        public BattleReport BuildReport()
        {
            var report = new BattleReport
            {
                outcome = _outcome,
                winnerTeam = _outcome == BattleOutcome.AttackerWin ? 0 : (_outcome == BattleOutcome.DefenderWin ? 1 : -1),
                ticks = Tick,
                attackerRemaining = (int)(TeamPower(0) / 1000),
                defenderRemaining = (int)(TeamPower(1) / 1000),
                routedSquads = _routedSquads,
                totalKilled = _totalKilled,
                stateHash = RollingHash,
                summary = _setup.label + ": " + _outcome + " in " + Tick + " ticks (" + (Tick / Math.Max(1, _cfg.tickRate)) + "s)",
                lootGold = _outcome == BattleOutcome.AttackerWin ? 1200 + Tick * 2 : 0,
                lootWood = _outcome == BattleOutcome.AttackerWin ? 800 + Tick : 0,
                lootMana = _outcome == BattleOutcome.AttackerWin ? 200 : 0,
                heroXp = 40 + Tick / 10
            };
            for (int i = 0; i < Count; i++)
            {
                if (IsSummon[i] != 0) continue;
                int survivors = (Routed[i] == 0 && Escaped[i] == 0) ? Alive[i] : 0;
                int routed = RoutedLost[i] + ((Routed[i] != 0 && Escaped[i] == 0) ? Alive[i] : 0);
                int deployed = MaxAlive[i];
                report.outcomes.Add(new SquadOutcome
                {
                    team = Team[i], unitId = UnitIds[i], deployed = deployed,
                    survivors = survivors, routed = routed,
                    killed = Math.Max(0, deployed - survivors - routed)
                });
            }
            for (int i = 0; i < _log.Count; i++) report.log.Add(_log[i]);
            return report;
        }
    }
}
