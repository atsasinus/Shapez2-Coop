using System;
using System.Collections.Generic;

namespace Shapez2Coop.Protocol
{
    // Stores presentation data only. An unrelated player's equal request number
    // must never confirm this client's pending visual.
    public sealed class PredictionLedger<T>
    {
        private readonly Dictionary<ulong, T> entries = new Dictionary<ulong, T>();
        public uint LocalPlayer { get; }
        public int Capacity { get; }
        public int Count => entries.Count;
        public IEnumerable<T> Values => entries.Values;
        public bool Contains(ulong request) => entries.ContainsKey(request);
        public PredictionLedger(uint localPlayer, int capacity = 64)
        {
            if (capacity < 1 || capacity > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
            LocalPlayer = localPlayer; Capacity = capacity;
        }
        public bool TryAdd(ulong request, T visual)
        {
            if (request == 0 || entries.Count >= Capacity || entries.ContainsKey(request)) return false;
            entries.Add(request, visual); return true;
        }
        public bool Confirm(uint origin, ulong request, out T visual)
        {
            if (origin == LocalPlayer) return Reject(request, out visual);
            visual = default!; return false;
        }
        public bool Reject(ulong request, out T visual)
        {
            if (!entries.TryGetValue(request, out visual!)) return false;
            entries.Remove(request); return true;
        }
        public void Clear() => entries.Clear();
    }
}
