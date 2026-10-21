using K4os.Compression.LZ4;
using Shapez2Multiplayer.Packets;
using System;
using System.Collections.Generic;

namespace Shapez2Multiplayer
{
    public sealed class ENetConnection : IConnection, IEquatable<ENetConnection>
    {
        private readonly ENetIoPump pump;
        private readonly ulong key;
        private bool closed;
        internal Action? ClosedLocally;
        public static readonly Dictionary<uint,string> NameCache = new Dictionary<uint,string>();
        internal ENetConnection(ENetIoPump pump, ulong key, uint slot)
        { this.pump=pump;this.key=key;Id=slot; }
        public uint Id { get; }
        public uint UniversalId { get; } = MultiplayerCore.CurrentUniversalId++;
        public string? Name => NameCache.GetValueOrDefault(Id, $"Player {UniversalId}");
        public int Ping => pump.Ping(key);
        internal void MarkClosed() => closed=true;
        public void Close() {if(closed)return;closed=true;pump.Close(key);ClosedLocally?.Invoke();}
        public bool Send(byte[] data, Packets.Packet type)
        {
            if(closed || pump.Failure!=null)return false;
            var compressed=LZ4Pickler.Pickle(data);
            if(compressed.Length>ChunkedPacket.ChunkThreshold)
            {ChunkedPacket.Send(compressed,this,type);return true;}
            return pump.Send(key,compressed);
        }
        public bool Equals(IConnection? other) => other is ENetConnection c && Equals(c);
        public bool Equals(ENetConnection? other) => other!=null && UniversalId==other.UniversalId;
        public override bool Equals(object obj) => obj is ENetConnection c && Equals(c);
        public override int GetHashCode() => (int)UniversalId;
        public static bool operator ==(ENetConnection? a,ENetConnection? b)
            => ReferenceEquals(a,b) || (!(a is null) && !(b is null) && a.UniversalId==b.UniversalId);
        public static bool operator !=(ENetConnection? a,ENetConnection? b) => !(a==b);
    }
}
