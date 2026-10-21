using System;

namespace Shapez2Coop.Protocol
{
    // Every partition is sampled at one complete authoritative step, never across steps.
    public static class DigestSchedule
    {
        public const int Partitions = 256;
        public static int Partition(int x,int y,int z)
        {
            unchecked
            {
                uint h=(uint)x*0x9e3779b1u ^ (uint)y*0x85ebca77u ^ (uint)z*0xc2b2ae3du;
                h^=h>>16;h*=0x7feb352du;h^=h>>15;
                return (int)(h & (Partitions-1));
            }
        }
        public static int Next(int index) {Require(index);return (index+1)%Partitions;}
        private static void Require(int index)
        {if(index<0 || index>=Partitions)throw new ProtocolException("Invalid digest partition");}
        public static byte[] Encode(int index,ReadOnlySpan<byte> digest)
        {
            Require(index);if(digest.Length!=32)throw new ProtocolException("Invalid state digest");
            var bytes=new byte[33];bytes[0]=(byte)index;digest.CopyTo(bytes.AsSpan(1));return bytes;
        }
        public static int ReadIndex(ReadOnlySpan<byte> bytes,int expected)
        {
            Require(expected);
            if(bytes.Length!=33 || bytes[0]!=expected)throw new ProtocolException("Digest coverage gap");
            return bytes[0];
        }
    }
}
