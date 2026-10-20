using System.IO;

namespace Shapez2Multiplayer.Packets
{
    public class SendToAllPacket : IPacket
    {
        public IPacket Packet { get; set; }
        public byte[]? PacketData;
        public SendToAllPacket() { }
        public SendToAllPacket(IPacket packet)
        {
            Packet = packet;
        }
        public SendToAllPacket(byte[] packet)
        {
            PacketData = packet;
        }
        public void Decode(Stream stream)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                PacketData = ms.ToArray();
                var tag = PacketExtensions.PacketFromData(PacketData);
                if (!Allowed(tag)) throw new InvalidDataException("Relay packet type is forbidden");
                Packet = PacketExtensions.Decode(PacketData, out var from);
                if (from.HasValue) throw new InvalidDataException("Forged relay sender");
            }
        }

        public bool Encode(Stream stream)
        {
            if (PacketData != null)
            {
                stream.Write(PacketData);
                return true;
            }
            var data = PacketExtensions.Encode(Packet);
            if (data == null) return false;
            stream.Write(data);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection == null || !CoopSession.IsAdmitted(connection) || PacketData == null)
                throw new InvalidDataException("Invalid relay source");
            if (MultiplayerCore.socketManager.Connecting.Count > 0) return;
            Packet.Handle(connection);
            var routed = new byte[PacketData.Length + 4];
            routed[0] = 1;
            System.BitConverter.GetBytes(connection.UniversalId).CopyTo(routed, 1);
            System.Buffer.BlockCopy(PacketData, 1, routed, 5, PacketData.Length - 1);
            foreach (var peer in MultiplayerCore.socketManager.Connected)
                if (peer != connection && CoopSession.IsAdmitted(peer))
                    peer.Send(routed, PacketExtensions.PacketFromData(PacketData));
        }
        private static bool Allowed(Packet tag) => tag == Packets.Packet.Cursor ||
            tag == Packets.Packet.ViewportPropertyChanged || tag == Packets.Packet.PlayerInteractionStateChanged ||
            tag == Packets.Packet.UpdateBuildingMassSelection || tag == Packets.Packet.UpdateIslandMassSelection ||
            tag == Packets.Packet.PlacementIndicatorData || tag == Packets.Packet.PinChange ||
            tag == Packets.Packet.UpdateWaypoint || tag == Packets.Packet.DeleteWaypoint;
    }
}
