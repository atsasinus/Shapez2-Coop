using Core.Localization;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using Game.Core.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using static Shapez2Multiplayer.MultiplayerCore;

namespace Shapez2Multiplayer.Packets
{
    public class SavegamePacket : IPacket
    {
        public IReadOnlyDictionary<string, byte[]> Savegame { set; private get; }
        public string Uid { get; set; }
        public MemoryStream SavegameEncoded { private set; get; }
        public SavegamePacket() { }
        public SavegamePacket(IReadOnlyDictionary<string, byte[]> savegame, string uid)
        {
            Savegame = savegame;
            Uid = uid;
        }
        public void Decode(Stream stream)
        {
            int UidLength = stream.ReadByte();
            byte[] UidBytes = new byte[UidLength];
            stream.Read(UidBytes, 0, UidLength);
            Uid = UTF8Encoding.UTF8.GetString(UidBytes);
            Shapez2Multiplayer.logger.Info?.Log($"Recieved savegame packet with Uid: {Uid}");
            SavegameEncoded = new MemoryStream();
            stream.CopyTo(SavegameEncoded);
        }

        public bool Encode(Stream stream)
        {
            var UidBytes = UTF8Encoding.UTF8.GetBytes(Uid);
            stream.WriteByte((byte)UidBytes.Length);
            stream.Write(UidBytes);
            Shapez2Multiplayer.WriteToStream(stream, Savegame);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (!MultiplayerCore.Client || MultiplayerCore.connectionManager.FinishedConnecting ||
                !Shapez2Multiplayer.Game.IsGameInMainMenu() || CoopSession.ReplicaWorld)
                throw new InvalidDataException("Unexpected world snapshot");
            _ = LoadSnapshot(MultiplayerCore.connectionManager);
        }

        private async Task LoadSnapshot(ShapezConnectionManager joining)
        {
            CoopSession.ReplicaWorld = true;
            try
            {
                MultiplayerCore.ConnectingDialog?.OnClosed.Unregister(MultiplayerCore.DialogClosed);
                MultiplayerCore.ConnectingDialog?.Close();
                MultiplayerCore.ConnectingDialog = null;
                IReadOnlyDictionary<Type, IDataSerializer> dataSerializers = Shapez2Multiplayer.MainMenuOrchestratorBackgroundGameOrchestrator.DataSerializers;
                SavegameEncoded.Position = 0;
                SavegameBlobReader savegameBlobReader = new SaveFileAccessor(Shapez2Multiplayer.MainMenuOrchestratorLogger).ReadFromStream(SavegameEncoded, dataSerializers);
                // The normal navigator owns its loading screen. A menu fade may return null
                // while another transition is active, so never make loading depend on it.
                // A unique local UID additionally prevents collision with a player's saves.
                await Shapez2Multiplayer.GameOrchestrator.LoadSession(new GameStartOptionsContinueExisting(
                    savegameBlobReader, false, "coop-replica-" + Guid.NewGuid().ToString("N"), false));
                CoopSession.ReplicaSession = Shapez2Multiplayer.GameSessionOrchestrator;
                if (!ReferenceEquals(joining, MultiplayerCore.connectionManager))
                {
                    await Shapez2Multiplayer.GameOrchestrator.LoadMainMenu();
                    return;
                }
                if (CoopSession.ReplicaSession == null) throw new InvalidOperationException("Snapshot world failed to load");
                    joining.HostDrawer = Shapez2Multiplayer.CreateOtherPlayerEntityPlacementDrawer();
                    joining.HostBuildingMassSelection = HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDBuildingMassSelection(null);
                    joining.HostIslandMassSelection = HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDIslandMassSelection(null);
                    Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.HUDData.Pins.OnPinAdded.Register(MultiplayerEvents.OnPinAdded);
                    Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.HUDData.Pins.OnPinRemoved.Register(MultiplayerEvents.OnPinRemoved);
                    ((IEntityPlacementStateController)Shapez2Multiplayer.EntityPlacementRunner).OnPlacementDataChanged.Register(MultiplayerEvents.OnPlacementDataChanged);
                    Shapez2Multiplayer.Research.PlayerLevelGoals.OnLeveledUp.Register(MultiplayerEvents.OnResearchPlayerLevelGoalManagerLeveledUpClient);
                    Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.OnStateChanged.Register(MultiplayerEvents.OnPlayerInteractionStateChanged);
                    Shapez2Multiplayer.PlayerWaypoints.OnWaypointAdded.Register(MultiplayerEvents.OnWaypointAdded);
                    Shapez2Multiplayer.PlayerWaypoints.OnWaypointChanged.Register(MultiplayerEvents.OnWaypointChanged);
                    Shapez2Multiplayer.PlayerWaypoints.OnWaypointRemoved.Register(MultiplayerEvents.OnWaypointRemoved);
                    joining.FinishedConnecting = true;
                    foreach (var connection in joining.Connections)
                    {
                        if (connection.UniversalId != MultiplayerCore.connectionManager.UniversalId)
                        {
                            MultiplayerCore.connectionManager.PlayersDrawers.Add(connection.UniversalId, Shapez2Multiplayer.CreateOtherPlayerEntityPlacementDrawer());
                            MultiplayerCore.connectionManager.PlayersBuildingMassSelections.Add(connection.UniversalId, HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDBuildingMassSelection(connection));
                            MultiplayerCore.connectionManager.PlayersIslandMassSelections.Add(connection.UniversalId, HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDIslandMassSelection(connection));
                        }
                    }
                    joining.Send(new FinishedConnectingPacket(CoopSession.StepCount, CoopSession.EditRevision, CoopSimulation.Digest()));
            }
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Error?.Log(ex.ToString());
                CoopSession.Fail("Snapshot loading failed: " + ex.Message);
            }
            finally { SavegameEncoded.Dispose(); }
        }
    }
}
