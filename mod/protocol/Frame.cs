using System;
using System.Buffers.Binary;

namespace Shapez2Coop.Protocol
{
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
        public ProtocolException(string message, Exception inner) : base(message, inner) { }
    }

    public enum FrameKind : byte
    {
        Hello = 1, Welcome, Intent, Commit, Reject, SnapshotChunk, SnapshotReady, StateDigest, Disconnect, SimulationStep, StepAck
    }

    /// <summary>Transport identity and authorization are supplied by the caller, never by payload data.</summary>
    public sealed class Frame
    {
        public FrameKind Kind { get; }
        public Guid Epoch { get; }
        public ulong RequestId { get; }
        public ulong Sequence { get; }
        public ReadOnlyMemory<byte> Payload { get; }

        public Frame(FrameKind kind, Guid epoch, ulong requestId, ulong sequence, ReadOnlySpan<byte> payload)
        {
            if (!FrameCodec.IsKnownKind(kind)) throw new ProtocolException("Unknown frame kind.");
            if (epoch == Guid.Empty) throw new ProtocolException("Empty session epoch.");
            if (payload.Length > FrameCodec.MaxPayloadSize) throw new ProtocolException("Payload exceeds limit.");
            Kind = kind;
            Epoch = epoch;
            RequestId = requestId;
            Sequence = sequence;
            Payload = payload.ToArray();
        }
    }

    public static class FrameCodec
    {
        public const uint Magic = 0x50433253; // S2CP, little endian
        public const ushort Version = 1;
        public const int HeaderSize = 44;
        public const int MaxPayloadSize = 1024 * 1024;
        public const int MaxFrameSize = HeaderSize + MaxPayloadSize;
        internal static bool IsKnownKind(FrameKind kind) => kind >= FrameKind.Hello && kind <= FrameKind.StepAck;

        public static byte[] Encode(Frame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            var result = new byte[HeaderSize + frame.Payload.Length];
            var span = result.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), Version);
            span[6] = (byte)frame.Kind;
            // Byte 7 is reserved and must remain zero.
            frame.Epoch.TryWriteBytes(span.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24), frame.RequestId);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32), frame.Sequence);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40), frame.Payload.Length);
            frame.Payload.Span.CopyTo(span.Slice(HeaderSize));
            return result;
        }

        /// <summary>Exactly one frame; trailing data, unknown flags and oversized payloads are rejected before allocation.</summary>
        public static Frame Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < HeaderSize || bytes.Length > MaxFrameSize) throw new ProtocolException("Invalid frame size.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic) throw new ProtocolException("Wrong protocol magic.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4)) != Version) throw new ProtocolException("Protocol version mismatch.");
            if (bytes[7] != 0) throw new ProtocolException("Unsupported flags.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(40));
            if (length < 0 || length > MaxPayloadSize || length != bytes.Length - HeaderSize) throw new ProtocolException("Invalid payload length.");
            return new Frame((FrameKind)bytes[6], new Guid(bytes.Slice(8, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32)), bytes.Slice(HeaderSize));
        }
    }
}
