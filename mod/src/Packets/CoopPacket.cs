using System;
using System.IO;
using Shapez2Coop.Protocol;

namespace Shapez2Multiplayer.Packets
{
    public sealed class CoopPacket : IPacket
    {
        public Frame Frame { get; private set; }
        public CoopPacket() { }
        public CoopPacket(Frame frame) { Frame = frame; }
        public void Decode(Stream stream)
        {
            var length = checked((int)(stream.Length - stream.Position));
            if (length < FrameCodec.HeaderSize || length > FrameCodec.MaxFrameSize)
                throw new InvalidDataException("Invalid co-op frame size");
            var bytes = new byte[length];
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            if (reader.Read(bytes, 0, length) != length) throw new EndOfStreamException();
            Frame = FrameCodec.Decode(bytes);
        }
        public bool Encode(Stream stream)
        {
            var bytes = FrameCodec.Encode(Frame);
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
            => CoopSession.Receive(connection, Frame);
    }
}
