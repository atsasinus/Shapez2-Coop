using System;
using System.Collections.Generic;

namespace Shapez2Coop.Protocol
{
    public sealed class ByteBudgetQueue<T>
    {
        private readonly Queue<(T Value, int Bytes)> entries = new Queue<(T, int)>();
        private readonly object gate = new object();
        private readonly int maxItems;
        private readonly long maxBytes;
        private long bytes;
        public ByteBudgetQueue(int maxItems, long maxBytes)
        {
            if (maxItems < 1 || maxBytes < 1) throw new ArgumentOutOfRangeException();
            this.maxItems=maxItems; this.maxBytes=maxBytes;
        }
        public int Count { get { lock(gate) return entries.Count; } }
        public long BufferedBytes { get { lock(gate) return bytes; } }
        public bool TryEnqueue(T value, int weight)
        {
            if (weight < 0) throw new ArgumentOutOfRangeException(nameof(weight));
            lock(gate)
            {
                if (entries.Count >= maxItems || weight > maxBytes-bytes) return false;
                entries.Enqueue((value,weight)); bytes+=weight; return true;
            }
        }
        public bool TryDequeue(out T value)
        {
            lock(gate)
            {
                if (entries.Count==0) { value=default!; return false; }
                var item=entries.Dequeue(); bytes-=item.Bytes; value=item.Value; return true;
            }
        }
        public void Clear() { lock(gate) {entries.Clear();bytes=0;} }
    }
}
