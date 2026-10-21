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
        private bool exercise;
        private uint firstPeer;
        private readonly CoopQaActions actions=new CoopQaActions(true,false);
        private int initialHz=20;
        private bool forceDesync,corrupted;
        private Action corruptBuffer;
        private Func<bool> occupied;
        private int corruptPartition=-1;
        private bool stalled;
        private void Start()
        {
            Instance = this; result.role = Role;
            Application.runInBackground = true; Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
            if(int.TryParse(Environment.GetEnvironmentVariable("SHAPEZ2_COOP_PERF_FRAME_CAP"),out var cap) && cap>=20 && cap<=240)Application.targetFrameRate=cap;
            deadline = Time.realtimeSinceStartupAsDouble + 300;
            if (double.TryParse(Environment.GetEnvironmentVariable("SHAPEZ2_COOP_PERF_SECONDS"), out var seconds)) duration = Math.Max(10, Math.Min(60, seconds));
            Debug.Log("PERF:START " + Role);
            exercise=Environment.GetEnvironmentVariable("SHAPEZ2_COOP_PERF_EXERCISE")=="1";
            forceDesync=Environment.GetEnvironmentVariable("SHAPEZ2_COOP_PERF_DESYNC")=="1";
            if(int.TryParse(Environment.GetEnvironmentVariable("SHAPEZ2_COOP_QA_HZ"),out var rate) && Shapez2Coop.Protocol.SessionPayloads.SupportedRate(rate))initialHz=rate;
            if(byte.TryParse(Environment.GetEnvironmentVariable(Role=="perf-host"?"SHAPEZ2_COOP_PERF_HOST_WORKERS":"SHAPEZ2_COOP_PERF_CLIENT_WORKERS"),out var workers) && workers<=16)
            {CoopSimulation.QaWorkers=workers;Debug.Log("PERF:WORKERS "+workers);}
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
                if (CoopSession.Faulted)
                {
                    if(forceDesync && corrupted && CoopSession.Status.Contains("Production state checksum mismatch"))
                    {Debug.Log("PERF:EXPECTED_DESYNC counts="+Shapez2Multiplayer.MapModel.BuildingCount);await Finish("pass");return;}
                    await Finish("fault: " + CoopSession.Status);return;
                }
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
                    if(forceDesync)CoopQaProduction.Seed();
                    result.buildings = Shapez2Multiplayer.MapModel.BuildingCount;
                    result.islands = Shapez2Multiplayer.MapModel.Islands.Count();
                    Debug.Log("PERF:WORLD buildings=" + result.buildings + " islands=" + result.islands);
                    phase = Environment.GetEnvironmentVariable("SHAPEZ2_COOP_PERF_JOIN_ONLY")=="1"?7:2;
                    next = Time.realtimeSinceStartupAsDouble + 5; return;
                }
                if (phase == 2) { Begin("native-before"); phase = 3; return; }
                if (phase == 3 && Due())
                {
                    End(); CoopSession.RequestSimulationRate(initialHz);
                    await MultiplayerCore.CreateLobby(steam: false, ENet: true);
                    phase = 4; next = Time.realtimeSinceStartupAsDouble + 5; return;
                }
                if (phase == 4) { Begin("host-alone"); phase = 5; return; }
                if (phase == 5 && Due())
                { End(); MultiplayerCore.Disconnect(canReturnToMenu: false); phase = 6; next = Time.realtimeSinceStartupAsDouble + 5; return; }
                if (phase == 6) { Begin("native-after"); phase = 7; return; }
                if (phase == 7 && Due())
                {
                    End();CoopSession.RequestSimulationRate(initialHz); await MultiplayerCore.CreateLobby(steam: false, ENet: true);
                    Begin("host-joining");
                    Debug.Log("PERF:JOIN_READY"); phase = 8; return;
                }
                if (phase == 8)
                {
                    if (MultiplayerCore.socketManager.Connected.Count > 0) peerSeen = true;
                    else if (peerSeen) { await Finish("join disconnected"); return; }
                }
                if (phase == 8 && MultiplayerCore.socketManager.Connected.Count > 0 && CoopSession.Ready)
                { End(); joinStep = CoopSession.StepCount;firstPeer=MultiplayerCore.socketManager.Connected.First().UniversalId; Begin("host-connected"); Debug.Log("PERF:JOINED host step=" + joinStep); phase = 9; return; }
                if (phase == 9 && Due())
                {if(forceDesync){if(MultiplayerCore.socketManager.Connected.Count==0){Debug.Log("PERF:PEER_ISOLATED hostActive="+MultiplayerCore.Hosting);await Finish("pass");}return;}
                  End(); if (MultiplayerCore.socketManager.Connected.Count == 0) { await Finish("client disconnected"); return; }
                  if(exercise){phase=30;return;}await Finish("pass");return; }
                if (phase == 20 && CoopSession.Ready)
                {
                    result.joinSeconds=Time.realtimeSinceStartupAsDouble-joinStarted;
                    result.buildings = Shapez2Multiplayer.MapModel.BuildingCount; result.islands = Shapez2Multiplayer.MapModel.Islands.Count();
                    Begin("client-connected"); Debug.Log("PERF:JOINED client step=" + CoopSession.StepCount); phase = 21; return;
                }
                if (phase == 21 && !MultiplayerCore.Client) { End(); await Finish("disconnected"); return; }
                if(phase==21 && !stalled && (Now-start)/(double)Stopwatch.Frequency>=3 &&
                    int.TryParse(Environment.GetEnvironmentVariable("SHAPEZ2_COOP_PERF_STALL_MS"),out var stall) && stall>0 && stall<=10000)
                {stalled=true;Debug.Log("PERF:STALL_BEGIN ms="+stall);System.Threading.Thread.Sleep(stall);Debug.Log("PERF:STALL_END");return;}
                if (phase == 20 && !MultiplayerCore.Client && Time.realtimeSinceStartupAsDouble > joinStarted+5)
                { await Finish("join disconnected"); return; }
                if (phase == 21 && (Now-start)/(double)Stopwatch.Frequency >= duration-2)
                { End();if(forceDesync){PrepareCorruption();phase=34;return;}if(exercise){phase=30;return;}result.status="pass"; Save(); Debug.Log("PERF:CLIENT_SAMPLE_READY"); phase=22; }
                if(phase==30 && Role=="perf-client" && CoopSession.Ready)
                {
                    actions.Tick();if(!actions.Complete)return;
                    if(CoopSession.EditRevision!=4)throw new InvalidOperationException("Unexpected large-factory action revision");
                    Debug.Log("PERF:ACTIONS_PASS revision=4");
                    MultiplayerCore.Disconnect(canReturnToMenu:false);
                    await MultiplayerCore.ReturnToMenu();await System.Threading.Tasks.Task.Delay(1000);
                    await MultiplayerCore.DirectConnect("127.0.0.1");phase=32;return;
                }
                if(phase==30 && Role=="perf-host" && CoopSession.Ready && MultiplayerCore.socketManager.Connected.Any(p=>p.UniversalId!=firstPeer))
                {Begin("host-reconnected");Debug.Log("PERF:RECONNECTED host revision="+CoopSession.EditRevision);phase=31;return;}
                if(phase==31 && Due())
                {if(CoopSession.EditRevision!=4)throw new InvalidOperationException("Reconnect lost edit revision");await Finish("pass");return;}
                if(phase==32 && CoopSession.Ready)
                {if(CoopSession.EditRevision!=4)throw new InvalidOperationException("Reconnect lost edit revision");Begin("client-reconnected");Debug.Log("PERF:RECONNECTED client revision=4");phase=33;return;}
                if(phase==33 && (Now-start)/(double)Stopwatch.Frequency>=duration-2)
                {End();result.status="pass";Save();Debug.Log("PERF:CLIENT_SAMPLE_READY");phase=22;}
            }
            catch (Exception ex) { Debug.LogError("PERF:FAIL " + ex); await Finish("exception: " + ex.Message); }
            finally { busy = false; }
        }
        private bool Due() => (Now - start) / (double)Stopwatch.Frequency >= duration;
        private void PrepareCorruption()
        {
            Shapez2Multiplayer.GameSessionOrchestrator.FinalizeLogicUpdate();
            foreach(var b in Shapez2Multiplayer.MapModel.Buildings)
            {
                if(b.Configuration is ItemProducerConfiguration)
                {
                    var lane=b.State.As<ItemProducerSimulationState>().OutputLaneState;
                    if(lane.Item==null)continue;
                    corruptBuffer=lane.Clear;occupied=()=>lane.Item!=null;
                    corruptPartition=Shapez2Coop.Protocol.DigestSchedule.Partition(b.Tile_G.x,b.Tile_G.y,b.Tile_G.z);
                    Debug.Log("PERF:CORRUPTION_ARMED producer partition="+corruptPartition);return;
                }
                var state=b.State.As<Game.Content.BuildingPath.Simulation.ConveyorSimulationState>();
                if(state==null)continue;
                var slot=state.Slot0.HasItem?state.Slot0:state.Slot1.HasItem?state.Slot1:null;
                if(slot==null)continue;
                corruptBuffer=slot.Clear;occupied=()=>slot.HasItem;corruptPartition=Shapez2Coop.Protocol.DigestSchedule.Partition(b.Tile_G.x,b.Tile_G.y,b.Tile_G.z);
                Debug.Log("PERF:CORRUPTION_ARMED partition="+corruptPartition);return;
            }
            throw new InvalidOperationException("No occupied belt for negative simulation test");
        }
        public void BeforeDigest(int partition)
        {
            if(phase!=34 || corrupted || partition!=corruptPartition)return;
            if(!occupied()){PrepareCorruption();return;}
            corruptBuffer();corrupted=true;
            Debug.Log("PERF:BUFFER_CORRUPTED partition="+partition+" buildings="+Shapez2Multiplayer.MapModel.BuildingCount);
        }
        public async void RequestStop() {if(!finished)await Finish(result.status??"stopped");}
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
            await MultiplayerCore.ReturnToMenu();
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
        [HarmonyPrefix] private static void Prefix(int partition,out long __state)
        {CoopQaPerf.Instance?.BeforeDigest(partition);__state=CoopQaPerf.Now;}
        [HarmonyPostfix] private static void Postfix(long __state) => CoopQaPerf.Instance?.Record("digest",CoopQaPerf.Now-__state);
    }
    [HarmonyPatch(typeof(CoopSimulation), nameof(CoopSimulation.Capture))]
    internal static class CoopQaPerfCapture
    {
        [HarmonyPrefix] private static void Prefix(out long __state) => __state=CoopQaPerf.Now;
        [HarmonyPostfix] private static void Postfix(long __state) => CoopQaPerf.Instance?.Record("capture",CoopQaPerf.Now-__state);
    }
}
