using System;
using System.IO;
using K4os.Compression.LZ4;
using Shapez2Multiplayer.Packets;

namespace Shapez2Multiplayer
{
    internal static class NetworkBoundary
    {
        public const int MaxWireBytes = 128 * 1024 * 1024;
        public static byte[] Unpack(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > MaxWireBytes)
                throw new InvalidDataException("Invalid network packet length");
            if (LZ4Pickler.UnpickledSize(bytes) > MaxWireBytes)
                throw new InvalidDataException("Expanded network packet exceeds limit");
            return LZ4Pickler.Unpickle(bytes);
        }
        public static void ValidateClientTag(Packet tag, IConnection peer)
        {
            if (tag == Packet.Coop || tag == Packet.PlayerInfo) return;
            if (!CoopSession.IsAdmitted(peer)) throw new InvalidDataException("Handshake required");
            if (tag == Packet.Chunked || tag == Packet.ChunkReceived || tag == Packet.FinishedConnecting || tag == Packet.SendToAll)
                return;
            throw new InvalidDataException("Packet is not allowed from a client");
        }
    }
}
