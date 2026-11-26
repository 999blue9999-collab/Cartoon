using System;
using System.Collections.Generic;
using System.IO;
using Nore.Data;
using Nore.Economy;
using Nore.Military;
using Nore.World;
using UnityEngine;

namespace Nore.State
{
    /// <summary>Flat, JsonUtility-friendly save file. No dictionaries, no Unity objects.</summary>
    [Serializable]
    public class SaveData
    {
        public int version = 1;
        public long savedAtUnix;
        public int rngSeed = 20260606;
        public string profileName = "Prince of Nore";
        public StrongholdState stronghold = new StrongholdState();
        public ArmyState army = new ArmyState();
        public HeroManager heroes = new HeroManager();
        public DragonManager dragons = new DragonManager();
        public CaravanSystem caravans = new CaravanSystem();
        public WorldState world = new WorldState();
    }

    public sealed class OfflineReport
    {
        public long elapsedSeconds;
        public long gold, wood, mana;
        public int completedUpgrades, deliveredCaravans, recoveredTroops, recoveredEnergy;
        public bool capped;
        public List<string> lines = new List<string>();

        public string Display()
        {
            string s = "Offline for " + GameTime.Duration(elapsedSeconds) + (capped ? " (capped)" : "") +
                       ": +" + gold + "g / +" + wood + "w / +" + mana + "m";
            if (completedUpgrades > 0) s += ", " + completedUpgrades + " upgrades finished";
            if (deliveredCaravans > 0) s += ", " + deliveredCaravans + " caravans delivered";
            if (recoveredTroops > 0) s += ", " + recoveredTroops + " troops recovered";
            return s;
        }
    }

    /// <summary>Owns all mutable state. Everything the UI mutates goes through here.</summary>
    public sealed class GameState
    {
        public readonly DataRegistry Data;
        public readonly DeterministicRng Rng;
        public readonly List<string> Log = new List<string>();

        public StrongholdState Stronghold;
        public ArmyState Army;
        public HeroManager Heroes;
        public DragonManager Dragons;
        public CaravanSystem Caravans;
        public WorldState World;
        public string ProfileName = "Prince of Nore";

        private GameState(DataRegistry data, SaveData save)
        {
            Data = data;
            Rng = new DeterministicRng(save.rngSeed);
            ProfileName = save.profileName;
            Stronghold = save.stronghold ?? StrongholdState.CreateNew(data, GameTime.NowUnix());
            Army = save.army ?? ArmyState.CreateNew(data);
            Heroes = save.heroes ?? HeroManager.CreateNew(data, GameTime.NowUnix());
            Dragons = save.dragons ?? DragonManager.CreateNew(data);
            Caravans = save.caravans ?? new CaravanSystem();
            World = save.world ?? WorldState.CreateNew(data, GameTime.NowUnix());
        }

        public static GameState New(DataRegistry data, int seed = 20260606)
        {
            long now = GameTime.NowUnix();
            var save = new SaveData
            {
                savedAtUnix = now,
                rngSeed = seed,
                stronghold = StrongholdState.CreateNew(data, now),
                army = ArmyState.CreateNew(data),
                heroes = HeroManager.CreateNew(data, now),
                dragons = DragonManager.CreateNew(data),
                caravans = new CaravanSystem(),
                world = WorldState.CreateNew(data, now)
            };
            var state = new GameState(data, save);
            state.AddLog("New realm created: " + data.Summary());
            return state;
        }

        public static GameState FromSave(SaveData save, DataRegistry data)
        {
            var state = new GameState(data, save ?? new SaveData());
            state.AddLog("Save loaded (saved " + GameTime.Duration(GameTime.NowUnix() - save.savedAtUnix) + " ago)");
            return state;
        }

        public SaveData ToSave()
        {
            return new SaveData
            {
                version = 1,
                savedAtUnix = GameTime.NowUnix(),
                rngSeed = Rng.Seed,
                profileName = ProfileName,
                stronghold = Stronghold,
                army = Army,
                heroes = Heroes,
                dragons = Dragons,
                caravans = Caravans,
                world = World
            };
        }

        public void AddLog(string line)
        {
            if (Log.Count >= 200) Log.RemoveAt(0);
            Log.Add("[" + DateTime.UtcNow.ToString("HH:mm:ss") + "] " + line);
            World.Note(line);
        }

        /// <summary>Idempotent per-second progression. Safe to call every Update().</summary>
        public void Tick(long now)
        {
            var lines = new List<string>();
            Stronghold.Update(now, Data, lines);
            Army.UpdateTraining(now, lines);
            Army.UpdateHospital(now, lines);
            Heroes.UpdateEnergy(now, Data, lines);
            Caravans.Update(now, Stronghold, Data, lines);
            World.DailyReset(now, Data, Dragons, lines);
            for (int i = 0; i < lines.Count; i++) AddLog(lines[i]);
        }

        public OfflineReport ApplyOffline(long now)
        {
            var report = new OfflineReport();
            long saved = SaveService.LastLoadedSavedAt;
            long elapsed = Math.Max(0, now - saved);
            long cap = Math.Max(1, Data.Config.offline.maxCatchupSeconds);
            report.capped = elapsed > cap;
            report.elapsedSeconds = Math.Min(elapsed, cap);

            long g0 = Stronghold.resources.gold, w0 = Stronghold.resources.wood, m0 = Stronghold.resources.mana;
            int upgrades0 = Stronghold.ActiveUpgrades(saved);
            int caravans0 = Caravans.Active().Count;
            int hospital0 = Army.HospitalLoad();
            int energy0 = 0;
            for (int i = 0; i < Heroes.heroes.Count; i++) energy0 += Heroes.heroes[i].energy;

            if (report.capped) Stronghold.lastProductionUnix = now - cap;
            var lines = new List<string>();
            Stronghold.Update(now, Data, lines);
            Army.UpdateTraining(now, lines);
            Army.UpdateHospital(now, lines);
            Heroes.UpdateEnergy(now, Data, lines);
            Caravans.Update(now, Stronghold, Data, lines);
            World.DailyReset(now, Data, Dragons, lines);

            report.gold = Stronghold.resources.gold - g0;
            report.wood = Stronghold.resources.wood - w0;
            report.mana = Stronghold.resources.mana - m0;
            report.completedUpgrades = Math.Max(0, upgrades0 - Stronghold.ActiveUpgrades(now));
            report.deliveredCaravans = Math.Max(0, caravans0 - Caravans.Active().Count);
            report.recoveredTroops = Math.Max(0, hospital0 - Army.HospitalLoad());
            int energy1 = 0;
            for (int i = 0; i < Heroes.heroes.Count; i++) energy1 += Heroes.heroes[i].energy;
            report.recoveredEnergy = energy1 - energy0;
            report.lines = lines;
            for (int i = 0; i < lines.Count; i++) AddLog("[offline] " + lines[i]);
            AddLog(report.Display());
            return report;
        }
    }

    public static class SaveService
    {
        private const string FileName = "nore_save.json";
        public static long LastLoadedSavedAt { get; private set; }

        public static string SavePath => Path.Combine(Application.persistentDataPath, FileName);

        public static string Serialize(SaveData data) => JsonUtility.ToJson(data, true);

        public static SaveData Deserialize(string json) => JsonUtility.FromJson<SaveData>(json);

        public static bool Save(SaveData data, out string message)
        {
            try
            {
                string dir = Application.persistentDataPath;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(SavePath, Serialize(data));
                message = "Saved to " + SavePath;
                return true;
            }
            catch (Exception e)
            {
                message = "Save failed: " + e.Message;
                return false;
            }
        }

        public static bool Load(out SaveData data, out string message)
        {
            data = null;
            try
            {
                if (!File.Exists(SavePath)) { message = "No save file yet"; return false; }
                data = Deserialize(File.ReadAllText(SavePath));
                if (data == null || data.version != 1) { message = "Incompatible save version"; return false; }
                LastLoadedSavedAt = data.savedAtUnix;
                message = "Loaded save from " + SavePath;
                return true;
            }
            catch (Exception e)
            {
                message = "Load failed: " + e.Message;
                return false;
            }
        }

        /// <summary>Externalized data: copy the shipped JSON into persistentDataPath for modding.</summary>
        public static string ExportDataOverrides(DataRegistry data)
        {
            string dir = Path.Combine(Application.persistentDataPath, "Data");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            TextAsset[] assets = Resources.LoadAll<TextAsset>("Data");
            for (int i = 0; i < assets.Length; i++)
                File.WriteAllText(Path.Combine(dir, assets[i].name + ".json"), assets[i].text);
            return "Exported " + assets.Length + " data files to " + dir;
        }
    }
}
