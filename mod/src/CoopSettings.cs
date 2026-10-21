using System;
using System.IO;
using Shapez2Coop.Protocol;
using UnityEngine;

namespace Shapez2Multiplayer
{
    [Serializable]
    public sealed class CoopSettings
    {
        public int SimulationHz = 20;
        public bool VisualPrediction = true;
        public static CoopSettings Current { get; private set; } = new CoopSettings();
        private static string FilePath => Path.Combine(GameEnvironment.DataPath, "coop-settings.json");
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                if (new FileInfo(FilePath).Length > 4096) throw new InvalidDataException("Co-op settings exceed limit");
                var value = JsonUtility.FromJson<CoopSettings>(File.ReadAllText(FilePath));
                if (value == null || !SessionPayloads.SupportedRate(value.SimulationHz)) throw new InvalidDataException("Invalid co-op frequency");
                Current = value;
            }
            catch (Exception ex) { Shapez2Multiplayer.logger.Warning?.Log("Co-op settings: " + ex.Message); }
        }
        public static void Save()
        {
            try { File.WriteAllText(FilePath, JsonUtility.ToJson(Current, true)); }
            catch (Exception ex) { Shapez2Multiplayer.logger.Warning?.Log("Could not save co-op settings: " + ex.Message); }
        }
    }
}
