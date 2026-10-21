using System;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using HarmonyLib;

namespace Shapez2Multiplayer
{
    public sealed class CoopQaKeyboard : Keyboard
    {
        protected override void RefreshConfiguration() { keyboardLayout = "qa"; }
    }
    // Compiled only with /p:CoopQa=true. Never included in the distributable build.
    public sealed class CoopQaBot : MonoBehaviour
    {
        private string role;
        private bool started, reported;
        private double deadline, menuReadyAt;
        private ulong? joinedStep;
        private long? joinedTime;
        private int initialHz = 20, rateStage;
        private bool switchRates;
        private string stopFile;
        private readonly CoopQaActions actions = new CoopQaActions();
        private readonly CoopQaRace race = new CoopQaRace();
        private readonly System.Collections.Generic.HashSet<uint> admittedPeers = new System.Collections.Generic.HashSet<uint>();
        private int expectedPeers = 1;
        private bool reconnectStarted;
        private void Start()
        {
            stopFile = Environment.GetEnvironmentVariable("SHAPEZ2_COOP_QA_STOP_FILE");
            role = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--coop-qa="))?.Substring(10);
            if (int.TryParse(Environment.GetEnvironmentVariable("SHAPEZ2_COOP_QA_PEERS"), out var peers))
                expectedPeers = Math.Max(1, Math.Min(3, peers));
            if (int.TryParse(Environment.GetEnvironmentVariable("SHAPEZ2_COOP_QA_HZ"), out var hz) &&
                Shapez2Coop.Protocol.SessionPayloads.SupportedRate(hz)) initialHz = hz;
            switchRates = Environment.GetEnvironmentVariable("SHAPEZ2_COOP_QA_SWITCH_RATES") == "1";
            CoopSettings.Current.VisualPrediction = true;
            // Batchmode has no physical input devices. The game's localization tick reads
            // Keyboard.current even when no UI is being operated.
            if (Keyboard.current == null || Keyboard.current.keyboardLayout == null)
            {
                InputSystem.RegisterLayout<CoopQaKeyboard>();
                InputSystem.AddDevice<CoopQaKeyboard>().MakeCurrent();
            }
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            foreach (var method in new[] {
                AccessTools.Method(typeof(HUD), nameof(HUD.Initialize)),
                AccessTools.Method(typeof(PlayerActionManager), nameof(PlayerActionManager.TryScheduleAction)),
                AccessTools.Method(typeof(GameSessionOrchestrator), "StartLogicUpdate") })
            {
                var patches = Harmony.GetPatchInfo(method);
                if (patches == null || !patches.Owners.Contains("bknibb.Shapez2Multiplayer"))
                    throw new InvalidOperationException("Required co-op hook missing: " + method);
            }
            Debug.Log("QA:HOOKS_PRESENT");
            if (role != null && role.StartsWith("perf-")) { gameObject.AddComponent<CoopQaPerf>().Role = role; role = null; return; }
            if (role == "ui") { gameObject.AddComponent<CoopQaUi>(); role = null; return; }
            deadline = Time.realtimeSinceStartupAsDouble + 360;
            Debug.Log("QA:MOD_LOADED role=" + role + " data=" + GameEnvironment.DataPath);
        }
        private async void Update()
        {
            if (!string.IsNullOrEmpty(stopFile) && System.IO.File.Exists(stopFile))
            {
                if(CoopQaPerf.Instance!=null){stopFile=null;reported=true;CoopQaPerf.Instance.RequestStop();return;}
                stopFile = null; reported = true; Debug.Log("QA:SHUTDOWN");
                MultiplayerCore.Disconnect(canReturnToMenu: false);
                if (Shapez2Multiplayer.Game.IsGameInSession(out IGameStartOptions _))
                    await Shapez2Multiplayer.GameOrchestrator.LoadMainMenu();
                Application.Quit(); return;
            }
            if (string.IsNullOrEmpty(role) || reported) return;
            if (Time.realtimeSinceStartupAsDouble > deadline || CoopSession.Faulted)
            { reported = true; Debug.LogError("QA:FAIL " + CoopSession.Status); return; }
            if (!started && Shapez2Multiplayer.Game.IsGameInMainMenu())
            {
                if (menuReadyAt == 0) { menuReadyAt = Time.realtimeSinceStartupAsDouble; return; }
                if (Time.realtimeSinceStartupAsDouble < menuReadyAt + 3) return;
                started = true;
                try
                {
                    if (role != "host") { await MultiplayerCore.DirectConnect("127.0.0.1"); return; }
                    var data = Shapez2Multiplayer.GameOrchestrator.InitializationDependencyContainer.Resolve<IGameData>();
                    var config = GameParameters.DefaultForMode(data.GetGameModeDefinition(data.GameModeIds.First()));
                    await Shapez2Multiplayer.GameOrchestrator.LoadSession(new GameStartOptionsStartNew(config, false, "coop-qa-factory"));
                    CoopQaProduction.Seed();
                    if (!CoopSession.RequestSimulationRate(initialHz)) throw new InvalidOperationException("Initial QA frequency rejected");
                    await MultiplayerCore.CreateLobby(steam: false, ENet: true);
                    Debug.Log("QA:HOST_READY");
                }
                catch (Exception ex) { reported = true; Debug.LogError("QA:FAIL " + ex); }
            }
            if (CoopSession.Ready)
            {
                if (role != "host" && !MultiplayerCore.connectionManager.FinishedConnecting) return;
                if (role == "host")
                {
                    foreach (var peer in MultiplayerCore.socketManager.Connected)
                    {
                        if (!admittedPeers.Add(peer.UniversalId)) continue;
                        joinedStep = null;
                        joinedTime = null;
                        Debug.Log("QA:HOST_JOIN number=" + admittedPeers.Count + " revision=" + CoopSession.EditRevision);
                    }
                    if (MultiplayerCore.socketManager.Connected.Count != expectedPeers) return;
                }
                if (!joinedStep.HasValue)
                {
                    joinedStep = CoopSession.StepCount;
                    joinedTime = Shapez2Multiplayer.GameSessionOrchestrator.Simulator.SimulationTime.Value;
                }
                long elapsed = Shapez2Multiplayer.GameSessionOrchestrator.Simulator.SimulationTime.Value - joinedTime.Value;
                long second = Game.Core.Simulation.Ticks.OneSecond.Value;
                if (role == "host" && switchRates && admittedPeers.Count == expectedPeers)
                {
                    int rate = rateStage == 0 ? 30 : 60;
                    if (rateStage < 2 && elapsed >= second * (rateStage == 0 ? 6 : 12))
                    {
                        if (!CoopSession.RequestSimulationRate(rate)) throw new InvalidOperationException("Live frequency change rejected");
                        rateStage++;
                    }
                }
                CoopQaProduction.Tick(role);
                if (role == "host" || role == "client")
                {
                    if (role == "client" && !reconnectStarted && MultiplayerCore.connectionManager.Connections.Count < expectedPeers) return;
                    try { if (!race.Tick(role)) return; }
                    catch (Exception ex) { reported = true; Debug.LogError("QA:FAIL " + ex); return; }
                }
                if (role == "client")
                {
                    if (!reconnectStarted && MultiplayerCore.connectionManager.Connections.Count < expectedPeers) return;
                    try { actions.Tick(); }
                    catch (Exception ex) { reported = true; Debug.LogError("QA:FAIL " + ex); return; }
                    if (!actions.Complete) return;
                }
                if (role != "client" && (CoopSession.EditRevision < 6 || !CoopQaProduction.Refilled)) return;
                if (elapsed < second * (switchRates && !reconnectStarted ? 24 : 12)) return;
                if (role == "host" && admittedPeers.Count < expectedPeers + 1) return;
                if (role == "client" && !reconnectStarted)
                {
                    if (CoopPrediction.PendingCount != 0 || CoopPrediction.VisualCount != 0 ||
                        CoopPrediction.Confirmed < 4 || CoopPrediction.RolledBack < 1 ||
                        CoopPrediction.ForeignConfirmationsIgnored == 0 || CoopPrediction.DrawnObjects == 0)
                    { reported = true; Debug.LogError("QA:FAIL Incomplete prediction evidence"); return; }
                    Debug.Log("QA:PREDICTION_PASS confirmed=" + CoopPrediction.Confirmed + " rollback=" + CoopPrediction.RolledBack +
                        " foreign=" + CoopPrediction.ForeignConfirmationsIgnored + " drawn=" + CoopPrediction.DrawnObjects);
                    reconnectStarted = true;
                    Debug.Log("QA:FIRST_SESSION_PASS revision=" + CoopSession.EditRevision);
                    try
                    {
                        MultiplayerCore.Disconnect(canReturnToMenu: false);
                        if (CoopPrediction.PendingCount != 0 || CoopPrediction.VisualCount != 0)
                            throw new InvalidOperationException("Disconnect retained visual predictions");
                        await Shapez2Multiplayer.GameOrchestrator.LoadMainMenu();
                        await System.Threading.Tasks.Task.Delay(2000);
                        joinedStep = null;
                        joinedTime = null;
                        await MultiplayerCore.DirectConnect("127.0.0.1");
                        Debug.Log("QA:RECONNECT_REQUESTED");
                    }
                    catch (Exception ex) { reported = true; Debug.LogError("QA:FAIL " + ex); }
                    return;
                }
                if (CoopSession.EditRevision != 6)
                { reported = true; Debug.LogError("QA:FAIL Reconnect lost authoritative edit revision"); return; }
                Debug.Log((role == "host" || role == "client" ? "QA:RECONNECT_PASS" : "QA:OBSERVER_READY") +
                    " role=" + role + " revision=" + CoopSession.EditRevision);
                reported = true;
                Debug.Log("QA:STEPS_PASS role=" + role + " step=" + CoopSession.StepCount + " digest=" + BitConverter.ToString(CoopSimulation.Digest()));
            }
        }
    }
}
