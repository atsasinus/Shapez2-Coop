using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Shapez2Coop.Protocol
{
    public sealed class SnapshotChunk
    {
        public Guid TransferId { get; }
        public int TotalLength { get; }
        public int Index { get; }
        public ReadOnlyMemory<byte> Hash { get; }
        public ReadOnlyMemory<byte> Data { get; }
        public SnapshotChunk(Guid transferId, int totalLength, int index, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> data)
        {
            if (transferId == Guid.Empty || totalLength <= 0 || totalLength > SnapshotAssembler.DefaultMaxSnapshotBytes || index < 0 || hash.Length != 32)
                throw new ProtocolException("Invalid snapshot chunk metadata.");
            long offset = (long)index * SnapshotChunkCodec.ChunkSize;
            if (offset >= totalLength || data.Length != Math.Min(SnapshotChunkCodec.ChunkSize, totalLength - offset))
                throw new ProtocolException("Invalid snapshot chunk size or index.");
            TransferId = transferId; TotalLength = totalLength; Index = index;
            Hash = hash.ToArray(); Data = data.ToArray();
        }
    }

    public static class SnapshotChunkCodec
    {
        public const int ChunkSize = 128 * 1024;
        public const int HeaderSize = 56;
        public static byte[] Encode(SnapshotChunk chunk)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            var bytes = new byte[HeaderSize + chunk.Data.Length];
            chunk.TransferId.TryWriteBytes(bytes.AsSpan(0, 16));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), chunk.TotalLength);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), chunk.Index);
            chunk.Hash.Span.CopyTo(bytes.AsSpan(24, 32));
            chunk.Data.Span.CopyTo(bytes.AsSpan(HeaderSize));
            return bytes;
        }
        public static SnapshotChunk Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length <= HeaderSize || bytes.Length > HeaderSize + ChunkSize) throw new ProtocolException("Invalid snapshot chunk frame size.");
            return new SnapshotChunk(new Guid(bytes.Slice(0, 16)), BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(20)), bytes.Slice(24, 32), bytes.Slice(HeaderSize));
        }
    }

    public sealed class CompletedSnapshot
    {
        public Guid TransferId { get; }
        public ulong Sequence { get; }
        public ReadOnlyMemory<byte> Data { get; }
        internal CompletedSnapshot(Guid transferId, ulong sequence, byte[] data)
        { TransferId = transferId; Sequence = sequence; Data = data; }
    }

    /// <summary>
    /// One in-flight transfer per registered peer, bounded globally. Use monotonically increasing milliseconds
    /// (e.g. Stopwatch); call Expire regularly even when no data arrives. Recovery allocates a fresh assembler.
    /// Received bytes are never deserialized into game state until the complete SHA-256 has been verified.
    /// </summary>
    public sealed class SnapshotAssembler : IDisposable
    {
        public const int DefaultMaxSnapshotBytes = 64 * 1024 * 1024;
        public const int DefaultAggregateBytes = 128 * 1024 * 1024;
        private sealed class Transfer
        {
            public Guid Id;
            public ulong Sequence;
            public byte[] Hash = null!;
            public byte[] Bytes = null!;
            public int NextIndex;
            public long StartedAt;
            public long ProgressAt;
        }
        private readonly object gate = new object();
        private readonly Dictionary<uint, Transfer?> peers = new Dictionary<uint, Transfer?>();
        private readonly int maxSnapshotBytes;
        private readonly int aggregateLimit;
        private readonly long idleTimeout;
        private readonly long lifetime;
        private long lastTime;
        private bool disposed;
        public Guid Epoch { get; }
        public int BufferedBytes { get; private set; }

        public SnapshotAssembler(Guid epoch, int maxSnapshotBytes = DefaultMaxSnapshotBytes,
            int aggregateBytes = DefaultAggregateBytes, long idleTimeoutMilliseconds = 15000, long lifetimeMilliseconds = 120000)
        {
            if (epoch == Guid.Empty) throw new ArgumentException("Empty epoch.", nameof(epoch));
            if (maxSnapshotBytes <= 0 || maxSnapshotBytes > DefaultMaxSnapshotBytes || aggregateBytes < maxSnapshotBytes ||
                aggregateBytes > DefaultAggregateBytes || idleTimeoutMilliseconds <= 0 || lifetimeMilliseconds < idleTimeoutMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes));
            Epoch = epoch; this.maxSnapshotBytes = maxSnapshotBytes; aggregateLimit = aggregateBytes;
            idleTimeout = idleTimeoutMilliseconds; lifetime = lifetimeMilliseconds;
        }

        public bool RegisterPeer(uint peerId)
        {
            lock (gate)
            {
                EnsureOpen();
                if (peers.Count >= HostSequencer.MaxPeers || peers.ContainsKey(peerId)) return false;
                peers.Add(peerId, null);
                return true;
            }
        }
        public void RemovePeer(uint peerId)
        {
            lock (gate)
            {
                if (peers.TryGetValue(peerId, out var transfer) && transfer != null) BufferedBytes -= transfer.Bytes.Length;
                peers.Remove(peerId);
            }
        }

        public CompletedSnapshot? Add(uint peerId, Frame frame, long nowMilliseconds)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            lock (gate)
            {
                EnsureOpen();
                AdvanceTime(nowMilliseconds);
                if (!peers.TryGetValue(peerId, out var transfer)) throw new ProtocolException("Snapshot from unknown peer.");
                try
                {
                    if (frame.Kind != FrameKind.SnapshotChunk || frame.Epoch != Epoch || frame.RequestId != 0)
                        throw new ProtocolException("Invalid snapshot envelope.");
                    var chunk = SnapshotChunkCodec.Decode(frame.Payload.Span);
                    if (chunk.TotalLength > maxSnapshotBytes) throw new ProtocolException("Snapshot exceeds configured limit.");
                    if (transfer != null && IsExpired(transfer, nowMilliseconds)) throw new ProtocolException("Snapshot transfer timed out.");
                    if (transfer == null)
                    {
                        if (chunk.Index != 0) throw new ProtocolException("Snapshot must begin with index zero.");
                        if (chunk.TotalLength > aggregateLimit - BufferedBytes) throw new ProtocolException("Snapshot memory budget exhausted.");
                        transfer = new Transfer { Id = chunk.TransferId, Sequence = frame.Sequence, Hash = chunk.Hash.ToArray(),
                            Bytes = new byte[chunk.TotalLength], StartedAt = nowMilliseconds, ProgressAt = nowMilliseconds };
                        peers[peerId] = transfer;
                        BufferedBytes += chunk.TotalLength;
                    }
                    if (transfer.Id != chunk.TransferId || transfer.Sequence != frame.Sequence || transfer.Bytes.Length != chunk.TotalLength || !chunk.Hash.Span.SequenceEqual(transfer.Hash))
                        throw new ProtocolException("Snapshot metadata changed during transfer.");
                    int offset = checked(chunk.Index * SnapshotChunkCodec.ChunkSize);
                    if (chunk.Index < transfer.NextIndex)
                    {
                        if (!chunk.Data.Span.SequenceEqual(transfer.Bytes.AsSpan(offset, chunk.Data.Length))) throw new ProtocolException("Conflicting duplicate snapshot chunk.");
                        return null; // Exact retry, without extending the timeout.
                    }
                    if (chunk.Index != transfer.NextIndex) throw new ProtocolException("Snapshot chunk gap.");
                    chunk.Data.Span.CopyTo(transfer.Bytes.AsSpan(offset));
                    transfer.NextIndex++;
                    transfer.ProgressAt = nowMilliseconds;
                    if (offset + chunk.Data.Length != transfer.Bytes.Length) return null;
                    using (var sha = SHA256.Create())
                    {
                        if (!sha.ComputeHash(transfer.Bytes).AsSpan().SequenceEqual(transfer.Hash)) throw new ProtocolException("Snapshot SHA-256 mismatch.");
                    }
                    var completed = new CompletedSnapshot(transfer.Id, transfer.Sequence, transfer.Bytes);
                    ClearTransfer(peerId);
                    return completed;
                }
                catch
                {
                    ClearTransfer(peerId);
                    throw;
                }
            }
        }

        /// <returns>IDs of peers whose transfers were discarded; callers must request a fresh snapshot or disconnect.</returns>
        public IReadOnlyList<uint> Expire(long nowMilliseconds)
        {
            lock (gate)
            {
                EnsureOpen();
                AdvanceTime(nowMilliseconds);
                var expired = new List<uint>();
                foreach (var peer in peers)
                    if (peer.Value != null && IsExpired(peer.Value, nowMilliseconds)) expired.Add(peer.Key);
                foreach (var peer in expired) ClearTransfer(peer);
                return expired;
            }
        }
        private bool IsExpired(Transfer transfer, long now) => now - transfer.ProgressAt >= idleTimeout || now - transfer.StartedAt >= lifetime;
        private void AdvanceTime(long now)
        {
            if (now < lastTime) throw new ArgumentOutOfRangeException(nameof(now), "Clock must be monotonic.");
            lastTime = now;
        }
        private void ClearTransfer(uint peerId)
        {
            if (peers.TryGetValue(peerId, out var transfer) && transfer != null)
            { BufferedBytes -= transfer.Bytes.Length; peers[peerId] = null; }
        }
        private void EnsureOpen() { if (disposed) throw new ObjectDisposedException(nameof(SnapshotAssembler)); }
        public void Dispose()
        {
            lock (gate) { peers.Clear(); BufferedBytes = 0; disposed = true; }
        }
    }
}
