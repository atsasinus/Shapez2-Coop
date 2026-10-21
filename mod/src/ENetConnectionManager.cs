using ENet;
using Shapez2Multiplayer.Packets;
using Steamworks;
using System;
using static Shapez2Multiplayer.MultiplayerCore;

namespace Shapez2Multiplayer
{
    public sealed class ENetConnectionManager : IConnectionManager
    {
        private readonly ENetIoPump pump;
        private readonly ENetConnection connection;
        private bool closed;
        public bool Connected {get;private set;}
        public IConnection Connection => connection;
        public event Action<DisconnectReason> DisconnectedEvent;
        public event Action ConnectedEvent;
        public event Action<byte[]> MessageEvent;
        public ENetConnectionManager(Host host,Peer peer)
        {
            uint slot=peer.ID;
            pump=new ENetIoPump(host,peer);
            connection=new ENetConnection(pump,1,slot);
        }
        public void Close()
        {if(closed)return;closed=true;Connected=false;connection.MarkClosed();pump.Dispose();}
        public void Update()
        {
            if(closed)return;
            if(pump.Failure!=null){CoopSession.Fail(pump.Failure);Close();return;}
            int budget=256;
            while(!closed && budget-->0 && CoopSession.CanReceive && pump.Poll(out var ev))
            {
                if(ev.Type==EventType.Connect)
                {
                    Connected=true;
                    if(SteamClient.IsValid)Connection.Send(PacketExtensions.Encode(new PlayerInfoPacket(SteamClient.Name))!,Packets.Packet.PlayerInfo);
                    ConnectedEvent?.Invoke();
                }
                else if(ev.Type==EventType.Receive)MessageEvent?.Invoke(ev.Data!);
                else
                {
                    Connected=false;connection.MarkClosed();
                    var reason=ev.Type==EventType.Timeout?DisconnectReason.Timedout:DisconnectReason.Lostconnection;
                    Shapez2Multiplayer.logger.Warning?.Log("ENet host disconnected reason="+reason+" step="+CoopSession.StepCount);
                    DisconnectedEvent?.Invoke(reason);
                }
            }
        }
    }
}
