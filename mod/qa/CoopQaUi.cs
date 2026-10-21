using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Shapez2Multiplayer
{
    // A separate UI gate: networking bots never open these menus themselves.
    public sealed class CoopQaUi : MonoBehaviour
    {
        private int phase;
        private double next, deadline;
        private bool busy;
        private bool menuObserved;
        private HUDMultiplayerPausePanel panel;
        private string screenshot;
        private void Start()
        {
            Application.runInBackground = true;
            deadline = Time.realtimeSinceStartupAsDouble + 150;
        }
        private async void Update()
        {
            if (busy || phase == 99 || Time.realtimeSinceStartupAsDouble < next) return;
            busy = true;
            try
            {
                if (Time.realtimeSinceStartupAsDouble > deadline) throw new InvalidOperationException("UI gate timeout");
                if (phase == 0)
                {
                    if (!Shapez2Multiplayer.Game.IsGameInMainMenu()) return;
                    var entries = Resources.FindObjectsOfTypeAll<HUDMenuButton>()
                        .Where(b => b.name == "CoopMultiplayerEntry" && b.gameObject.activeInHierarchy).ToArray();
                    if (entries.Length == 0) return; // Main menu initializes asynchronously.
                    if (entries.Length != 1) throw new InvalidOperationException("Expected one visible multiplayer menu entry");
                    if (!menuObserved) { menuObserved = true; next = Time.realtimeSinceStartupAsDouble + 3; return; }
                    entries[0].OnClick.Invoke(); phase = 1; next = Time.realtimeSinceStartupAsDouble + 2;
                }
                else if (phase == 1)
                {
                    var view = Resources.FindObjectsOfTypeAll<HUDMenuMultiplayerState>().Single(v => v.gameObject.activeInHierarchy);
                    if (view.DirectConnectInput == null || view.BtnDirectConnect == null) throw new InvalidOperationException("Connection view incomplete");
                    Debug.Log("QA:MENU_ENTRY_PASS");
                    view.GoBack(); phase = 2; next = Time.realtimeSinceStartupAsDouble + 1;
                }
                else if (phase == 2)
                {
                    var data = Shapez2Multiplayer.GameOrchestrator.InitializationDependencyContainer.Resolve<IGameData>();
                    var config = GameParameters.DefaultForMode(data.GetGameModeDefinition(data.GameModeIds.First()));
                    await Shapez2Multiplayer.GameOrchestrator.LoadSession(new GameStartOptionsStartNew(config, false, "coop-ui-qa"));
                    Shapez2Multiplayer.HudEvents.ShowPauseMenu.Invoke(); phase = 3; next = Time.realtimeSinceStartupAsDouble + 2;
                }
                else if (phase == 3)
                {
                    panel = HUDMultiplayerPausePanel.instance;
                    Validate();
                    Button("SimulationFrequency").OnClick.Invoke(); phase = 4; next = Time.realtimeSinceStartupAsDouble + 1;
                }
                else if (phase == 4)
                {
                    if (CoopSettings.Current.SimulationHz != 30 || !Label("SimulationFrequency").Contains("30"))
                        throw new InvalidOperationException("Frequency button did not update");
                    Button("VisualPrediction").OnClick.Invoke(); phase = 5; next = Time.realtimeSinceStartupAsDouble + 1;
                }
                else if (phase == 5)
                {
                    if (CoopSettings.Current.VisualPrediction || string.IsNullOrWhiteSpace(Label("VisualPrediction")))
                        throw new InvalidOperationException("Prediction button did not update");
                    Screen.SetResolution(1920, 1080, false); phase = 6; next = Time.realtimeSinceStartupAsDouble + 2;
                }
                else if (phase == 6)
                {
                    Validate(); screenshot = Path.Combine(GameEnvironment.DataPath, "ui-pause-large.png");
                    ScreenCapture.CaptureScreenshot(screenshot); phase = 7; next = Time.realtimeSinceStartupAsDouble + 2;
                }
                else if (phase == 7)
                {
                    if (!File.Exists(screenshot)) throw new InvalidOperationException("Large UI screenshot missing");
                    Screen.SetResolution(1100, 740, false); phase = 8; next = Time.realtimeSinceStartupAsDouble + 2;
                }
                else if (phase == 8)
                {
                    Validate(); screenshot = Path.Combine(GameEnvironment.DataPath, "ui-pause-small.png");
                    ScreenCapture.CaptureScreenshot(screenshot); phase = 9; next = Time.realtimeSinceStartupAsDouble + 2;
                }
                else if (phase == 9)
                {
                    if (!File.Exists(screenshot)) throw new InvalidOperationException("Small UI screenshot missing");
                    Debug.Log("QA:UI_PASS"); phase = 99;
                    await Shapez2Multiplayer.GameOrchestrator.LoadMainMenu();
                    await System.Threading.Tasks.Task.Delay(3000); Application.Quit();
                }
            }
            catch (Exception ex) { phase = 99; Debug.LogError("QA:FAIL UI " + ex); Application.Quit(1); }
            finally { busy = false; }
        }
        private HUDButton Button(string name) => panel.transform.Find(name).GetComponent<HUDButton>();
        private string Label(string name) => Button(name).GetComponentInChildren<TMP_Text>(true).text;
        private void Validate()
        {
            if (panel == null || !panel.gameObject.activeInHierarchy) throw new InvalidOperationException("Pause panel not visible");
            var root = panel.GetComponent<RectTransform>();
            foreach (var name in new[] { "HostButton", "InviteButton", "ReportIssueButton", "SimulationFrequency", "VisualPrediction" })
            {
                var button = Button(name);
                if (string.IsNullOrWhiteSpace(Label(name))) throw new InvalidOperationException("Empty button: " + name);
                var text = button.GetComponentInChildren<TMP_Text>(true);
                text.ForceMeshUpdate();
                if (text.isTextOverflowing) throw new InvalidOperationException("Clipped label: " + name);
                var corners = new Vector3[4]; button.GetComponent<RectTransform>().GetWorldCorners(corners);
                foreach (var corner in corners)
                {
                    var local = root.InverseTransformPoint(corner);
                    if (!root.rect.Contains(new Vector2(local.x, local.y))) throw new InvalidOperationException("Button outside panel: " + name);
                }
                Debug.Log("QA:UI_LABEL " + name + "=" + Label(name));
            }
            Debug.Log("QA:UI_LAYOUT " + Screen.width + "x" + Screen.height);
        }
    }
}
