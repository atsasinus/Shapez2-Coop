using System;
using System.Buffers.Binary;

namespace Shapez2Coop.Protocol
{
    public static class SessionPayloads
    {
        public const int MaxCommandBytes = 120 * 1024;
        public static bool SupportedRate(int hz) => hz == 20 || hz == 30 || hz == 60;
        private static void RequireRate(int hz)
        { if (!SupportedRate(hz)) throw new ProtocolException("Unsupported simulation frequency"); }
        public static byte[] Welcome(ulong step, int hz, uint player)
        {
            RequireRate(hz);
            if (player == 0) throw new ProtocolException("Client identity cannot be the host");
            var data = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(data, step);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), hz);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), player);
            return data;
        }
        public static (ulong Step, int Hz, uint Player) ReadWelcome(ReadOnlySpan<byte> data)
        {
            if (data.Length != 16) throw new ProtocolException("Invalid welcome boundary");
            var hz = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8)); RequireRate(hz);
            var player = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12));
            if (player == 0) throw new ProtocolException("Invalid client identity");
            return (BinaryPrimitives.ReadUInt64LittleEndian(data), hz, player);
        }
        public static byte[] Commit(uint origin, ReadOnlySpan<byte> command)
        {
            if (command.Length == 0 || command.Length > MaxCommandBytes) throw new ProtocolException("Invalid command size");
            var data = new byte[4 + command.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data, origin); command.CopyTo(data.AsSpan(4));
            return data;
        }
        public static (uint Origin, ReadOnlyMemory<byte> Command) ReadCommit(ReadOnlyMemory<byte> data)
        {
            if (data.Length <= 4 || data.Length > MaxCommandBytes + 4) throw new ProtocolException("Invalid commit size");
            return (BinaryPrimitives.ReadUInt32LittleEndian(data.Span), data.Slice(4));
        }
        public static byte[] Step(ulong revision, int hz)
        {
            RequireRate(hz); var data = new byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(data, revision);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), hz); return data;
        }
        public static (ulong Revision, int Hz) ReadStep(ReadOnlySpan<byte> data)
        {
            if (data.Length != 12) throw new ProtocolException("Invalid simulation boundary");
            var hz = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8)); RequireRate(hz);
            return (BinaryPrimitives.ReadUInt64LittleEndian(data), hz);
        }
    }
}
