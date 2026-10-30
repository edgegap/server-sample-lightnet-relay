using System;
using FishNet.Managing;
using UnityEngine;

namespace Tanks.FishNetRelay
{
    /// <summary>Starts and stops FishNet on the LightNet relay (sits on the FishNet NetworkManager).</summary>
    public class FishNetSession : MonoBehaviour, ITanksSession
    {
        NetworkManager manager;
        FishNetRelayTransport transport;

        void Awake()
        {
            manager = GetComponent<NetworkManager>();
            transport = GetComponent<FishNetRelayTransport>();
        }

        public Netcode Netcode => Netcode.FishNet;

        public void StartHost(TanksSettings relay, Action<string> onRoomCode)
        {
            Configure(relay);
            transport.OnRoomCreated = onRoomCode;
            manager.ServerManager.StartConnection();
            manager.ClientManager.StartConnection();
        }

        public void StartClient(TanksSettings relay, string code)
        {
            Configure(relay);
            transport.roomCode = code;
            manager.ClientManager.StartConnection();
        }

        public void Stop()
        {
            if (manager.ClientManager.Started) manager.ClientManager.StopConnection();
            if (manager.ServerManager.Started) manager.ServerManager.StopConnection(true);
        }

        public bool IsServer => manager.IsServerStarted;
        public bool IsActive => manager.IsServerStarted || manager.IsClientStarted || transport.GetConnectionState(false) != FishNet.Transporting.LocalConnectionState.Stopped;
        public string RoomCode => transport.RoomCode;

        void Configure(TanksSettings relay)
        {
            transport.relayAddress = relay.relayAddress;
            transport.relayTcpPort = relay.relayTcpPort;
            transport.relayUdpPort = relay.relayUdpPort;
        }
    }
}
