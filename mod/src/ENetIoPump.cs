using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using ENet;
using Shapez2Coop.Protocol;

namespace Shapez2Multiplayer
{
    // Sole native-host owner after construction. No Unity/game API on this thread.
    internal sealed class ENetIoPump : IDisposable
    {
        internal sealed class Incoming
        {
            public EventType Type;
            public ulong Key;
            public uint Slot;
            public byte[]? Data;
        }
        private sealed class Command { public ulong Key; public byte[]? Data; }
        private readonly Host host;
        private readonly Thread thread;
        private readonly ByteBudgetQueue<Incoming> incoming = new ByteBudgetQueue<Incoming>(4096,64L*1024*1024);
        private readonly ByteBudgetQueue<Command> outgoing = new ByteBudgetQueue<Command>(4096,64L*1024*1024);
        private readonly Dictionary<ulong,Peer> peers = new Dictionary<ulong,Peer>();
        private readonly Dictionary<uint,ulong> keys = new Dictionary<uint,ulong>();
        private readonly ConcurrentDictionary<ulong,int> pings = new ConcurrentDictionary<ulong,int>();
        private ulong nextKey;
        private volatile bool stopping;
        private string? failure;
        private readonly object budgetGate=new object();
        private readonly Dictionary<ulong,long> reservedByPeer=new Dictionary<ulong,long>();
        private long reservedBytes;
        private int reservedItems;
        private readonly PacketFreeCallback packetFreed;
        private sealed class Lease {public ulong Key;public int Weight;}
        public string? Failure => Volatile.Read(ref failure);
        public ENetIoPump(Host host, Peer? initial = null)
        {
            this.host=host;
            packetFreed=PacketFreed;
            if(initial.HasValue) {peers.Add(1,initial.Value);keys.Add(initial.Value.ID,1);nextKey=1;}
            thread=new Thread(Run) {IsBackground=true,Name="Shapez2 ENet I/O"}; thread.Start();
        }
        public int Ping(ulong key) => pings.TryGetValue(key,out var ping)?ping:0;
        public bool Send(ulong key, byte[] bytes)
        {
            int weight=checked(bytes.Length+64);
            lock(budgetGate)
            {
                reservedByPeer.TryGetValue(key,out var peerBytes);
                if(stopping || Failure!=null || reservedItems>=4096 || weight>64L*1024*1024-reservedBytes || weight>32L*1024*1024-peerBytes)return false;
                reservedByPeer[key]=peerBytes+weight;reservedBytes+=weight;reservedItems++;
                if(outgoing.TryEnqueue(new Command {Key=key,Data=bytes},weight))return true;
                Release(key,weight);return false;
            }
        }
        private void Release(ulong key,int weight)
        {
            lock(budgetGate){reservedBytes-=weight;reservedItems--;reservedByPeer[key]-=weight;if(reservedByPeer[key]==0)reservedByPeer.Remove(key);}
        }
        private void PacketFreed(ENet.Packet packet)
        {
            var handle=GCHandle.FromIntPtr(packet.UserData);
            var lease=(Lease)handle.Target!;handle.Free();Release(lease.Key,lease.Weight);
        }
        public void Close(ulong key)
        {
            if(!stopping && !outgoing.TryEnqueue(new Command {Key=key},64))
                Interlocked.CompareExchange(ref failure,"Transport command queue exhausted",null);
        }
        public bool Poll(out Incoming value) => incoming.TryDequeue(out value);
        private void Publish(Incoming value)
        {
            if(!incoming.TryEnqueue(value,checked((value.Data?.Length??0)+64)))
                throw new InvalidOperationException("Transport receive queue exhausted");
        }
        private void Handle(Event ev)
        {
            if(ev.Type==EventType.None)return;
            uint slot=ev.Peer.ID;
            if(ev.Type==EventType.Connect)
            {
                if(!keys.TryGetValue(slot,out var key)) {key=++nextKey;keys[slot]=key;peers[key]=ev.Peer;}
                ev.Peer.Timeout(32,15000,60000); ev.Peer.PingInterval(1000);
                Publish(new Incoming {Type=ev.Type,Key=key,Slot=slot}); return;
            }
            if(!keys.TryGetValue(slot,out var known))
            { if(ev.Type==EventType.Receive) ev.Packet.Dispose(); return; }
            if(ev.Type==EventType.Receive)
            {
                try
                {
                    if(ev.Packet.Length==0 || ev.Packet.Length>512*1024)
                    {ev.Peer.Disconnect(0);Publish(new Incoming {Type=EventType.Disconnect,Key=known,Slot=slot});keys.Remove(slot);peers.Remove(known);return;}
                    var bytes=new byte[ev.Packet.Length];ev.Packet.CopyTo(bytes);
                    Publish(new Incoming {Type=ev.Type,Key=known,Slot=slot,Data=bytes});
                }
                finally {ev.Packet.Dispose();}
            }
            else if(ev.Type==EventType.Disconnect || ev.Type==EventType.Timeout)
            {
                Publish(new Incoming {Type=ev.Type,Key=known,Slot=slot});
                keys.Remove(slot);peers.Remove(known);pings.TryRemove(known,out _);
            }
        }
        private void DrainCommands()
        {
            int budget=256;
            while(budget-->0 && outgoing.TryDequeue(out var command))
            {
                if(!peers.TryGetValue(command.Key,out var peer)) {if(command.Data!=null)Release(command.Key,command.Data.Length+64);continue;}
                if(command.Data==null) {peer.Disconnect(0);peers.Remove(command.Key);keys.Remove(peer.ID);pings.TryRemove(command.Key,out _);continue;}
                var packet=new ENet.Packet(); packet.Create(command.Data,PacketFlags.Reliable);
                var handle=GCHandle.Alloc(new Lease {Key=command.Key,Weight=command.Data.Length+64});
                packet.UserData=GCHandle.ToIntPtr(handle);packet.SetFreeCallback(packetFreed);
                if(!peer.Send(0,ref packet))
                {
                    packet.Dispose();Publish(new Incoming {Type=EventType.Disconnect,Key=command.Key,Slot=peer.ID});
                    peer.Disconnect(0);peers.Remove(command.Key);keys.Remove(peer.ID);pings.TryRemove(command.Key,out _);
                }
            }
        }
        private void Run()
        {
            try
            {
                long nextPing=0;
                while(!stopping)
                {
                    DrainCommands();
                    int budget=256;
                    while(budget-->0 && host.CheckEvents(out var queued)>0) Handle(queued);
                    if(host.Service(1,out var ev)>0) Handle(ev);
                    long now=System.Diagnostics.Stopwatch.GetTimestamp()*1000/System.Diagnostics.Stopwatch.Frequency;
                    if(now>=nextPing)
                    {foreach(var pair in peers)pings[pair.Key]=(int)pair.Value.RoundTripTime;nextPing=now+100;}
                }
                DrainCommands();
                foreach(var peer in peers.Values) peer.Disconnect(0);
                host.Flush();
            }
            catch(Exception ex) {Interlocked.CompareExchange(ref failure,ex.GetType().Name+": "+ex.Message,null);}
            finally
            {
                lock(budgetGate)stopping=true;
                host.Dispose();
                while(outgoing.TryDequeue(out var command))if(command.Data!=null)Release(command.Key,command.Data.Length+64);
                GC.KeepAlive(packetFreed);
            }
        }
        public void Dispose()
        {
            lock(budgetGate)stopping=true;
            if(!thread.Join(2000)) throw new InvalidOperationException("ENet I/O thread did not stop");
            incoming.Clear();outgoing.Clear();
            lock(budgetGate)if(reservedBytes!=0 || reservedItems!=0)throw new InvalidOperationException("ENet packet reservation leak");
        }
    }
}
