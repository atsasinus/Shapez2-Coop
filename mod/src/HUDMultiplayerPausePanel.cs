using Core.Dependency;
using Core.Localization;
using Shapez2UILib;
using Steamworks;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Shapez2Multiplayer
{
    public class HUDMultiplayerPausePanel : HUDComponent
    {
        public static HUDMultiplayerPausePanel? instance;
        public static List<HUDPlayerEntry> Entries = new List<HUDPlayerEntry>();
        private TextMeshProUGUI SessionStatus;
        private HUDButton RateButton, PredictionButton;
        [Construct]
        private void Construct(IDependencyResolver DependencyResolver)
        {
            HostButton = transform.GetChild(1).GetComponent<HUDButton>();
            HostButton.OnClick.AddListener(new UnityAction(ToggleHosting));
            InviteButton = transform.GetChild(2).GetComponent<HUDButton>();
            InviteButton.Text = "multiplayer.invite".T();
            InviteButton.OnClick.AddListener(InviteFriends);
            ReportIssueButton = transform.GetChild(3).GetComponent<HUDButton>();
            ReportIssueButton.Text = "multiplayer.report-issue".T();
            ReportIssueButton.OnClick.AddListener(ReportIssue);
            //HostButton.transform.GetChild(0).GetComponent<Image>().sprite = Shapez2Multiplayer.HUDButtonBase;
            UIScrollContainer = transform.GetChild(4).GetComponent<HUDScrollContainer>();
            ScrollRect = UIScrollContainer.GetComponent<ScrollRect>();
            // These controls must be created before this component's Construct:
            // adding an unconstructed child here breaks HUDLocalizedText updates.
            RateButton = transform.Find("SimulationFrequency").GetComponent<HUDButton>();
            RateButton.OnClick.AddListener(CycleRate);
            PredictionButton = transform.Find("VisualPrediction").GetComponent<HUDButton>();
            PredictionButton.OnClick.AddListener(TogglePrediction);
            instance = this;
            SessionStatus = UIFactory.AddTextPrimary(transform);
            SessionStatus.fontSize = 16;
            var statusRect = SessionStatus.rectTransform;
            statusRect.anchorMin = new Vector2(0, 1);
            statusRect.anchorMax = new Vector2(1, 1);
            statusRect.offsetMin = new Vector2(16, -66);
            statusRect.offsetMax = new Vector2(-16, -12);
            SessionStatus.enableWordWrapping = true;
            if (MultiplayerCore.Client)
            {
                foreach (var connection in MultiplayerCore.connectionManager.Connections)
                {
                    AddPlayer(connection);
                }
            }
        }
        public static void InviteFriends()
        {
            if (MultiplayerCore.Lobby.HasValue) SteamFriends.OpenGameInviteOverlay(MultiplayerCore.Lobby.Value.Id);
        }
        public static void ReportIssue()
        {
            Application.OpenURL("https://github.com/Bknibb/Shapez2Multiplayer/issues");
        }
        public void AddPlayer(IConnection connection)
        {
            GameObject PlayerObject = new GameObject(connection.Name);
            HUDPlayerEntry playerEntry = PlayerObject.AddComponent<HUDPlayerEntry>();
            playerEntry.Connection = connection;
            PlayerObject.transform.SetParent(ScrollRect.content);
            PlayerObject.transform.localScale = Vector3.one;
            PlayerObject.layer = LayerMask.NameToLayer("UI");
            RectTransform RectTransform = PlayerObject.AddComponent<RectTransform>();
            GameObject panel = UIFactory.AddPanel(PlayerObject.transform, playerEntry);
            TextMeshProUGUI NameText = UIFactory.AddTextPrimary(PlayerObject.transform);
            NameText.gameObject.name = "NameText";
            NameText.transform.SetParent(PlayerObject.transform);
            NameText.fontStyle = FontStyles.Normal;
            RectTransform NameTextRect = NameText.GetComponent<RectTransform>();
            NameTextRect.anchorMin = Vector2.zero;
            NameTextRect.anchorMax = new Vector2(0.4f, 1);
            NameTextRect.offsetMin = new Vector2(20, 20);
            NameTextRect.offsetMax = new Vector2(-20, -20);
            playerEntry.NameText = NameText;
            LayoutElement layoutElement = PlayerObject.AddComponent<LayoutElement>();
            layoutElement.minHeight = 80;

            TextMeshProUGUI PingText = UIFactory.AddTextPrimary(PlayerObject.transform);
            PingText.gameObject.name = "PingText";
            PingText.transform.SetParent(PlayerObject.transform);
            RectTransform PingTextRect = PingText.GetComponent<RectTransform>();
            PingTextRect.anchorMin = new Vector2(0.4f, 0);
            PingTextRect.anchorMax = new Vector2(0.6f, 1);
            PingTextRect.offsetMin = new Vector2(20, 20);
            PingTextRect.offsetMax = new Vector2(-20, -20);
            playerEntry.PingText = PingText;

            HUDButton KickButton = UIFactory.AddButton(PlayerObject.transform, playerEntry);
            KickButton.name = "KickButton";
            KickButton.transform.SetParent(PlayerObject.transform);
            RectTransform KickButtonRect = KickButton.GetComponent<RectTransform>();
            KickButtonRect.anchorMin = new Vector2(0.6f, 0);
            KickButtonRect.anchorMax = Vector2.one;
            KickButtonRect.offsetMin = new Vector2(20, 20);
            KickButtonRect.offsetMax = new Vector2(-20, -20);
            playerEntry.KickButton = KickButton;
            this.GetDependencyResolver().Inject(playerEntry);
            Entries.Add(playerEntry);
        }
        public void RemovePlayer(IConnection connection)
        {
            foreach (var entry in Entries)
            {
                if (entry.Connection.Equals(connection))
                {
                    entry.Dispose();
                    GameObject.Destroy(entry.gameObject);
                    Entries.Remove(entry);
                    return;
                }
            }
        }
        public void ClearPlayers()
        {
            foreach (var entry in Entries)
            {
                entry.Dispose();
                GameObject.Destroy(entry.gameObject);
            }
            Entries.Clear();
        }
        public override void OnDispose()
        {
            instance = null;
        }
        public override void OnUpdate(InputDownstreamContext context)
        {
            if (SessionStatus != null) SessionStatus.text = "Co-op alpha | " + CoopSession.Status +
                (CoopPrediction.PendingCount == 0 ? "" : " | " + CoopPrediction.PendingCount + " pending");
            int hz = CoopSession.Active ? CoopSession.SimulationHz : CoopSettings.Current.SimulationHz;
            RateButton.Text = new CombinedText("multiplayer.simulation-frequency".T(), new RawText(": " + hz + " Hz"));
            RateButton.Interactable = !MultiplayerCore.Client && !CoopSession.Faulted;
            PredictionButton.Text = new CombinedText("multiplayer.visual-prediction".T(), new RawText(": "),
                (CoopSettings.Current.VisualPrediction ? "multiplayer.on" : "multiplayer.off").T());
            HostButton.Interactable = !MultiplayerCore.InLobby || MultiplayerCore.Hosting;
            HostButton.Text = MultiplayerCore.InLobby && !MultiplayerCore.Hosting ? "multiplayer.inlobby".T() : MultiplayerCore.Hosting ? "multiplayer.stophosting".T() : "multiplayer.host".T();
            InviteButton.Interactable = MultiplayerCore.Hosting;
        }
        public void ToggleHosting()
        {
            if (MultiplayerCore.InLobby && !MultiplayerCore.Hosting) return;
            if (MultiplayerCore.Hosting)
            {
                MultiplayerCore.Disconnect();
            } else
            {
                MultiplayerCore.CreateLobby(steam: SteamClient.IsValid, ENet: true);
            }
        }
        private void CycleRate()
        {
            int hz = CoopSession.Active ? CoopSession.SimulationHz : CoopSettings.Current.SimulationHz;
            CoopSession.RequestSimulationRate(hz == 20 ? 30 : hz == 30 ? 60 : 20);
        }
        private void TogglePrediction()
        {
            CoopSettings.Current.VisualPrediction = !CoopSettings.Current.VisualPrediction;
            CoopPrediction.Clear(); CoopSettings.Save();
        }
        private HUDButton HostButton;
        private HUDButton InviteButton;
        private HUDButton ReportIssueButton;
        private HUDScrollContainer UIScrollContainer;
        private ScrollRect ScrollRect;
    }
}
