using ENet;
using System;
using System.Collections.Generic;

namespace Shapez2Multiplayer
{
    public sealed class ENetSocketManager : ISocketManager
    {
        private readonly ENetIoPump pump;
        private readonly Dictionary<ulong,ENetConnection> connections=new Dictionary<ulong,ENetConnection>();
        public bool Valid {get;private set;}=true;
        public IReadOnlyCollection<IConnection> Connected => connections.Values;
        public event Action<IConnection> DisconnectedEvent;
        public event Action<IConnection> ConnectedEvent;
        public event Action<IConnection,byte[]> MessageEvent;
        public ENetSocketManager(Host host) {pump=new ENetIoPump(host);}
        public void Close()
        {
            if(!Valid)return;Valid=false;
            foreach(var peer in connections.Values)peer.MarkClosed();
            pump.Dispose();connections.Clear();
        }
        public void Update()
        {
            if(!Valid)return;
            if(pump.Failure!=null){CoopSession.Fail(pump.Failure);Close();return;}
            int budget=256;
            while(Valid && budget-->0 && CoopSession.CanReceive && pump.Poll(out var ev))
            {
                if(ev.Type==EventType.Connect)
                {
                    var peer=new ENetConnection(pump,ev.Key,ev.Slot);
                    ulong key=ev.Key;peer.ClosedLocally=()=>Detach(key,peer);
                    connections.Add(ev.Key,peer);ConnectedEvent?.Invoke(peer);
                }
                else if(connections.TryGetValue(ev.Key,out var peer))
                {
                    if(ev.Type==EventType.Receive)MessageEvent?.Invoke(peer,ev.Data!);
                    else
                    {
                        Shapez2Multiplayer.logger.Warning?.Log("ENet peer "+ev.Type+" id="+ev.Slot+" step="+CoopSession.StepCount);
                        Detach(ev.Key,peer);
                    }
                }
            }
        }
        private void Detach(ulong key,ENetConnection peer)
        {if(!connections.Remove(key))return;peer.MarkClosed();DisconnectedEvent?.Invoke(peer);}
    }
}
