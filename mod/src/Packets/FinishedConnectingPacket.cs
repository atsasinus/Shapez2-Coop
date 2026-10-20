using Core.Localization;
using System.IO;
using System.Linq;

namespace Shapez2Multiplayer.Packets
{
    public class FinishedConnectingPacket : IPacket
    {
        public ulong Step, Revision;
        public byte[] Digest;
        public FinishedConnectingPacket() { }
        public FinishedConnectingPacket(ulong step, ulong revision, byte[] digest)
        { Step = step; Revision = revision; Digest = digest; }
        public void Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            Step = reader.ReadUInt64(); Revision = reader.ReadUInt64();
            Digest = reader.ReadBytes(32);
            if (Digest.Length != 32 || stream.Position != stream.Length) throw new InvalidDataException("Invalid snapshot acknowledgement");
        }

        public bool Encode(Stream stream)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            if (Digest == null || Digest.Length != 32) throw new InvalidDataException("Missing snapshot checksum");
            writer.Write(Step); writer.Write(Revision); writer.Write(Digest);
            return true;
        }

        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection == null)
            {
                Shapez2Multiplayer.logger.Warning.Log("FinishedConnectingPacket Recieved From Host");
                return;
            }
            CoopSession.FinishedJoining(connection);
            if (Step != CoopSession.StepCount || Revision != CoopSession.EditRevision ||
                !Digest.SequenceEqual(CoopSimulation.Digest()))
                throw new InvalidDataException("Loaded snapshot does not match host state");
#if COOP_QA
            UnityEngine.Debug.Log("QA:SNAPSHOT_MATCH step=" + Step + " digest=" + System.BitConverter.ToString(Digest));
#endif
            MultiplayerCore.socketManager.Connecting.Remove(connection);
            if (MultiplayerCore.socketManager.Connecting.Count == 0)
            {
                MultiplayerCore.socketManager.SendToAll(new PausePacket(false));
                new PausePacket(false).Handle(null);
                PlacementIndicatorDataPacket.SentToAllConnections = false;
                MultiplayerCore.socketManager.ForceUpdateCursor();
                MultiplayerCore.socketManager.PingUpdateTimer = float.MaxValue;
            }
            else
            {
                MultiplayerCore.socketManager.SendToAllExcept(new PausePacket(true, new CombinedText("multiplayer.paused-dialog.description-waitingforplayer".T(), new RawText("\n" + string.Join(", ", MultiplayerCore.socketManager.Connecting.Select(c => c.Name))))), MultiplayerCore.socketManager.Connecting);
                new PausePacket(true, new CombinedText("multiplayer.paused-dialog.description-waitingforplayer".T(), new RawText("\n" + string.Join(", ", MultiplayerCore.socketManager.Connecting.Select(c => c.Name))))).Handle(null);
            }
        }
    }
}
