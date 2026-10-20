using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Shapez2Coop.Protocol;

static class Program
{
    static readonly Guid Epoch = Guid.Parse("91662917-03f0-4669-b0bd-aa79438d88f3");
    static int checks;
    static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    static void Reject(Action action, string name)
    {
        try { action(); } catch (ProtocolException) { checks++; return; }
        throw new Exception("Expected protocol rejection: " + name);
    }
    static Frame Intent(ulong id, byte value = 1) => new Frame(FrameKind.Intent, Epoch, id, 0, new[] { value });
    static void Main()
    {
        Frames(); Host(); Client(); Snapshots();
        Console.WriteLine($"PASS: {checks} protocol assertions; frame fuzz/truncation, four participants, concurrent ordering, replay, fail-closed commits, bounded snapshots.");
    }
    static void Frames()
    {
        var encoded = FrameCodec.Encode(Intent(42, 9));
        var decoded = FrameCodec.Decode(encoded);
        Check(decoded.Epoch == Epoch && decoded.RequestId == 42 && decoded.Payload.Span[0] == 9, "frame round trip");
        for (int i = 0; i < encoded.Length; i++) { int length = i; Reject(() => FrameCodec.Decode(encoded.AsSpan(0, length)), "truncation"); }
        foreach (int offset in new[] { 0, 4, 6, 7 })
        { var bad = (byte[])encoded.Clone(); bad[offset] = 255; Reject(() => FrameCodec.Decode(bad), "header corruption"); }
        foreach (int length in new[] { -1, int.MaxValue, 0, 2 })
        { var bad = (byte[])encoded.Clone(); BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(40), length); Reject(() => FrameCodec.Decode(bad), "length corruption"); }
        Reject(() => FrameCodec.Decode(encoded.Concat(new byte[] { 0 }).ToArray()), "trailing byte");
        Reject(() => new Frame(FrameKind.Intent, Guid.Empty, 1, 0, Array.Empty<byte>()), "empty epoch");
        Reject(() => new Frame(FrameKind.Intent, Epoch, 1, 0, new byte[FrameCodec.MaxPayloadSize + 1]), "allocation bound");
        var random = new Random(725);
        for (int i = 0; i < 500; i++)
        { var bytes = new byte[random.Next(0, 256)]; random.NextBytes(bytes); Reject(() => FrameCodec.Decode(bytes), "random wire input"); }
    }
    static void Host()
    {
        var host = new HostSequencer(Epoch);
        for (uint peer = 1; peer <= 3; peer++) Check(host.RegisterPeer(peer), "three peers admitted");
        Check(!host.RegisterPeer(4) && !host.RegisterPeer(0) && !host.RegisterPeer(1), "capacity and identity limits");
        int applications = 0;
        Check(host.Submit(1, Intent(1), _ => { applications++; return true; }).Kind == DecisionKind.Rejected, "not ready");
        for (uint peer = 1; peer <= 3; peer++) Check(host.MarkReady(peer, 0), "snapshot boundary acknowledgement");
        var first = host.Submit(1, Intent(1), _ => { applications++; return true; });
        Check(first.Kind == DecisionKind.Accepted && first.Commit!.Sequence == 1 && applications == 1, "first accepted");
        Check(host.Submit(1, Intent(1), _ => { applications++; return true; }).Kind == DecisionKind.Duplicate && applications == 1, "duplicate not applied");
        Check(host.Submit(1, Intent(1, 2), _ => true).Kind == DecisionKind.Rejected, "ID collision");
        Check(host.Submit(1, Intent(2), _ => false).Kind == DecisionKind.Rejected && host.Sequence == 1, "rejected command no commit");
        Check(host.Submit(1, Intent(2), _ => true).Kind == DecisionKind.Duplicate && host.Sequence == 1, "rejected request cannot change outcome");
        Check(host.Submit(1, Intent(1), _ => true).Kind == DecisionKind.Rejected, "stale replay");
        Check(host.Submit(9, Intent(1), _ => true).Kind == DecisionKind.Rejected, "unknown peer");
        Check(host.Submit(1, new Frame(FrameKind.Intent, Guid.NewGuid(), 3, 0, new byte[1]), _ => true).Kind == DecisionKind.Rejected, "old epoch");
        Check(host.Submit(1, new Frame(FrameKind.Commit, Epoch, 3, 2, new byte[1]), _ => true).Kind == DecisionKind.Rejected, "wrong direction");
        int active = 0, maxActive = 0;
        var sequences = new ConcurrentBag<ulong>();
        Parallel.For(0, 4, p =>
        {
            uint peer = (uint)p;
            for (ulong request = 10; request < 110; request++)
            {
                var result = host.Submit(peer, Intent(request), _ =>
                {
                    int current = Interlocked.Increment(ref active);
                    maxActive = Math.Max(maxActive, current);
                    Thread.SpinWait(1000);
                    Interlocked.Decrement(ref active);
                    return true;
                });
                if (result.Kind != DecisionKind.Accepted) throw new Exception("Concurrent command rejected.");
                sequences.Add(result.Commit!.Sequence);
            }
        });
        Check(maxActive == 1 && sequences.Distinct().Count() == 400 && host.Sequence == 401, "host plus three concurrent peers total order");
        Check(sequences.OrderBy(x => x).SequenceEqual(Enumerable.Range(2, 400).Select(x => (ulong)x)), "no sequence gaps");
        Check(!host.MarkReady(2, 0), "stale snapshot refused");
        Check(host.RemovePeer(3) && host.RegisterPeer(4) && !host.MarkReady(4, 0) && host.MarkReady(4, host.Sequence), "fresh reconnect boundary");
        Reject(() => host.Submit(0, Intent(111), _ => throw new InvalidOperationException()), "apply exception");
        Check(host.Faulted && host.Sequence == 401, "host faults on uncertain mutation");
        Reject(() => host.Submit(1, Intent(112), _ => true), "faulted host refuses work");
    }
    static void Client()
    {
        var client = new ClientCommitTracker(Epoch);
        var first = new Frame(FrameKind.Commit, Epoch, 1, 1, new byte[] { 7 });
        int count = 0;
        Check(client.Apply(first, _ => count++) && !client.Apply(first, _ => count++) && count == 1, "client dedup");
        Reject(() => client.Apply(new Frame(FrameKind.Commit, Epoch, 2, 3, new byte[1]), _ => count++), "gap");
        Check(client.Faulted && count == 1 && client.Sequence == 1, "gap fail closed");
        Reject(() => client.Apply(new Frame(FrameKind.Commit, Epoch, 2, 2, new byte[1]), _ => count++), "cannot resume after gap");
        var wrongEpoch = new ClientCommitTracker(Epoch);
        Reject(() => wrongEpoch.Apply(new Frame(FrameKind.Commit, Guid.NewGuid(), 1, 1, new byte[1]), _ => { }), "wrong epoch");
        var conflicting = new ClientCommitTracker(Epoch);
        conflicting.Apply(first, _ => { });
        Reject(() => conflicting.Apply(new Frame(FrameKind.Commit, Epoch, 1, 1, new byte[] { 8 }), _ => { }), "conflicting commit replay");
        var failing = new ClientCommitTracker(Epoch, 12);
        Reject(() => failing.Apply(new Frame(FrameKind.Commit, Epoch, 2, 13, new byte[1]), _ => throw new Exception()), "client application failure");
        Check(failing.Faulted && failing.Sequence == 12, "failed apply does not advance");
    }
    static Frame Chunk(Guid id, byte[] data, int index, byte[]? hash = null, ulong sequence = 5)
    {
        int offset = index * SnapshotChunkCodec.ChunkSize;
        var chunk = new SnapshotChunk(id, data.Length, index, hash ?? SHA256.HashData(data),
            data.AsSpan(offset, Math.Min(SnapshotChunkCodec.ChunkSize, data.Length - offset)));
        return new Frame(FrameKind.SnapshotChunk, Epoch, 0, sequence, SnapshotChunkCodec.Encode(chunk));
    }
    static void Snapshots()
    {
        var bytes = new byte[SnapshotChunkCodec.ChunkSize + 27];
        new Random(42).NextBytes(bytes);
        var id = Guid.NewGuid();
        using var assembler = new SnapshotAssembler(Epoch, bytes.Length, bytes.Length * 2, 100, 300);
        Check(assembler.RegisterPeer(1) && assembler.RegisterPeer(2) && assembler.RegisterPeer(3) && !assembler.RegisterPeer(4), "snapshot peer cap");
        Check(assembler.Add(1, Chunk(id, bytes, 0), 0) == null && assembler.BufferedBytes == bytes.Length, "first chunk allocates bounded transfer");
        Check(assembler.Add(1, Chunk(id, bytes, 0), 1) == null, "duplicate chunk idempotent");
        var result = assembler.Add(1, Chunk(id, bytes, 1), 2);
        Check(result != null && result.Sequence == 5 && result.Data.Span.SequenceEqual(bytes) && assembler.BufferedBytes == 0, "verified snapshot completion");
        Reject(() => assembler.Add(1, Chunk(id, bytes, 1), 3), "missing first chunk");
        assembler.Add(1, Chunk(id, bytes, 0), 4);
        Reject(() => assembler.Add(1, Chunk(Guid.NewGuid(), bytes, 1), 5), "changed transfer ID");
        Check(assembler.BufferedBytes == 0, "invalid transfer releases memory");
        assembler.Add(1, Chunk(id, bytes, 0, new byte[32]), 6);
        Reject(() => assembler.Add(1, Chunk(id, bytes, 1, new byte[32]), 7), "SHA mismatch");
        assembler.Add(1, Chunk(id, bytes, 0), 8);
        assembler.Add(2, Chunk(id, bytes, 0), 9);
        Reject(() => assembler.Add(3, Chunk(id, bytes, 0), 10), "aggregate memory budget");
        Check(assembler.BufferedBytes == bytes.Length * 2, "quota failure preserves other transfers");
        Check(assembler.Expire(108).SequenceEqual(new uint[] { 1 }), "idle timeout exact boundary");
        Check(assembler.Expire(109).SequenceEqual(new uint[] { 2 }) && assembler.BufferedBytes == 0, "all expired released");
        assembler.Add(1, Chunk(id, bytes, 0), 110);
        assembler.RemovePeer(1);
        Check(assembler.BufferedBytes == 0, "disconnect releases transfer");
        Reject(() => assembler.Add(1, Chunk(id, bytes, 0), 111), "removed peer cannot send");
        assembler.Add(2, Chunk(id, bytes, 0), 112);
        Reject(() => assembler.Add(2, Chunk(id, bytes, 1, sequence: 6), 113), "snapshot sequence changed");
        var badChunk = SnapshotChunkCodec.Encode(new SnapshotChunk(id, bytes.Length, 0, SHA256.HashData(bytes), bytes.AsSpan(0, SnapshotChunkCodec.ChunkSize)));
        BinaryPrimitives.WriteInt32LittleEndian(badChunk.AsSpan(20), int.MaxValue);
        Reject(() => SnapshotChunkCodec.Decode(badChunk), "index overflow");
        Reject(() => SnapshotChunkCodec.Decode(new byte[56]), "missing chunk data");
        assembler.Add(2, Chunk(id, bytes, 0), 114);
        Reject(() => assembler.Add(2, Chunk(id, bytes, 1), 214), "late chunk rejected");
        Check(assembler.BufferedBytes == 0, "timeout on receive releases transfer");
    }
}
