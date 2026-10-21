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
            public bool WaitingSnapshot;
            public ulong LastRequest;
            public ulong AcknowledgedStep;
        }
        private static readonly Dictionary<IConnection, PeerState> Peers = new Dictionary<IConnection, PeerState>();
        private static readonly ByteBudgetQueue<(IConnection? Peer, Frame? Frame, Action? Control)> Inbox = new ByteBudgetQueue<(IConnection?,Frame?,Action?)>(256,16L*1024*1024);
        private static Guid Epoch;
        private static ulong Revision, RequestId, Step;
        public static ulong StepCount => Step;
        public static ulong EditRevision => Revision;
        public static ulong SnapshotStep { get; private set; }
        public static uint LocalPlayerId { get; private set; }
        public static int SimulationHz { get; private set; } = 20;
        private static int? RequestedRate;
        private static long? NextDigestTime;
        private static int NextDigestPartition;
        private static int PendingDigestParts;
        private static double NextHeartbeat;
        public static byte[]? SnapshotDigest {get;internal set;}
#if COOP_QA
        public static int QaIntentDelayMs = 200;
        private static readonly Queue<(Frame Frame, double Due)> DelayedIntents = new Queue<(Frame, double)>();
#endif
        private static double NextStep;
        private static bool Welcomed;
        public static bool ClientPausedForJoin;
        public static bool Applying { get; private set; }
        public static bool ReplicaWorld;
        public static GameSessionOrchestrator? ReplicaSession;
        public static bool Faulted { get; private set; }
        public static string Status { get; private set; } = "Offline";
        public static bool Active => MultiplayerCore.Hosting || MultiplayerCore.Client;
        public static bool Ready => Active && !Faulted && (MultiplayerCore.Hosting
            ? MultiplayerCore.socketManager.Connecting.Count == 0
            : Welcomed && MultiplayerCore.connectionManager.FinishedConnecting && !ClientPausedForJoin);
        private static double Now => Time.realtimeSinceStartupAsDouble;
        internal static bool CanReceive => Inbox.Count<192 && Inbox.BufferedBytes<12L*1024*1024;
        // Game assembly MVIDs detect binary differences, including builds sharing a version label.
        private static string Compatibility => "coop-alpha-4|" + GameEnvironmentManager.Version + "|" +
            typeof(CoopSession).Assembly.ManifestModule.ModuleVersionId + "|" +
            typeof(GameSessionOrchestrator).Assembly.ManifestModule.ModuleVersionId + "|" +
            typeof(PlayerActionManager).Assembly.ManifestModule.ModuleVersionId;

        public static void Reset()
        {
            Peers.Clear(); Inbox.Clear(); Epoch = Guid.Empty;
            Revision = RequestId = Step = SnapshotStep = 0; Welcomed = Applying = Faulted = ClientPausedForJoin = false;
            Status = "Offline"; NextStep = 0;
            LocalPlayerId = 0; SimulationHz = 20; RequestedRate = null; NextDigestTime = null;
            NextDigestPartition=PendingDigestParts=0;NextHeartbeat=0;SnapshotDigest=null;CoopSimulation.ResetDigestIndex();
            CoopPrediction.Clear();
#if COOP_QA
            DelayedIntents.Clear();
#endif
        }
        public static void StartHost()
        {
            Reset(); ReplicaWorld = false; ReplicaSession = null; Epoch = Guid.NewGuid(); Status = "Hosting experimental co-op";
            ApplyRate(CoopSettings.Current.SimulationHz);
        }
        public static bool BeginHandshake(IConnection peer)
        {
            if (Peers.Count >= 3) { peer.Close(); return false; }
            Peers.Add(peer, new PeerState { Deadline = Now + 15 });
            return true;
        }
        public static void ConnectedToHost()
        {
            Reset(); ClientPausedForJoin = true; Epoch = Guid.NewGuid(); Status = "Checking compatibility";
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
            if(!Inbox.TryEnqueue((peer,frame,null),frame.Payload.Length+64))throw new InvalidDataException("Co-op receive queue exhausted");
        }
        public static void ReceiveControl(Action control,int bytes)
        {if(!Inbox.TryEnqueue((null,null,control),checked(bytes+64)))throw new InvalidDataException("Co-op control queue exhausted");}
        public static bool Submit(IPlayerAction action)
        {
            if (!Ready) return false;
            try { return SubmitAtRevision(PacketExtensions.Encode(new PlayerActionPacket(action)), Revision, action); }
            catch (Exception ex)
            { Status = "Edit could not be submitted"; Shapez2Multiplayer.logger.Warning?.Log(ex.Message); return false; }
        }
        public static bool SubmitEncoded(byte[] data)
            => SubmitAtRevision(data, Revision);
#if COOP_QA
        public static bool QaSubmitAtRevision(byte[] data, ulong observedRevision)
        {
            IPlayerAction? preview = null;
            if (PacketExtensions.PacketFromData(data) == Packet.PlayerAction)
                preview = ((PlayerActionPacket)PacketExtensions.Decode(data)).PlayerAction;
            return SubmitAtRevision(data, observedRevision, preview);
        }
#endif
        private static bool SubmitAtRevision(byte[] data, ulong observedRevision, IPlayerAction? preview = null)
        {
            if (!Ready) return false;
            if (data == null || data.Length == 0 || data.Length > SessionPayloads.MaxCommandBytes) return false;
#if COOP_QA
            if (MultiplayerCore.Client && DelayedIntents.Count >= 64) return false;
#endif
            var frame = new Frame(FrameKind.Intent, Epoch, ++RequestId, observedRevision, data);
            CoopPrediction.Track(RequestId, preview);
            if (MultiplayerCore.Hosting) Receive(null, frame);
            else
            {
#if COOP_QA
                if (QaIntentDelayMs > 0) DelayedIntents.Enqueue((frame, Now + QaIntentDelayMs / 1000.0));
                else
#endif
                    SendHost(frame);
            }
            return true;
        }
        private static bool Execute(byte[] data)
        {
            if (data.Length == 0 || data.Length > SessionPayloads.MaxCommandBytes) throw new InvalidDataException("World command exceeds limit");
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
            try
            {
                bool accepted = Shapez2Multiplayer.PlayerActions.ExecuteActionImmediately_INTERNAL(action, out _);
#if COOP_QA
                CoopQaProduction.ObserveClear(action, accepted);
#endif
                return accepted;
            }
            finally { Applying = false; }
        }
        public static void Update()
        {
            if (!Active || Faulted) return;
            try
            {
#if COOP_QA
                if (MultiplayerCore.Client && Ready)
                    while (DelayedIntents.Count > 0 && DelayedIntents.Peek().Due <= Now)
                        SendHost(DelayedIntents.Dequeue().Frame);
#endif
                // Join packets are pure data; state mutations wait for the engine's simulation barrier.
                Shapez2Multiplayer.GameSessionOrchestrator?.FinalizeLogicUpdate();
                int budget = 256;
                long inboxStarted=System.Diagnostics.Stopwatch.GetTimestamp();
                while (budget-- > 0 && Inbox.TryDequeue(out var item))
                {
                    if(item.Peer!=null && !Peers.ContainsKey(item.Peer))continue;
                    if(item.Control!=null)item.Control();else Handle(item.Peer, item.Frame!);
                    if((System.Diagnostics.Stopwatch.GetTimestamp()-inboxStarted)*1000.0/System.Diagnostics.Stopwatch.Frequency>=Math.Max(4,Math.Min(12,Time.unscaledDeltaTime*400)))break;
                }
                foreach(var peer in Peers.Where(p=>p.Value.Deadline<Now).Select(p=>p.Key).ToArray())
                {
                    Shapez2Multiplayer.logger.Warning?.Log("Peer deadline expired: admitted="+Peers[peer].Admitted+" waiting="+Peers[peer].WaitingSnapshot+" step="+Step);
                    Peers[peer].Deadline=double.PositiveInfinity;peer.Close();
                }
                if(MultiplayerCore.Client && Ready && Now>=NextHeartbeat)
                {SendHost(new Frame(FrameKind.StepAck,Epoch,0,Step,Array.Empty<byte>()));NextHeartbeat=Now+1;}
                if (MultiplayerCore.Hosting && MultiplayerCore.socketManager.Connecting.Count == 0)
                {
                    var waiting = Peers.FirstOrDefault(p => p.Value.WaitingSnapshot);
                    if (waiting.Key != null && !SnapshotPacking.Busy) BeginSnapshot(waiting.Key, waiting.Value);
                }
                if (MultiplayerCore.Hosting && Ready && RequestedRate.HasValue)
                {
                    int rate = RequestedRate.Value; RequestedRate = null;
                    Broadcast(new Frame(FrameKind.SimulationRate, Epoch, 0, Step, SessionPayloads.Step(Revision, rate)));
                    ApplyRate(rate);
                }
                if (!Ready) NextStep = Now;
                // Amortize small checks over rendering frames at complete steps.
                // Commands remain ordered; no world data is read from an I/O worker.
                if(MultiplayerCore.Hosting && Ready && PendingDigestParts>0)
                {
                    long digestStarted=System.Diagnostics.Stopwatch.GetTimestamp();
                    if(!Peers.Any(p=>p.Value.Admitted))PendingDigestParts=0;
                    while(PendingDigestParts>0)
                    {
                        var digest=DigestSchedule.Encode(NextDigestPartition,CoopSimulation.Digest(NextDigestPartition));
                        NextDigestPartition=DigestSchedule.Next(NextDigestPartition);
                        Broadcast(new Frame(FrameKind.StateDigest,Epoch,0,Step,digest));
#if COOP_QA
                        Debug.Log("QA:CHECKSUM_HOST step="+Step+" digest="+BitConverter.ToString(digest));
#endif
                        PendingDigestParts--;
                        if((System.Diagnostics.Stopwatch.GetTimestamp()-digestStarted)*1000.0/System.Diagnostics.Stopwatch.Frequency>=4)break;
                    }
                }
                int stepBudget = 4;
                long stepsStarted=System.Diagnostics.Stopwatch.GetTimestamp();
                while (MultiplayerCore.Hosting && Ready && PendingDigestParts<8 && Peers.Values.All(p=>!p.Admitted || Step-p.AcknowledgedStep<(ulong)(SimulationHz*2)) && Now >= NextStep && stepBudget-- > 0)
                {
                    NextStep += 1.0 / SimulationHz;
                    CoopSimulation.Advance();
                    Broadcast(new Frame(FrameKind.SimulationStep, Epoch, 0, ++Step, SessionPayloads.Step(Revision, SimulationHz)));
                    long time = Shapez2Multiplayer.GameSessionOrchestrator.Simulator.SimulationTime.Value;
                    if (!NextDigestTime.HasValue) NextDigestTime = time + Game.Core.Simulation.Ticks.OneSecond.Value / 12;
                    if (time >= NextDigestTime.Value && Peers.Any(p => p.Value.Admitted))
                    {
                        NextDigestTime += Game.Core.Simulation.Ticks.OneSecond.Value / 12;
                        PendingDigestParts+=4;
                    }
                    if((System.Diagnostics.Stopwatch.GetTimestamp()-stepsStarted)*1000.0/System.Diagnostics.Stopwatch.Frequency>=8)break;
                }
                // Bound catch-up after a long rendering frame; simulation steps are never
                // skipped on a replica. An overloaded host slows down the shared game.
                if (MultiplayerCore.Hosting && Ready && Now - NextStep > 0.5) NextStep = Now + 1.0 / SimulationHz;
            }
            catch (Exception ex)
            {
                Shapez2Multiplayer.logger.Error?.Log(ex.ToString());
                Fail(ex.Message);
            }
        }
        private static void BeginSnapshot(IConnection peer, PeerState state)
        {
            SnapshotStep = Step;
            Broadcast(new Frame(FrameKind.SnapshotReady, Epoch, 0, Step, Array.Empty<byte>()));
            foreach (var admitted in Peers.Values.Where(p => p.Admitted)) admitted.Deadline = Now + 120;
            state.WaitingSnapshot = false; state.Admitted = true; state.Deadline = Now + 120;
            Send(peer, new Frame(FrameKind.Welcome, Epoch, 0, Revision, SessionPayloads.Welcome(Step, SimulationHz, peer.UniversalId)));
            MultiplayerCore.socketManager.Admit(peer);
            NextDigestPartition=PendingDigestParts=0;NextDigestTime=null;state.Deadline=Now+120;
        }

        private static void Handle(IConnection? peer, Frame frame)
        {
            if (MultiplayerCore.Hosting && frame.Kind == FrameKind.Hello)
            {
                if (peer == null || !Peers.TryGetValue(peer, out var state) || state.Admitted || state.WaitingSnapshot)
                    throw new InvalidDataException("Unexpected hello");
                if (System.Text.Encoding.UTF8.GetString(frame.Payload.ToArray()) != Compatibility)
                { Peers.Remove(peer); peer.Close(); return; }
                // A loading client has no world to prepare for somebody else's join.
                // Serialize admissions; at most three peers can wait in the session.
                state.WaitingSnapshot = true; state.Deadline = Now + 300;
                return;
            }
            if (MultiplayerCore.Client && frame.Kind == FrameKind.Welcome && !Welcomed)
            {
                var welcome = SessionPayloads.ReadWelcome(frame.Payload.Span);
                uint announced = MultiplayerCore.connectionManager.UniversalId;
                if (announced != 0 && announced != welcome.Player) throw new InvalidDataException("Conflicting authoritative player identity");
                Epoch = frame.Epoch; Revision = frame.Sequence; Step = welcome.Step;
                LocalPlayerId = welcome.Player; ApplyRate(welcome.Hz);
                CoopPrediction.Initialize(LocalPlayerId);
                SnapshotStep = Step;
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
                        Broadcast(new Frame(FrameKind.Commit, Epoch, frame.RequestId, ++Revision,
                            SessionPayloads.Commit(peer?.UniversalId ?? 0, frame.Payload.Span)));
                    else if (peer != null)
                        Send(peer, new Frame(FrameKind.Reject, Epoch, frame.RequestId, Revision, Array.Empty<byte>()));
                    else
                    {
#if COOP_QA
                        Debug.Log("QA:INTENT_REJECT request=" + frame.RequestId + " revision=" + Revision);
#endif
                        Status = "Edit rejected: world changed; retry";
                    }
                    return;
                }
                if (frame.Kind == FrameKind.StepAck && peer != null)
                {
                    if (frame.Sequence > Step) throw new InvalidDataException("Invalid step acknowledgement");
                    if(frame.Sequence<Peers[peer].AcknowledgedStep)throw new InvalidDataException("Step acknowledgement regressed");
                    if(frame.Payload.Length!=0)throw new InvalidDataException("Invalid heartbeat");
                    Peers[peer].AcknowledgedStep=frame.Sequence;
                    Peers[peer].Deadline = Now + 60; return;
                }
                throw new InvalidDataException("Client sent a host-only message");
            }
            if (!Welcomed) throw new InvalidDataException("Missing welcome");
            switch (frame.Kind)
            {
                case FrameKind.Commit:
                    var commit = SessionPayloads.ReadCommit(frame.Payload);
                    if (frame.Sequence != Revision + 1 || !Execute(commit.Command.ToArray()))
                        throw new InvalidDataException("World command diverged; reconnect required");
                    Revision = frame.Sequence; CoopPrediction.Confirm(commit.Origin, frame.RequestId); Status = "Connected"; break;
                case FrameKind.Reject:
                    CoopPrediction.Reject(frame.RequestId);
#if COOP_QA
                    Debug.Log("QA:INTENT_REJECT request=" + frame.RequestId + " revision=" + frame.Sequence);
#endif
                    Status = "Edit rejected: another player changed the world; retry"; break;
                case FrameKind.StateDigest:
                    var partition=DigestSchedule.ReadIndex(frame.Payload.Span,NextDigestPartition);
                    if (frame.Sequence != Step || !frame.Payload.Span.Slice(1).SequenceEqual(CoopSimulation.Digest(partition)))
                        throw new InvalidDataException("Production state checksum mismatch");
                    NextDigestPartition=DigestSchedule.Next(NextDigestPartition);
#if COOP_QA
                    Debug.Log("QA:CHECKSUM_CLIENT step=" + Step + " digest=" + BitConverter.ToString(frame.Payload.ToArray()));
#endif
                    break;
                case FrameKind.SnapshotReady:
                    if (frame.Sequence != Step) throw new InvalidDataException("Snapshot preparation boundary mismatch");
                    SnapshotStep = Step;
                    CoopSimulation.PrepareSnapshot();
                    CoopSimulation.Digest(); // Apply the same signal-channel serialization maintenance as host capture.
                    NextDigestPartition=0;
                    break;
                case FrameKind.SimulationStep:
                    var boundary = SessionPayloads.ReadStep(frame.Payload.Span);
                    if (!Ready || frame.Sequence != Step + 1 || boundary.Revision != Revision || boundary.Hz != SimulationHz)
                        throw new InvalidDataException("Simulation step gap; reconnect required");
                    CoopSimulation.Advance(); Step = frame.Sequence;
                    Status = "Connected / step " + Step; break;
                case FrameKind.SimulationRate:
                    var change = SessionPayloads.ReadStep(frame.Payload.Span);
                    if (!Ready || frame.Sequence != Step || change.Revision != Revision)
                        throw new InvalidDataException("Simulation rate boundary mismatch");
                    ApplyRate(change.Hz); break;
                default: throw new InvalidDataException("Unexpected host message");
            }
        }
        public static bool RequestSimulationRate(int hz)
        {
            if (MultiplayerCore.Client || !SessionPayloads.SupportedRate(hz)) return false;
            CoopSettings.Current.SimulationHz = hz; CoopSettings.Save();
            if (MultiplayerCore.Hosting) RequestedRate = hz;
            else SimulationHz = hz;
            return true;
        }
        private static void ApplyRate(int hz)
        {
            if (!SessionPayloads.SupportedRate(hz)) throw new InvalidDataException("Unsupported simulation frequency");
            SimulationHz = hz; NextStep = Now + 1.0 / hz;
#if COOP_QA
            Debug.Log("QA:RATE_APPLIED role=" + (MultiplayerCore.Client ? "client" : "host") + " hz=" + hz + " step=" + Step);
#endif
        }
        public static void FinishedJoining(IConnection peer)
        {
            if (!IsAdmitted(peer) || !MultiplayerCore.socketManager.Connecting.Contains(peer))
                throw new InvalidDataException("Unexpected snapshot acknowledgement");
            Peers[peer].Deadline = Now + 30;
            Peers[peer].AcknowledgedStep=Step;
        }
        public static void Fail(string reason)
        {
            CoopPrediction.Clear();
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
