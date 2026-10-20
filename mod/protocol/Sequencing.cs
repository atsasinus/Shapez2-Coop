using System;
using System.Collections.Generic;

namespace Shapez2Coop.Protocol
{
    public enum DecisionKind { Accepted, Duplicate, Rejected }

    public sealed class HostDecision
    {
        public DecisionKind Kind { get; }
        public Frame? Commit { get; }
        public string? Rejection { get; }
        internal HostDecision(DecisionKind kind, Frame? commit = null, string? rejection = null)
        { Kind = kind; Commit = commit; Rejection = rejection; }
    }

    /// <summary>
    /// Owns one host's command order. Call from the game thread; the lock also prevents concurrent callbacks.
    /// apply must validate before mutating and return false only if it changed nothing. Exceptions fault the session.
    /// Retries are idempotent only for the latest request; earlier requests are rejected without application.
    /// A duplicate result must be sent only to the requester, never broadcast as a new commit.
    /// </summary>
    public sealed class HostSequencer
    {
        private sealed class Peer
        {
            public bool Ready;
            public ulong LastRequest;
            public byte[]? LastPayload;
            public HostDecision? LastDecision;
        }
        private readonly object gate = new object();
        private readonly Dictionary<uint, Peer> peers = new Dictionary<uint, Peer>();
        private readonly Peer host = new Peer { Ready = true };
        private bool applying;
        public Guid Epoch { get; }
        public ulong Sequence { get; private set; }
        public bool Faulted { get; private set; }
        public int PeerCount { get { lock (gate) return peers.Count; } }
        public const int MaxPeers = 3;

        public HostSequencer(Guid epoch, ulong initialSequence = 0)
        {
            if (epoch == Guid.Empty) throw new ArgumentException("Empty epoch.", nameof(epoch));
            Epoch = epoch;
            Sequence = initialSequence;
        }

        public bool RegisterPeer(uint peerId)
        {
            lock (gate)
            {
                EnsureHealthy();
                if (peerId == 0 || peers.ContainsKey(peerId) || peers.Count >= MaxPeers) return false;
                peers.Add(peerId, new Peer());
                return true;
            }
        }

        public bool MarkReady(uint peerId, ulong snapshotSequence)
        {
            lock (gate)
            {
                EnsureHealthy();
                if (snapshotSequence != Sequence || !peers.TryGetValue(peerId, out var peer)) return false;
                peer.Ready = true;
                return true;
            }
        }

        public bool RemovePeer(uint peerId) { lock (gate) return peers.Remove(peerId); }

        /// <summary>Peer 0 means local host intent. Remote callers must use their authenticated connection's assigned ID.</summary>
        public HostDecision Submit(uint peerId, Frame intent, Func<ReadOnlyMemory<byte>, bool> apply)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            lock (gate)
            {
                EnsureHealthy();
                if (applying) throw new ProtocolException("Reentrant host command application.");
                if (intent.Kind != FrameKind.Intent || intent.Epoch != Epoch || intent.Sequence != 0 || intent.RequestId == 0)
                    return Reject("Invalid intent envelope.");
                Peer peer;
                if (peerId == 0) peer = host;
                else if (!peers.TryGetValue(peerId, out peer!)) return Reject("Unknown peer.");
                if (!peer.Ready) return Reject("Snapshot acknowledgement required.");
                if (intent.RequestId == peer.LastRequest)
                {
                    if (peer.LastPayload == null || !intent.Payload.Span.SequenceEqual(peer.LastPayload)) return Reject("Request ID collision.");
                    return new HostDecision(DecisionKind.Duplicate, peer.LastDecision!.Commit, peer.LastDecision.Rejection);
                }
                if (intent.RequestId < peer.LastRequest) return Reject("Stale request.");
                if (Sequence == ulong.MaxValue) { Faulted = true; throw new ProtocolException("Host sequence exhausted."); }
                HostDecision decision;
                applying = true;
                try
                {
                    if (!apply(intent.Payload)) decision = Reject("Command rejected by host.");
                    else decision = new HostDecision(DecisionKind.Accepted,
                        new Frame(FrameKind.Commit, Epoch, intent.RequestId, ++Sequence, intent.Payload.Span));
                }
                catch (Exception ex)
                {
                    Faulted = true;
                    throw new ProtocolException("Command application failed; session requires recovery.", ex);
                }
                finally { applying = false; }
                peer.LastRequest = intent.RequestId;
                peer.LastPayload = intent.Payload.ToArray();
                peer.LastDecision = decision;
                return decision;
            }
        }

        private static HostDecision Reject(string reason) => new HostDecision(DecisionKind.Rejected, rejection: reason);
        private void EnsureHealthy() { if (Faulted) throw new ProtocolException("Host session is faulted."); }
    }

    /// <summary>Feed only authenticated host commits on the main game thread. Recreate after a verified snapshot recovery.</summary>
    public sealed class ClientCommitTracker
    {
        private readonly object gate = new object();
        private Frame? last;
        private bool applying;
        public Guid Epoch { get; }
        public ulong Sequence { get; private set; }
        public bool Faulted { get; private set; }
        public ClientCommitTracker(Guid epoch, ulong snapshotSequence = 0)
        {
            if (epoch == Guid.Empty) throw new ArgumentException("Empty epoch.", nameof(epoch));
            Epoch = epoch;
            Sequence = snapshotSequence;
        }

        /// <returns>False only for an exact retry of the latest commit; it is never applied twice.</returns>
        public bool Apply(Frame commit, Action<ReadOnlyMemory<byte>> apply)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            lock (gate)
            {
                if (Faulted) throw new ProtocolException("Client session is faulted.");
                try
                {
                    if (applying || commit.Kind != FrameKind.Commit || commit.Epoch != Epoch || commit.RequestId == 0)
                        throw new ProtocolException("Invalid host commit.");
                    if (last != null && commit.Sequence == Sequence && commit.RequestId == last.RequestId && commit.Payload.Span.SequenceEqual(last.Payload.Span)) return false;
                    if (Sequence == ulong.MaxValue || commit.Sequence != Sequence + 1) throw new ProtocolException("Commit gap or conflicting replay; snapshot recovery required.");
                    applying = true;
                    apply(commit.Payload);
                    Sequence = commit.Sequence;
                    last = commit;
                    return true;
                }
                catch (Exception ex)
                {
                    Faulted = true;
                    if (ex is ProtocolException) throw;
                    throw new ProtocolException("Commit application failed; snapshot recovery required.", ex);
                }
                finally { applying = false; }
            }
        }
    }
}
