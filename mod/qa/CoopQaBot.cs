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
        private string stopFile;
        private void Start()
        {
            stopFile = Environment.GetEnvironmentVariable("SHAPEZ2_COOP_QA_STOP_FILE");
            role = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--coop-qa="))?.Substring(10);
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
            deadline = Time.realtimeSinceStartupAsDouble + 360;
            Debug.Log("QA:MOD_LOADED role=" + role + " data=" + GameEnvironment.DataPath);
        }
        private async void Update()
        {
            if (!string.IsNullOrEmpty(stopFile) && System.IO.File.Exists(stopFile))
            { stopFile = null; Debug.Log("QA:SHUTDOWN"); Application.Quit(); return; }
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
                    if (role == "client") { await MultiplayerCore.DirectConnect("127.0.0.1"); return; }
                    var data = Shapez2Multiplayer.GameOrchestrator.InitializationDependencyContainer.Resolve<IGameData>();
                    var config = GameParameters.DefaultForMode(data.GetGameModeDefinition(data.GameModeIds.First()));
                    await Shapez2Multiplayer.GameOrchestrator.LoadSession(new GameStartOptionsStartNew(config, false, "coop-qa-factory"));
                    await MultiplayerCore.CreateLobby(steam: false, ENet: true);
                    Debug.Log("QA:HOST_READY");
                }
                catch (Exception ex) { reported = true; Debug.LogError("QA:FAIL " + ex); }
            }
            if (CoopSession.Ready)
            {
                if (role == "host" && MultiplayerCore.socketManager.Connected.Count != 1) return;
                if (role == "client" && !MultiplayerCore.connectionManager.FinishedConnecting) return;
                if (!joinedStep.HasValue) joinedStep = CoopSession.StepCount;
                if (CoopSession.StepCount < joinedStep.Value + 200) return;
                reported = true;
                Debug.Log("QA:STEPS_PASS role=" + role + " step=" + CoopSession.StepCount + " digest=" + BitConverter.ToString(CoopSimulation.Digest()));
            }
        }
    }
}
