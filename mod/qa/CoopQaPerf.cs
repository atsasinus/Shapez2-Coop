using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Shapez2Multiplayer
{
    [Serializable] public sealed class CoopQaPerfResult
    {
        public string role, status;
        public int buildings, islands;
        public double joinSeconds;
        [NonSerialized] public List<CoopQaPerfSample> samples = new List<CoopQaPerfSample>();
    }
    [Serializable] public sealed class CoopQaPerfSample
    {
        public string phase;
        public int frames, steps, gc0, gc1, gc2;
        public double seconds, fps, frameP50Ms, frameP95Ms, frameP99Ms, frameMaxMs, tickMeanMs;
        public double stepMeanMs, stepMaxMs, digestMeanMs, digestMaxMs, captureMaxMs, simSeconds;
        public long managedBytes, workingSetBytes;
    }
    public sealed class CoopQaPerf : MonoBehaviour
    {
        public static CoopQaPerf Instance;
        public static long Now => Stopwatch.GetTimestamp();
        public static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        public string Role;
        private readonly CoopQaPerfResult result = new CoopQaPerfResult();
        private readonly List<double> frames = new List<double>();
        private long lastFrame, start, simStart;
        private double deadline, next;
        private int phase, tickCount, stepCount, digestCount, gc0, gc1, gc2;
        private double tickMs, stepMs, stepMax, digestMs, digestMax, captureMax;
        private bool busy, measuring, finished;
        private string sampleName;
        private double duration = 15;
        private ulong joinStep;
        private double joinStarted;
        private bool peerSeen;
        private void Start()
        {
            Instance = this; result.role = Role;
            Application.runInBackground = true; Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
            deadline = Time.realtimeSinceStartupAsDouble + 300;
            if (double.TryParse(Environment.GetEnvironmentVariable("SHAPEZ2_COOP_PERF_SECONDS"), out var seconds)) duration = Math.Max(10, Math.Min(60, seconds));
            Debug.Log("PERF:START " + Role);
        }
        private async void Update()
        {
            long stamp = Now;
            if (measuring && lastFrame != 0 && frames.Count < 100000) frames.Add(Milliseconds(stamp - lastFrame));
            lastFrame = stamp;
            if (busy || finished) return;
            busy = true;
            try
            {
                if (CoopSession.Faulted) { await Finish("fault: " + CoopSession.Status); return; }
                if (Time.realtimeSinceStartupAsDouble > deadline) { await Finish("timeout phase=" + phase); return; }
                if (Time.realtimeSinceStartupAsDouble < next) return;
                if (phase == 0)
                {
                    if (!Shapez2Multiplayer.Game.IsGameInMainMenu()) return;
                    phase = 1; next = Time.realtimeSinceStartupAsDouble + 3; return;
                }
                if (phase == 1)
                {
                    if (Role == "perf-client")
                    { joinStarted=Time.realtimeSinceStartupAsDouble; await MultiplayerCore.DirectConnect("127.0.0.1"); phase = 20; return; }
                    var path = Path.GetFullPath(Environment.GetEnvironmentVariable("SHAPEZ2_COOP_PERF_SAVE"));
                    var localRoot = Path.GetFullPath(Path.Combine(GameEnvironment.DataPath, "..", ".."));
                    if (!path.StartsWith(Path.GetFullPath(Path.Combine(localRoot, "..")), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Perf save must be an isolated workspace copy");
                    var serializers = Shapez2Multiplayer.MainMenuOrchestratorBackgroundGameOrchestrator.DataSerializers;
                    using (var stream = new MemoryStream(File.ReadAllBytes(path), false))
                    {
                        var reader = new SaveFileAccessor(Shapez2Multiplayer.MainMenuOrchestratorLogger).ReadFromStream(stream, serializers);
                        await Shapez2Multiplayer.GameOrchestrator.LoadSession(new GameStartOptionsContinueExisting(reader, false,
                            "perf-" + Guid.NewGuid().ToString("N"), false));
                    }
                    Shapez2Multiplayer.SimulationSpeed.Speed = 1;
                    Shapez2Multiplayer.SimulationSpeed.IsPaused = false;
                    result.buildings = Shapez2Multiplayer.MapModel.BuildingCount;
                    result.islands = Shapez2Multiplayer.MapModel.Islands.Count();
                    Debug.Log("PERF:WORLD buildings=" + result.buildings + " islands=" + result.islands);
                    phase = 2; next = Time.realtimeSinceStartupAsDouble + 5; return;
                }
                if (phase == 2) { Begin("native-before"); phase = 3; return; }
                if (phase == 3 && Due())
                {
                    End(); CoopSession.RequestSimulationRate(20);
                    await MultiplayerCore.CreateLobby(steam: false, ENet: true);
                    phase = 4; next = Time.realtimeSinceStartupAsDouble + 5; return;
                }
                if (phase == 4) { Begin("host-alone"); phase = 5; return; }
                if (phase == 5 && Due())
                { End(); MultiplayerCore.Disconnect(canReturnToMenu: false); phase = 6; next = Time.realtimeSinceStartupAsDouble + 5; return; }
                if (phase == 6) { Begin("native-after"); phase = 7; return; }
                if (phase == 7 && Due())
                {
                    End(); await MultiplayerCore.CreateLobby(steam: false, ENet: true);
                    Begin("host-joining");
                    Debug.Log("PERF:JOIN_READY"); phase = 8; return;
                }
                if (phase == 8)
                {
                    if (MultiplayerCore.socketManager.Connected.Count > 0) peerSeen = true;
                    else if (peerSeen) { await Finish("join disconnected"); return; }
                }
                if (phase == 8 && MultiplayerCore.socketManager.Connected.Count > 0 && CoopSession.Ready)
                { End(); joinStep = CoopSession.StepCount; Begin("host-connected"); Debug.Log("PERF:JOINED host step=" + joinStep); phase = 9; return; }
                if (phase == 9 && Due())
                { End(); if (MultiplayerCore.socketManager.Connected.Count == 0) { await Finish("client disconnected"); return; } await Finish("pass"); return; }
                if (phase == 20 && CoopSession.Ready)
                {
                    result.joinSeconds=Time.realtimeSinceStartupAsDouble-joinStarted;
                    result.buildings = Shapez2Multiplayer.MapModel.BuildingCount; result.islands = Shapez2Multiplayer.MapModel.Islands.Count();
                    Begin("client-connected"); Debug.Log("PERF:JOINED client step=" + CoopSession.StepCount); phase = 21; return;
                }
                if (phase == 21 && !MultiplayerCore.Client) { End(); await Finish("disconnected"); return; }
                if (phase == 20 && !MultiplayerCore.Client && Time.realtimeSinceStartupAsDouble > joinStarted+5)
                { await Finish("join disconnected"); return; }
                if (phase == 21 && (Now-start)/(double)Stopwatch.Frequency >= duration-2)
                { End(); result.status="pass"; Save(); Debug.Log("PERF:CLIENT_SAMPLE_READY"); phase=22; }
            }
            catch (Exception ex) { Debug.LogError("PERF:FAIL " + ex); await Finish("exception: " + ex.Message); }
            finally { busy = false; }
        }
        private bool Due() => (Now - start) / (double)Stopwatch.Frequency >= duration;
        private void Begin(string name)
        {
            frames.Clear(); tickCount = stepCount = digestCount = 0; tickMs = stepMs = stepMax = digestMs = digestMax = captureMax = 0;
            gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
            start = Now; simStart = Shapez2Multiplayer.GameSessionOrchestrator.Simulator.SimulationTime.Value;
            sampleName = name; measuring = true; lastFrame = 0;
            Debug.Log("PERF:MEASURE " + name);
        }
        private void End()
        {
            if (!measuring) return; measuring = false;
            var sorted = frames.OrderBy(x => x).ToArray();
            double seconds = (Now - start) / (double)Stopwatch.Frequency;
            double Quantile(double q) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
            var sample = new CoopQaPerfSample {
                phase=sampleName, frames=frames.Count, seconds=seconds, fps=frames.Count / seconds,
                frameP50Ms=Quantile(.50), frameP95Ms=Quantile(.95), frameP99Ms=Quantile(.99), frameMaxMs=Quantile(1),
                tickMeanMs=tickCount == 0 ? 0 : tickMs/tickCount, steps=stepCount, stepMeanMs=stepCount==0?0:stepMs/stepCount,
                stepMaxMs=stepMax, digestMeanMs=digestCount==0?0:digestMs/digestCount, digestMaxMs=digestMax, captureMaxMs=captureMax,
                simSeconds=(Shapez2Multiplayer.GameSessionOrchestrator.Simulator.SimulationTime.Value-simStart)/(double)Game.Core.Simulation.Ticks.OneSecond.Value,
                gc0=GC.CollectionCount(0)-gc0, gc1=GC.CollectionCount(1)-gc1, gc2=GC.CollectionCount(2)-gc2,
                managedBytes=GC.GetTotalMemory(false), workingSetBytes=Process.GetCurrentProcess().WorkingSet64
            };
            result.samples.Add(sample); Debug.Log("PERF:SAMPLE " + JsonUtility.ToJson(sample)); Save();
        }
        private async System.Threading.Tasks.Task Finish(string status)
        {
            if (finished) return; End(); finished = true; result.status = status; Save(); Debug.Log("PERF:RESULT " + status);
            MultiplayerCore.Disconnect(canReturnToMenu: false);
            if (Shapez2Multiplayer.Game.IsGameInSession(out IGameStartOptions _)) await Shapez2Multiplayer.GameOrchestrator.LoadMainMenu();
            await System.Threading.Tasks.Task.Delay(3000); Application.Quit();
        }
        private void Save()
        {
            // Unity's runtime JSON serializer omits this list of custom QA records.
            string header = JsonUtility.ToJson(result);
            string samples = string.Join(",", result.samples.Select(sample => JsonUtility.ToJson(sample)));
            File.WriteAllText(Path.Combine(GameEnvironment.DataPath, "perf-result.json"), header.Substring(0,header.Length-1)+",\"samples\":["+samples+"]}");
        }
        public void Record(string kind, long ticks)
        {
            if (kind == "capture" || kind == "digest") Debug.Log("PERF:OP " + kind + " ms=" + Milliseconds(ticks).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            if (!measuring) return; double ms = Milliseconds(ticks);
            if (kind=="tick") { tickCount++; tickMs+=ms; }
            if (kind=="step") { stepCount++; stepMs+=ms; stepMax=Math.Max(stepMax,ms); }
            if (kind=="digest") { digestCount++; digestMs+=ms; digestMax=Math.Max(digestMax,ms); }
            if (kind=="capture") captureMax=Math.Max(captureMax,ms);
        }
    }
    [HarmonyPatch(typeof(GameSessionOrchestrator), nameof(GameSessionOrchestrator.Tick))]
    internal static class CoopQaPerfTick
    {
        [HarmonyPrefix] private static void Prefix(out long __state) => __state=CoopQaPerf.Now;
        [HarmonyPostfix] private static void Postfix(long __state) => CoopQaPerf.Instance?.Record("tick",CoopQaPerf.Now-__state);
    }
    [HarmonyPatch(typeof(CoopSimulation), nameof(CoopSimulation.Advance))]
    internal static class CoopQaPerfStep
    {
        [HarmonyPrefix] private static void Prefix(out long __state) => __state=CoopQaPerf.Now;
        [HarmonyPostfix] private static void Postfix(long __state) => CoopQaPerf.Instance?.Record("step",CoopQaPerf.Now-__state);
    }
    [HarmonyPatch(typeof(CoopSimulation), nameof(CoopSimulation.Digest))]
    internal static class CoopQaPerfDigest
    {
        [HarmonyPrefix] private static void Prefix(out long __state) => __state=CoopQaPerf.Now;
        [HarmonyPostfix] private static void Postfix(long __state) => CoopQaPerf.Instance?.Record("digest",CoopQaPerf.Now-__state);
    }
    [HarmonyPatch(typeof(CoopSimulation), nameof(CoopSimulation.Capture))]
    internal static class CoopQaPerfCapture
    {
        [HarmonyPrefix] private static void Prefix(out long __state) => __state=CoopQaPerf.Now;
        [HarmonyPostfix] private static void Postfix(long __state) => CoopQaPerf.Instance?.Record("capture",CoopQaPerf.Now-__state);
    }
}
