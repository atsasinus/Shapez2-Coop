using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Shapez2Coop.Protocol;
using UnityEngine;
namespace Shapez2Multiplayer.Packets
{
    public sealed class ChunkedPacket : IPacket
    {
        public const int ChunkThreshold = 256 * 1024;
        public const int MaxTotalDataSize = SnapshotAssembler.DefaultMaxSnapshotBytes;
        public static readonly List<Tuple<ChunkedPacket, IConnection, Packet>> ToSend = new List<Tuple<ChunkedPacket, IConnection, Packet>>();
        private static readonly HashSet<uint> Registered = new HashSet<uint>();
        private static Guid CacheEpoch = Guid.NewGuid();
        private static SnapshotAssembler Receiver = new SnapshotAssembler(CacheEpoch);
        private static uint CurrentId;
        public uint Id;
        private byte[] Payload;
        public ChunkedPacket() { }
        public static void Reset()
        {
            Receiver.Dispose(); CacheEpoch = Guid.NewGuid(); Receiver = new SnapshotAssembler(CacheEpoch);
            Registered.Clear(); ToSend.Clear(); CurrentId = 0;
        }
        public static void Remove(IConnection peer)
        {
            Receiver.RemovePeer(peer.UniversalId); Registered.Remove(peer.UniversalId);
            ToSend.RemoveAll(c => c.Item2.Equals(peer));
        }
        public static void Send(byte[] data, IConnection peer, Packet type)
        {
            if (data.Length > MaxTotalDataSize || ToSend.Sum(c => (long)c.Item1.Payload.Length) + data.Length > 128L * 1024 * 1024)
                throw new InvalidDataException("Outgoing snapshot budget exhausted");
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            var transfer = Guid.NewGuid(); var id = ++CurrentId;
            for (int offset = 0, index = 0; offset < data.Length; offset += SnapshotChunkCodec.ChunkSize, index++)
            {
                var chunk = new SnapshotChunk(transfer, data.Length, index, hash,
                    data.AsSpan(offset, Math.Min(SnapshotChunkCodec.ChunkSize, data.Length - offset)));
                ToSend.Add(Tuple.Create(new ChunkedPacket { Id = id, Payload = SnapshotChunkCodec.Encode(chunk) }, peer, type));
            }
        }
        // Reliable transfers must finish; a partially sent transfer cannot be silently cancelled.
        public static void Cancel(uint id) { }
        public static void Cancel(Packet type) { }
        public static void Update()
        {
            for (int i = 0; i < 4 && ToSend.Count > 0; i++)
            {
                var item = ToSend[0]; ToSend.RemoveAt(0);
                if (!item.Item2.Send(PacketExtensions.Encode(item.Item1), Packet.Chunked))
                { Remove(item.Item2); item.Item2.Close(); }
            }
            foreach (var peer in Receiver.Expire((long)(Time.realtimeSinceStartupAsDouble * 1000)))
            {
                var connection = MultiplayerCore.socketManager?.Connected.FirstOrDefault(c => c.UniversalId == peer);
                if (connection != null) connection.Close(); else if (MultiplayerCore.Client) CoopSession.Fail("Snapshot transfer timed out");
            }
        }
        public void Decode(Stream stream)
        {
            int length = checked((int)(stream.Length - stream.Position));
            if (length > SnapshotChunkCodec.HeaderSize + SnapshotChunkCodec.ChunkSize) throw new InvalidDataException("Oversized chunk");
            using var reader = new BinaryReader(stream);
            Payload = reader.ReadBytes(length);
            SnapshotChunkCodec.Decode(Payload);
        }
        public bool Encode(Stream stream) { stream.Write(Payload, 0, Payload.Length); return true; }
        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            uint peer = connection?.UniversalId ?? 0;
            if (Registered.Add(peer) && !Receiver.RegisterPeer(peer)) throw new InvalidDataException("Chunk peer limit");
            var result = Receiver.Add(peer, new Frame(FrameKind.SnapshotChunk, CacheEpoch, 0, 0, Payload),
                (long)(Time.realtimeSinceStartupAsDouble * 1000));
            if (result == null) return;
            var bytes = result.Data.ToArray();
            var inner = NetworkBoundary.Unpack(bytes);
            if (PacketExtensions.PacketFromData(inner) == Packet.Chunked) throw new InvalidDataException("Nested chunk transfer");
            if (connection != null) MultiplayerCore.socketManager.OnMessage(connection, bytes);
            else MultiplayerCore.connectionManager.OnMessage(bytes);
        }
    }
}
