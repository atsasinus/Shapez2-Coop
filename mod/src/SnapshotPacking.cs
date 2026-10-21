using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shapez2Multiplayer.Packets;

namespace Shapez2Multiplayer
{
    // Pure immutable byte processing. Capture and loading stay on the game thread.
    internal static class SnapshotPacking
    {
        private static int active;
        public static bool Busy => Volatile.Read(ref active)!=0;
        public static Task<byte[]> Start(IReadOnlyDictionary<string,byte[]> snapshot,string uid)
        {
            var entries=snapshot.ToArray();
            if(entries.Length>65536 || entries.Sum(e=>(long)e.Value.Length)>NetworkBoundary.MaxWireBytes)
                throw new InvalidDataException("Expanded snapshot exceeds transfer budget");
            var uidBytes=System.Text.Encoding.UTF8.GetBytes(uid);
            if(uidBytes.Length>255)throw new InvalidDataException("Snapshot UID exceeds limit");
            if(Interlocked.CompareExchange(ref active,1,0)!=0)throw new InvalidOperationException("Snapshot packer busy");
            return Task.Run(()=>{
                try
                {
                    using var zipBytes=new MemoryStream();
                    using(var archive=new ZipArchive(zipBytes,ZipArchiveMode.Create,true,System.Text.Encoding.UTF8))
                        foreach(var entry in entries)
                        {
                            using(var output=archive.CreateEntry(entry.Key,CompressionLevel.Fastest).Open())output.Write(entry.Value,0,entry.Value.Length);
                            if(zipBytes.Length>NetworkBoundary.MaxWireBytes)throw new InvalidDataException("Packed snapshot exceeds transfer budget");
                        }
                    using var packet=new MemoryStream();
                    packet.WriteByte(0);packet.WriteByte((byte)Packet.Savegame);packet.WriteByte((byte)uidBytes.Length);
                    packet.Write(uidBytes,0,uidBytes.Length);zipBytes.Position=0;zipBytes.CopyTo(packet);
                    if(packet.Length>NetworkBoundary.MaxWireBytes)throw new InvalidDataException("Snapshot packet exceeds transfer budget");
                    return packet.ToArray();
                }
                finally {Volatile.Write(ref active,0);}
            });
        }
    }
}
