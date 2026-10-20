using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Shapez2Coop.Protocol;
using Shapez2Multiplayer.Packets;
using UnityEngine;

namespace Shapez2Multiplayer
{
    // All methods run on Unity's main thread. Commands and steps share the same ordered channel.
    public static class CoopSession
    {
        private sealed class PeerState
        {
            public double Deadline;
            public bool Admitted;
            public ulong LastRequest;
        }
        private static readonly Dictionary<IConnection, PeerState> Peers = new Dictionary<IConnection, PeerState>();
        private static readonly Queue<(IConnection? Peer, Frame Frame)> Inbox = new Queue<(IConnection?, Frame)>();
        private static Guid Epoch;
        private static ulong Revision, RequestId, Step;
        public static ulong StepCount => Step;
        public static ulong EditRevision => Revision;
        private static double NextStep;
        private static bool Welcomed;
        public static bool Applying { get; private set; }
        public static bool ReplicaWorld;
        public static GameSessionOrchestrator? ReplicaSession;
        public static bool Faulted { get; private set; }
        public static string Status { get; private set; } = "Offline";
        public static bool Active => MultiplayerCore.Hosting || MultiplayerCore.Client;
        public static bool Ready => Active && !Faulted && (MultiplayerCore.Hosting
            ? MultiplayerCore.socketManager.Connecting.Count == 0
            : Welcomed && MultiplayerCore.connectionManager.FinishedConnecting);
        private static double Now => Time.realtimeSinceStartupAsDouble;
        // Game assembly MVIDs detect binary differences, including builds sharing a version label.
        private static string Compatibility => "coop-alpha-3|" + GameEnvironmentManager.Version + "|" +
            typeof(CoopSession).Assembly.ManifestModule.ModuleVersionId + "|" +
            typeof(GameSessionOrchestrator).Assembly.ManifestModule.ModuleVersionId + "|" +
            typeof(PlayerActionManager).Assembly.ManifestModule.ModuleVersionId;

        public static void Reset()
        {
            Peers.Clear(); Inbox.Clear(); Epoch = Guid.Empty;
            Revision = RequestId = Step = 0; Welcomed = Applying = Faulted = false;
            Status = "Offline"; NextStep = 0;
        }
        public static void StartHost()
        {
            Reset(); ReplicaWorld = false; ReplicaSession = null; Epoch = Guid.NewGuid(); Status = "Hosting experimental co-op";
        }
        public static bool BeginHandshake(IConnection peer)
        {
            if (Peers.Count >= 3) { peer.Close(); return false; }
            Peers.Add(peer, new PeerState { Deadline = Now + 15 });
            return true;
        }
        public static void ConnectedToHost()
        {
            Reset(); Epoch = Guid.NewGuid(); Status = "Checking compatibility";
            SendHost(new Frame(FrameKind.Hello, Epoch, 0, 0, System.Text.Encoding.UTF8.GetBytes(Compatibility)));
        }
        public static void Remove(IConnection peer) => Peers.Remove(peer);
        public static bool IsAdmitted(IConnection peer) => Peers.TryGetValue(peer, out var state) && state.Admitted;
        private static void Send(IConnection peer, Frame frame)
        {
            if (!peer.Send(PacketExtensions.Encode(new CoopPacket(frame)), Packet.Coop))
                throw new IOException("Reliable co-op send failed");
        }
        private static void SendHost(Frame frame) => Send(MultiplayerCore.connectionManager.ConnectionManager.Connection, frame);
        private static void Broadcast(Frame frame)
        {
            foreach (var peer in Peers.Where(p => p.Value.Admitted).Select(p => p.Key).ToArray())
                Send(peer, frame);
        }
        public static void Receive(IConnection? peer, Frame frame)
        {
            if (Inbox.Count >= 256) throw new InvalidDataException("Co-op receive queue exhausted");
            Inbox.Enqueue((peer, frame));
        }
        public static bool Submit(IPlayerAction action)
            => SubmitEncoded(PacketExtensions.Encode(new PlayerActionPacket(action)));
        public static bool SubmitEncoded(byte[] data)
        {
            if (!Ready) return false;
            if (data == null || data.Length > 128 * 1024) return false;
            var frame = new Frame(FrameKind.Intent, Epoch, ++RequestId, Revision, data);
            if (MultiplayerCore.Hosting) Receive(null, frame); else SendHost(frame);
            return true;
        }
        private static bool Execute(byte[] data)
        {
            if (PacketExtensions.PacketFromData(data) == Packet.UpdateBuildingConfiguration)
            {
                var configuration = (UpdateBuildingConfigurationPacket)PacketExtensions.Decode(data);
                if (!Shapez2Multiplayer.MapModel.TryGetBuilding(configuration.TileCoordinate, out _)) return false;
                configuration.Handle(null);
                return true;
            }
            if (PacketExtensions.PacketFromData(data) != Packet.PlayerAction)
                throw new InvalidDataException("Unsupported world mutation");
            if (!(PacketExtensions.Decode(data, out var from) is PlayerActionPacket command) || from.HasValue || command.PlayerAction == null)
                throw new InvalidDataException("Invalid action command");
            var action = command.PlayerAction;
            if (!action.IsPossible(Shapez2Multiplayer.InteractionMode)) return false;
            Applying = true;
            try { return Shapez2Multiplayer.PlayerActions.ExecuteActionImmediately_INTERNAL(action, out _); }
            finally { Applying = false; }
        }
        public static void Update()
        {
            if (!Active || Faulted) return;
            try
            {
                foreach (var peer in Peers.Where(p => p.Value.Deadline < Now).Select(p => p.Key).ToArray())
                {
                    Peers[peer].Deadline = double.PositiveInfinity; peer.Close();
                }
                // Join packets are pure data; state mutations wait for the engine's simulation barrier.
                Shapez2Multiplayer.GameSessionOrchestrator?.FinalizeLogicUpdate();
                int budget = 256;
                while (Inbox.Count > 0 && budget-- > 0)
                {
                    var item = Inbox.Dequeue();
                    Handle(item.Peer, item.Frame);
                }
                if (MultiplayerCore.Hosting && Ready && Now >= NextStep)
                {
                    NextStep = Now + 0.05;
                    CoopSimulation.Advance();
                    Broadcast(new Frame(FrameKind.SimulationStep, Epoch, 0, ++Step, BitConverter.GetBytes(Revision)));
                    if (Step % 100 == 0)
                    {
                        var digest = CoopSimulation.Digest();
                        Broadcast(new Frame(FrameKind.StateDigest, Epoch, 0, Step, digest));
#if COOP_QA
                        if (Peers.Any(p => p.Value.Admitted))
                            Debug.Log("QA:CHECKSUM_HOST step=" + Step + " digest=" + BitConverter.ToString(digest));
#endif
                    }
                }
            }
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Error?.Log(ex.ToString());
                Fail(ex.Message);
            }
        }
        private static void Handle(IConnection? peer, Frame frame)
        {
            if (MultiplayerCore.Hosting && frame.Kind == FrameKind.Hello)
            {
                if (peer == null || !Peers.TryGetValue(peer, out var state) || state.Admitted)
                    throw new InvalidDataException("Unexpected hello");
                if (System.Text.Encoding.UTF8.GetString(frame.Payload.ToArray()) != Compatibility)
                { Peers.Remove(peer); peer.Close(); return; }
                Broadcast(new Frame(FrameKind.SnapshotReady, Epoch, 0, Step, Array.Empty<byte>()));
                state.Admitted = true; state.Deadline = Now + 120;
                var boundary = BitConverter.GetBytes(Step);
                Send(peer, new Frame(FrameKind.Welcome, Epoch, 0, Revision, boundary));
                MultiplayerCore.socketManager.Admit(peer);
                return;
            }
            if (MultiplayerCore.Client && frame.Kind == FrameKind.Welcome && !Welcomed)
            {
                if (frame.Payload.Length != 8) throw new InvalidDataException("Invalid snapshot boundary");
                Epoch = frame.Epoch; Revision = frame.Sequence; Step = BitConverter.ToUInt64(frame.Payload.ToArray(), 0);
                Welcomed = true; Status = "Loading host snapshot"; return;
            }
            if (frame.Epoch != Epoch) throw new InvalidDataException("Stale session epoch");
            if (MultiplayerCore.Hosting)
            {
                if (peer != null && !IsAdmitted(peer)) throw new InvalidDataException("Peer not admitted");
                if (frame.Kind == FrameKind.Intent)
                {
                    if (peer != null)
                    {
                        var state = Peers[peer];
                        if (frame.RequestId <= state.LastRequest) return;
                        if (frame.RequestId != state.LastRequest + 1) throw new InvalidDataException("Request sequence gap");
                        state.LastRequest = frame.RequestId;
                    }
                    // Conservative optimistic concurrency: reject any intent created before a committed edit.
                    // Resolve the command against current game objects ONLY after checking its revision.
                    bool accepted = false;
                    if (Ready && frame.Sequence == Revision)
                    {
                        try { accepted = Execute(frame.Payload.ToArray()); }
                        catch (InvalidDataException) { accepted = false; }
                    }
                    if (accepted)
                        Broadcast(new Frame(FrameKind.Commit, Epoch, frame.RequestId, ++Revision, frame.Payload.Span));
                    else if (peer != null)
                        Send(peer, new Frame(FrameKind.Reject, Epoch, frame.RequestId, Revision, Array.Empty<byte>()));
                    else Status = "Edit rejected: world changed; retry";
                    return;
                }
                if (frame.Kind == FrameKind.StepAck && peer != null)
                {
                    if (frame.Sequence > Step) throw new InvalidDataException("Invalid step acknowledgement");
                    Peers[peer].Deadline = Now + 30; return;
                }
                throw new InvalidDataException("Client sent a host-only message");
            }
            if (!Welcomed) throw new InvalidDataException("Missing welcome");
            switch (frame.Kind)
            {
                case FrameKind.Commit:
                    if (frame.Sequence != Revision + 1 || !Execute(frame.Payload.ToArray()))
                        throw new InvalidDataException("World command diverged; reconnect required");
                    Revision = frame.Sequence; Status = "Connected"; break;
                case FrameKind.Reject:
                    Status = "Edit rejected: another player changed the world; retry"; break;
                case FrameKind.StateDigest:
                    if (frame.Sequence != Step || !frame.Payload.Span.SequenceEqual(CoopSimulation.Digest()))
                        throw new InvalidDataException("Production state checksum mismatch");
#if COOP_QA
                    Debug.Log("QA:CHECKSUM_CLIENT step=" + Step + " digest=" + BitConverter.ToString(frame.Payload.ToArray()));
#endif
                    break;
                case FrameKind.SnapshotReady:
                    if (frame.Sequence != Step) throw new InvalidDataException("Snapshot preparation boundary mismatch");
                    CoopSimulation.PrepareSnapshot();
                    CoopSimulation.Digest(); // Apply the same signal-channel serialization maintenance as host capture.
                    break;
                case FrameKind.SimulationStep:
                    if (!Ready || frame.Sequence != Step + 1 || frame.Payload.Length != 8 ||
                        BitConverter.ToUInt64(frame.Payload.ToArray(), 0) != Revision)
                        throw new InvalidDataException("Simulation step gap; reconnect required");
                    CoopSimulation.Advance(); Step = frame.Sequence;
                    if (Step % 20 == 0) SendHost(new Frame(FrameKind.StepAck, Epoch, 0, Step, Array.Empty<byte>()));
                    Status = "Connected / step " + Step; break;
                default: throw new InvalidDataException("Unexpected host message");
            }
        }
        public static void FinishedJoining(IConnection peer)
        {
            if (!IsAdmitted(peer) || !MultiplayerCore.socketManager.Connecting.Contains(peer))
                throw new InvalidDataException("Unexpected snapshot acknowledgement");
            Peers[peer].Deadline = Now + 30;
        }
        public static void Fail(string reason)
        {
            Faulted = true; Status = "Stopped: " + reason + ". Reconnect to reload host state.";
            Shapez2Multiplayer.logger.Error?.Log(Status);
            foreach (var peer in Peers.Keys.ToArray()) peer.Close();
            if (MultiplayerCore.Client) MultiplayerCore.connectionManager.ConnectionManager.Connection.Close();
        }
    }

    [HarmonyPatch(typeof(PlayerActionManager), nameof(PlayerActionManager.ScheduleUndo))]
    internal static class CoopDisableUndo
    {
        [HarmonyPrefix] private static bool Prefix() => !CoopSession.Active;
    }
    [HarmonyPatch(typeof(PlayerActionManager), nameof(PlayerActionManager.ScheduleRedo))]
    internal static class CoopDisableRedo
    {
        [HarmonyPrefix] private static bool Prefix() => !CoopSession.Active;
    }
}
