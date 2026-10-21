using Core.Localization;
using K4os.Compression.LZ4;
using Shapez2Multiplayer.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using static Shapez2Multiplayer.Packets.ChunkedPacket;

namespace Shapez2Multiplayer
{
    public class ShapezSocketManager
    {
        private List<ISocketManager> _socketManagers = new List<ISocketManager>();
        private System.Threading.Tasks.Task<byte[]>? snapshotPacking;
        private IConnection? snapshotPeer;
        public IReadOnlyCollection<ISocketManager> SocketManagers => _socketManagers;
        public IReadOnlyCollection<IConnection> Connected => _socketManagers.SelectMany(s => s.Connected).Where(CoopSession.IsAdmitted).ToArray();
        public List<IConnection> Connecting = new List<IConnection>();
        public List<Tuple<IPacket, IConnection>> BufferedRecievePackets = new List<Tuple<IPacket, IConnection>>();
        public List<IPacket> BufferedSendToAllPackets = new List<IPacket>();
        public List<Tuple<IPacket, IConnection>> BufferedSendToAllExceptPackets = new List<Tuple<IPacket, IConnection>>();
        public List<Tuple<IPacket, List<IConnection>>> BufferedSendToAllExceptListPackets = new List<Tuple<IPacket, List<IConnection>>>();
        public List<Tuple<IPacket, IConnection>> BufferedSendToPackets = new List<Tuple<IPacket, IConnection>>();
        public List<Tuple<IPacket, List<IConnection>>> BufferedSendToListPackets = new List<Tuple<IPacket, List<IConnection>>>();
        public List<Tuple<IPacket, IConnection>> BufferedSendToAllFromPackets = new List<Tuple<IPacket, IConnection>>();
        private long bufferedBytes;
        private void Buffer<T>(List<T> queue,T value,IPacket packet)
        {
            // These are latest-view hints; the normal refresh/ForceUpdateCursor republishes
            // current values after admission. Replaying old cursor frames only adds latency.
            if(packet is CursorPacket || packet is ViewportPropertyChangedPacket || packet is PlayerInteractionStateChangedPacket ||
                packet is UpdateBuildingMassSelectionPacket || packet is UpdateIslandMassSelectionPacket || packet is PlacementIndicatorDataPacket)return;
            int bytes=PacketExtensions.Encode(packet)?.Length??0;
            if(queue.Count>=256 || bytes>16L*1024*1024-bufferedBytes)throw new System.IO.InvalidDataException("Join UI backlog exceeded budget");
            queue.Add(value);bufferedBytes+=bytes;
        }
        public Dictionary<uint, OtherPlayerEntityPlacementDrawer> PlayersDrawers = new Dictionary<uint, OtherPlayerEntityPlacementDrawer>();
        public Dictionary<uint, OtherPlayerHUDBuildingMassSelection> PlayersBuildingMassSelections = new Dictionary<uint, OtherPlayerHUDBuildingMassSelection>();
        public Dictionary<uint, OtherPlayerHUDIslandMassSelection> PlayersIslandMassSelections = new Dictionary<uint, OtherPlayerHUDIslandMassSelection>();
        public static readonly List<Type> AlwaysAllowedToSend = new List<Type>()
        {
            typeof(CoopPacket), typeof(SavegamePacket),
            typeof(PausePacket),
            typeof(DisconnectReasonPacket),
            typeof(UpdateConnectionInfoPacket),
            typeof(ChunkedPacket),
            typeof(ChunkReceivedPacket),
            typeof(UniversalIDPacket)
        };
        public static readonly List<Type> AlwaysAllowedToRecieve = new List<Type>()
        {
            typeof(CoopPacket), typeof(FinishedConnectingPacket),
            typeof(PlayerInfoPacket),
            typeof(ChunkedPacket),
            typeof(ChunkReceivedPacket)
        };
        public ShapezSocketManager(ISocketManager socketManager)
        {
            CoopSession.StartHost();
            socketManager.ConnectedEvent += OnConnected;
            socketManager.DisconnectedEvent += OnDisconnected;
            socketManager.MessageEvent += OnMessage;
            lock (_socketManagers)
            {
                _socketManagers.Add(socketManager);
            }
        }
        public void AddSocketManager(ISocketManager socketManager)
        {
            socketManager.ConnectedEvent += OnConnected;
            socketManager.DisconnectedEvent += OnDisconnected;
            socketManager.MessageEvent += OnMessage;
            lock (_socketManagers)
            {
                _socketManagers.Add(socketManager);
            }
        }
        public void RemoveSocketManager(ISocketManager socketManager)
        {
            socketManager.Close();
            lock (_socketManagers)
            {
                _socketManagers.Remove(socketManager);
            }
        }
        public void OnConnected(IConnection connection) { CoopSession.BeginHandshake(connection); }
        public void Admit(IConnection connection)
        {
            Shapez2Multiplayer.logger.Info?.Log("Client connected: " + connection.UniversalId);
            HUDMultiplayerPausePanel.instance.AddPlayer(connection);

            PlayersDrawers.Add(connection.UniversalId, Shapez2Multiplayer.CreateOtherPlayerEntityPlacementDrawer());
            PlayersBuildingMassSelections.Add(connection.UniversalId, HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDBuildingMassSelection(connection));
            PlayersIslandMassSelections.Add(connection.UniversalId, HUDMultiplayerMassSelectionsHost.Instance.CreateOtherPlayerHUDIslandMassSelection(connection));
            if (!connection.Send(PacketExtensions.Encode(new UniversalIDPacket(connection.UniversalId)), Packets.Packet.UniversalID)) Shapez2Multiplayer.logger.Warning.Log($"Failed to send UniversalId Packet");
            SendToAll(new UpdateConnectionInfoPacket(new List<InfoConnection>() { new InfoConnection(connection) }, new List<uint>()));
            Connecting.Add(connection);
            if (Connecting.Count >= 1)
            {
                SendToAllExcept(new PausePacket(true, new CombinedText("multiplayer.paused-dialog.description-waitingforplayer".T(), new RawText("\n"+string.Join(", ", Connecting.Select(c => c.Name))))), Connecting);
                new PausePacket(true, new CombinedText("multiplayer.paused-dialog.description-waitingforplayer".T(), new RawText("\n" + string.Join(", ", Connecting.Select(c => c.Name))))).Handle(null);
            }
            var snapshot=CoopSimulation.Capture();
            CoopSession.SnapshotDigest=CoopSimulation.Digest();
            snapshotPeer=connection;
            snapshotPacking=SnapshotPacking.Start(snapshot,Shapez2Multiplayer.SavegameOptionsManager.Uid);
        }

        public void OnDisconnected(IConnection connection)
        {
            if (!CoopSession.IsAdmitted(connection)) { CoopSession.Remove(connection); return; }
            CoopSession.Remove(connection);
            Shapez2Multiplayer.logger.Info?.Log("Client disconnected: " + connection.UniversalId);
            HUDMultiplayerPausePanel.instance.RemovePlayer(connection);
            ChunkedPacket.Remove(connection);
            PlayersDrawers.Remove(connection.UniversalId);
            PlayersBuildingMassSelections.Remove(connection.UniversalId);
            PlayersIslandMassSelections.Remove(connection.UniversalId);
            HUDMultiplayerCursors.Instance.RemoveCursor(connection);
            SendToAll(new UpdateConnectionInfoPacket(new List<InfoConnection>(), new List<uint>() { connection.UniversalId }));
            if (Connecting.Remove(connection) && Connecting.Count == 0)
            {
                SendToAllExcept(new PausePacket(false), connection);
                new PausePacket(false).Handle(null);
            }
            Shapez2Multiplayer.HUD.Events.ShowNotification.Invoke(new HUDNotificationData(HUDNotificationType.Info, "multiplayer.player-lost-connection".T().Bind("player-name", new RawText(connection.Name))));
        }
        public void Disconnect(IConnection connection, MultiplayerCore.DisconnectReason? reason = null)
        {
            if (reason.HasValue)
            {
                SendTo(new DisconnectReasonPacket(reason.Value), connection);
                lock (_socketManagers)
                {
                    foreach (var socketManager in _socketManagers) socketManager.Update();
                }
            }
            connection.Close();
        }

        public void OnMessage(IConnection connection, byte[] data)
        {
            try
            {
                var decoded = NetworkBoundary.Unpack(data);
                var tag = PacketExtensions.PacketFromData(decoded);
                NetworkBoundary.ValidateClientTag(tag, connection);
                var packet = PacketExtensions.Decode(decoded, out var from);
                if (from.HasValue) throw new System.IO.InvalidDataException("Forged sender");
                if (Connecting.Count > 0 && !AlwaysAllowedToRecieve.Contains(packet.GetType())) return;
                packet.Handle(connection);
            }
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Warning?.Log("Rejected peer packet: " + ex.Message);
                connection.Close();
            }
        }        public bool SendToAll(IPacket packet)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                Buffer(BufferedSendToAllPackets,packet,packet);
                return true;
            }
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return true;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            bool success = true;
            foreach (var connection in Connected)
            {
                success = connection.Send(encoded, type) && success;
            }
            return success;
        }
        public void SendToAllExcept(IPacket packet, IConnection excluded)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                Buffer(BufferedSendToAllExceptPackets,new Tuple<IPacket,IConnection>(packet,excluded),packet);
                return;
            }
            if (!Connected.Any(connection => connection != excluded)) return;
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            foreach (var connection in Connected)
            {
                if (connection == excluded) continue;
                if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
            }
        }
        public void SendToAllFrom(IPacket packet, IConnection from)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                Buffer(BufferedSendToAllFromPackets,new Tuple<IPacket,IConnection>(packet,from),packet);
                return;
            }
            if (!Connected.Any(connection => connection != from)) return;
            var encoded = PacketExtensions.Encode(packet, from.UniversalId);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            foreach (var connection in Connected)
            {
                if (connection == from) continue;
                if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
            }
        }
        public void SendToAllExcept(IPacket packet, List<IConnection> excluded)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                Buffer(BufferedSendToAllExceptListPackets,new Tuple<IPacket,List<IConnection>>(packet,excluded),packet);
                return;
            }
            if (!Connected.Any(connection => !excluded.Contains(connection))) return;
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            foreach (var connection in Connected)
            {
                if (excluded.Contains(connection)) continue;
                if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
            }
        }
        public void SendTo(IPacket packet, IConnection connection)
        {
            if (!Connected.Contains(connection) && !Connecting.Contains(connection)) return;
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                Buffer(BufferedSendToPackets,new Tuple<IPacket,IConnection>(packet,connection),packet);
                return;
            }
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
        }
        public void SendTo(IPacket packet, List<IConnection> connections)
        {
            if (Connecting.Count > 0 && !AlwaysAllowedToSend.Contains(packet.GetType()))
            {
                Buffer(BufferedSendToListPackets,new Tuple<IPacket,List<IConnection>>(packet,connections),packet);
                return;
            }
            var encoded = PacketExtensions.Encode(packet);
            if (encoded == null) return;
            var type = PacketExtensions.GetFromType(packet.GetType());
            if (packet is SendToAllPacket sendToAllPacket) type = PacketExtensions.GetFromType(sendToAllPacket.Packet.GetType());
            foreach (var connection in connections)
            {
                if (!Connected.Contains(connection) && !Connecting.Contains(connection)) continue;
                if (!connection.Send(encoded, type)) Shapez2Multiplayer.logger.Warning.Log($"Dropped packet {packet.GetType().Name} to {connection.Name} because send failed");
            }
        }
        public void ForceUpdateCursor()
        {
            SyncCursorTimer = 0.0f;
            var cursorState = Shapez2Multiplayer.GameCursorManager._State;
            if (ScreenUtils.TryGetWorldCoordinate(Shapez2Multiplayer.GameSessionOrchestrator.Viewport, Shapez2Multiplayer.GameSessionOrchestrator.Viewport.CursorScreenPosition, out var cursorWorldPosition))
            {
                LastCursorState = cursorState;
                LastCursorWorldPosition = (float3)cursorWorldPosition;
                SendToAll(new CursorPacket((float3)cursorWorldPosition, cursorState));
            }
            var viewportIslandLayer = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.IslandLayer;
            var viewportBuildingLayer = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.BuildingLayer;
            var viewportShowAllBuildingLayers = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.ShowAllBuildingLayers;
            var viewportShowAllIslandLayers = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.ShowAllIslandLayers;
            LastViewportIslandLayer = viewportIslandLayer;
            LastViewportBuildingLayer = viewportBuildingLayer;
            LastViewportShowAllBuildingLayers = viewportShowAllBuildingLayers;
            LastViewportShowAllIslandLayers = viewportShowAllIslandLayers;
            SendToAll(new ViewportPropertyChangedPacket(viewportIslandLayer, viewportBuildingLayer, viewportShowAllBuildingLayers, viewportShowAllIslandLayers));
            SendToAll(new PlayerInteractionStateChangedPacket(Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.State));
        }
        public float PingUpdateTimer = 0.0f;
        public float SyncResearchTimer = 0.0f;
        float MassSelectionsTimer = 0.0f;
        float SyncLobbyDataTimer = 0.0f;
        float SyncCursorTimer = 0.0f;
        const float PING_UPDATE_TIME = 5.0f;
        const float SYNC_RESEARCH_TIME = 60.0f * 3f;
        const float SYNC_MASS_SELECTIONS_TIME = 1.0f;
        const float SYNC_LOBBY_DATA_TIME = 60.0f * 5f;
        const float SYNC_CURSOR_TIME = 0.1f;
        CursorHoverState? LastCursorState;
        float3? LastCursorWorldPosition;
        short? LastViewportIslandLayer;
        short? LastViewportBuildingLayer;
        bool? LastViewportShowAllBuildingLayers;
        bool? LastViewportShowAllIslandLayers;
        public void Update()
        {
            if(snapshotPacking!=null && snapshotPacking.IsCompleted)
            {
                var pending=snapshotPacking;var peer=snapshotPeer!;snapshotPacking=null;snapshotPeer=null;
                try
                {
                    var bytes=pending.GetAwaiter().GetResult();
                    if(CoopSession.IsAdmitted(peer) && Connecting.Contains(peer) && !peer.Send(bytes,Packet.Savegame))peer.Close();
                }
                catch(Exception ex){Shapez2Multiplayer.logger.Warning?.Log("Snapshot packing failed: "+ex.Message);peer.Close();}
            }
            lock (_socketManagers)
            {
                foreach (var sm in _socketManagers.ToList()) // the lock will not work for some reason so just use a copy
                {
                    if (sm.Valid) sm.Update();
                }
            }
            PingUpdateTimer += Time.deltaTime;
            if (PingUpdateTimer >= PING_UPDATE_TIME)
            {
                PingUpdateTimer = 0.0f;
                SendToAll(new UpdateConnectionInfoPacket(Connected.Select(c => new InfoConnection(c)).ToList(), new List<uint>()));
            }
            SyncResearchTimer += Time.deltaTime;
            if (!CoopSession.Active && SyncResearchTimer >= SYNC_RESEARCH_TIME)
            {
                SyncResearchTimer = 0.0f;
                SendToAll(new SyncResearchManagerPacket(Shapez2Multiplayer.Research));
            }
            MassSelectionsTimer += Time.deltaTime;
            if (MassSelectionsTimer >= SYNC_MASS_SELECTIONS_TIME)
            {
                MassSelectionsTimer = 0.0f;
                SendToAll(new UpdateBuildingMassSelectionPacket(Shapez2Multiplayer.HUDBuildingMassSelection, Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.BuildingSelection.ToList()));
                SendToAll(new UpdateIslandMassSelectionPacket(Shapez2Multiplayer.HUDIslandMassSelection, Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer.InteractionState.IslandSelection.ToList()));
            }
            SyncLobbyDataTimer += Time.deltaTime;
            if (SyncLobbyDataTimer >= SYNC_LOBBY_DATA_TIME)
            {
                SyncLobbyDataTimer = 0.0f;
                MultiplayerCore.RefreshLobbyData();
            }
            SyncCursorTimer += Time.deltaTime;
            if (SyncCursorTimer >= SYNC_CURSOR_TIME)
            {
                SyncCursorTimer = 0.0f;
                var cursorState = Shapez2Multiplayer.GameCursorManager._State;
                if (ScreenUtils.TryGetWorldCoordinate(Shapez2Multiplayer.GameSessionOrchestrator.Viewport, Shapez2Multiplayer.GameSessionOrchestrator.Viewport.CursorScreenPosition, out var cursorWorldPosition) && (cursorState != LastCursorState || !((float3)cursorWorldPosition).Equals(LastCursorWorldPosition)))
                {
                    LastCursorState = cursorState;
                    LastCursorWorldPosition = (float3)cursorWorldPosition;
                    SendToAll(new CursorPacket((float3)cursorWorldPosition, cursorState));
                }
                var viewportIslandLayer = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.IslandLayer;
                var viewportBuildingLayer = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.BuildingLayer;
                var viewportShowAllBuildingLayers = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.ShowAllBuildingLayers;
                var viewportShowAllIslandLayers = Shapez2Multiplayer.GameSessionOrchestrator.Viewport.ShowAllIslandLayers;
                if (viewportIslandLayer != LastViewportIslandLayer || viewportBuildingLayer != LastViewportBuildingLayer || viewportShowAllBuildingLayers != LastViewportShowAllBuildingLayers || viewportShowAllIslandLayers != LastViewportShowAllIslandLayers)
                {
                    LastViewportIslandLayer = viewportIslandLayer;
                    LastViewportBuildingLayer = viewportBuildingLayer;
                    LastViewportShowAllBuildingLayers = viewportShowAllBuildingLayers;
                    LastViewportShowAllIslandLayers = viewportShowAllIslandLayers;
                    SendToAll(new ViewportPropertyChangedPacket(viewportIslandLayer, viewportBuildingLayer, viewportShowAllBuildingLayers, viewportShowAllIslandLayers));
                }
            }
            if (Connecting.Count > 0) return;
            foreach (var packet in BufferedRecievePackets)
            {
                packet.Item1.Handle(packet.Item2);
            }
            BufferedRecievePackets.Clear();
            foreach (var packet in BufferedSendToAllPackets)
            {
                SendToAll(packet);
            }
            BufferedSendToAllPackets.Clear();
            foreach (var packet in BufferedSendToAllExceptPackets)
            {
                SendToAllExcept(packet.Item1, packet.Item2);
            }
            BufferedSendToAllExceptPackets.Clear();
            foreach (var packet in BufferedSendToAllExceptListPackets)
            {
                SendToAllExcept(packet.Item1, packet.Item2);
            }
            BufferedSendToAllExceptListPackets.Clear();
            foreach (var packet in BufferedSendToPackets)
            {
                SendTo(packet.Item1, packet.Item2);
            }
            BufferedSendToPackets.Clear();
            foreach (var packet in BufferedSendToListPackets)
            {
                SendTo(packet.Item1, packet.Item2);
            }
            BufferedSendToListPackets.Clear();
            foreach (var packet in BufferedSendToAllFromPackets)
            {
                SendToAllFrom(packet.Item1, packet.Item2);
            }
            BufferedSendToAllFromPackets.Clear();
            bufferedBytes=0;
        }
    }
}
